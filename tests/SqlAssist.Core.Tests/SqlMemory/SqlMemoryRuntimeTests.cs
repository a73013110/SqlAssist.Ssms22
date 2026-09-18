using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Core.Settings;
using Xunit;
using static SqlAssist.Core.Tests.SqlMemory.SqlMemoryTestData;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryRuntimeTests
{
    private static readonly SqlMemoryLeaseOwner Owner = new("LIBRARYPC", 4242, Start.AddHours(-1));

    private static readonly SqlMemoryConfiguration Enabled =
        SqlMemoryConfiguration.From(new SqlAssistSettings { SqlMemoryEnabled = true });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Harness
    {
        private DateTimeOffset _now = Start;

        public Harness(SqlMemoryRuntimeOptions? options = null, Func<CancellationToken, Task<ISqlMemoryStore>>? open = null)
        {
            Runtime = new SqlMemoryRuntime(open ?? (_ => { Opens++; return Task.FromResult<ISqlMemoryStore>(Store); }),
                () => Owner, new SqlMemoryLeaseReaper("LIBRARYPC", _ => true), Timers, Log, () => _now,
                options ?? new SqlMemoryRuntimeOptions { StartupDelay = TimeSpan.Zero });
            Runtime.StatusChanged += (_, status) => { lock (Statuses) Statuses.Add(status); };
            Runtime.CaptureDropped += (_, drop) => { lock (Drops) Drops.Add(drop); };
            Runtime.Start();
        }

        public SqlMemoryRuntime Runtime { get; }
        public FakeSqlMemoryStore Store { get; } = new();
        public ManualSqlMemoryTimers Timers { get; } = new();
        public RecordingSqlMemoryLog Log { get; } = new();
        public List<SqlMemoryRuntimeStatus> Statuses { get; } = new();
        public List<SqlCaptureDroppedEventArgs> Drops { get; } = new();
        public int Opens { get; set; }

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    [Fact]
    public async Task EnablingOpensTheStorageOnceAndPublishesANewGeneration()
    {
        var harness = new Harness();
        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(SqlMemoryRuntimePhase.Disabled, harness.Runtime.Status.Phase);

        await harness.Runtime.ApplyAsync(Enabled);

        Assert.True(harness.Runtime.IsAvailable);
        Assert.Equal(SqlMemoryRuntimePhase.Ready, harness.Runtime.Status.Phase);
        Assert.Equal(1, harness.Runtime.Generation);
        Assert.Equal("", harness.Runtime.Status.Message);
        Assert.Equal(new[] { SqlMemoryRuntimePhase.Opening, SqlMemoryRuntimePhase.Ready },
            harness.Statuses.Select(status => status.Phase));

        // 只換設定不重開儲存，也不換世代；舊畫面的回應仍然有效。
        await harness.Runtime.ApplyAsync(SqlMemoryConfiguration.From(new SqlAssistSettings
        {
            SqlMemoryEnabled = true, SqlMemoryIdleSeconds = 9,
        }));
        Assert.Equal(1, harness.Opens);
        Assert.Equal(1, harness.Runtime.Generation);
        Assert.Equal(TimeSpan.FromSeconds(9), harness.Runtime.IdleDebounce);
    }

    [Fact]
    public async Task DisablingDrainsReleasesTheMaintenanceLeaseAndRejectsLaterReads()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);
        Assert.Equal(SqlCaptureEnqueueResult.Accepted, harness.Runtime.TryEnqueue(Capture()));

        await harness.Runtime.ApplyAsync(SqlMemoryConfiguration.Disabled);

        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(2, harness.Runtime.Generation);
        // 已接受的擷取先提交才釋放儲存。
        Assert.Single(harness.Store.Captures.Writes);
        Assert.Equal(1, harness.Store.Disposals);
        Assert.Equal(1, harness.Store.Maintenance.MaintenanceLeaseReleases);
        Assert.Equal(SqlCaptureEnqueueResult.Stopped, harness.Runtime.TryEnqueue(Capture(2)));
        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() =>
            harness.Runtime.ReadContentAsync("sha256:missing", Token));
        Assert.Equal(SqlMemoryStorageErrorKind.Unavailable, error.Kind);
    }

    /// <summary>連續切換開關時收斂到最後一份設定；排隊中的轉換不會把已關掉的儲存再掛回去。</summary>
    [Fact]
    public async Task TogglingWhileTheStorageIsStillOpeningConvergesOnTheLatestConfiguration()
    {
        var opening = new TaskCompletionSource<ISqlMemoryStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new FakeSqlMemoryStore();
        var harness = new Harness(open: _ => opening.Task);

        var enable = harness.Runtime.ApplyAsync(Enabled);
        var disable = harness.Runtime.ApplyAsync(SqlMemoryConfiguration.Disabled);
        Assert.False(harness.Runtime.IsAvailable);
        opening.SetResult(storage);
        await Task.WhenAll(enable, disable).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(SqlMemoryRuntimePhase.Disabled, harness.Runtime.Status.Phase);
        Assert.Equal(1, storage.Disposals);
    }

    [Fact]
    public async Task AnOpenFailureIsVisibleClassifiedAndRetriedByTheNextSettingsChange()
    {
        var attempts = 0;
        var storage = new FakeSqlMemoryStore();
        var harness = new Harness(open: _ => ++attempts == 1
            ? throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Incompatible, "schema 版本不相容")
            : Task.FromResult<ISqlMemoryStore>(storage));

        var failure = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => harness.Runtime.ApplyAsync(Enabled));

        Assert.Equal(SqlMemoryStorageErrorKind.Incompatible, failure.Kind);
        Assert.Equal(SqlMemoryRuntimePhase.OpenFailed, harness.Runtime.Status.Phase);
        Assert.Equal(SqlMemoryStorageErrorKind.Incompatible, harness.Runtime.Status.ErrorKind);
        Assert.Contains("不相容", harness.Runtime.Status.Message);
        Assert.False(harness.Runtime.IsCapturing);

        await harness.Runtime.ApplyAsync(Enabled);
        Assert.True(harness.Runtime.IsAvailable);
        Assert.Equal(2, attempts);
    }

    /// <summary>寫入器致命失敗時宿主必須察覺並停用，不能把排空當成保存成功。</summary>
    [Fact]
    public async Task AFaultedWriterStopsCapturingAndDisposesTheStorage()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);
        var failed = new TaskCompletionSource<SqlMemoryRuntimeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.StatusChanged += (_, status) =>
        {
            if (status.Phase == SqlMemoryRuntimePhase.WriterFailed) failed.TrySetResult(status);
        };
        harness.Store.Captures.CommitException = new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Corrupt, "頁面損毀");

        harness.Runtime.TryEnqueue(Capture());
        var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(2, status.Generation);
        Assert.Contains("寫入失敗", status.Message);
        Assert.False(harness.Runtime.IsCapturing);
        await WaitUntil(() => harness.Store.Disposals == 1);
        Assert.Contains(harness.Log.Messages, message => message.Contains("背景寫入器已停止"));
    }

    [Fact]
    public async Task ARejectedCaptureIsVisibleInTheStatusAndNotifiesEveryTimeWhenTheSnapshotIsTooLarge()
    {
        var harness = new Harness(new SqlMemoryRuntimeOptions { StartupDelay = TimeSpan.Zero, MaximumPendingTextBytes = 8 });
        await harness.Runtime.ApplyAsync(Enabled);

        Assert.Equal(SqlCaptureEnqueueResult.SnapshotTooLarge, harness.Runtime.TryEnqueue(Capture()));
        Assert.Equal(SqlCaptureEnqueueResult.SnapshotTooLarge, harness.Runtime.TryEnqueue(Capture(2)));

        Assert.Equal(SqlCaptureDrop.SnapshotTooLarge, harness.Runtime.Status.LastDrop);
        Assert.Equal("這份 SQL 太大，本次未記錄。", harness.Runtime.Status.Message);
        Assert.Equal(new[] { true, true }, harness.Drops.Select(drop => drop.ShouldNotify));
        // 工具窗那一行完整狀態與通知上的原因短語是兩份文案，各自回答不同的問題。
        Assert.Equal(new[] { "這份 SQL 太大", "這份 SQL 太大" }, harness.Drops.Select(drop => drop.Reason));
        // 換設定之後重新回到乾淨的狀態列。
        await harness.Runtime.ApplyAsync(Enabled);
        Assert.Null(harness.Runtime.Status.LastDrop);
    }

    /// <summary>心跳開出來的租約明確交給維護排除，也明確隨每次提交寫入 Session。</summary>
    [Fact]
    public async Task TheHeartbeatLeaseFlowsExplicitlyIntoMaintenanceAndCommits()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);

        await harness.Timers.Maintain();
        Assert.Equal(new string?[] { null }, harness.Store.Maintenance.ExcludedLeaseIds);

        await harness.Timers.Beat();
        Assert.Equal(1, harness.Store.Maintenance.Opens);
        harness.Advance(TimeSpan.FromHours(2));
        await harness.Timers.Maintain();
        Assert.Equal(new string?[] { null, "lease-1" }, harness.Store.Maintenance.ExcludedLeaseIds);

        harness.Runtime.TryEnqueue(Capture());
        await harness.Runtime.ApplyAsync(SqlMemoryConfiguration.Disabled);
        Assert.Equal(new string?[] { "lease-1" }, harness.Store.Captures.LeaseIds);
    }

    [Fact]
    public async Task MaintenanceWaitsForTheHostToBeIdleBeforeTheIntervalElapses()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);
        await harness.Timers.Maintain();
        Assert.Single(harness.Store.Maintenance.Requests);

        // 間隔未到、剛剛有編輯：不跑。閒置超過門檻而且過了最小間隔：提前跑。
        harness.Advance(TimeSpan.FromMinutes(20));
        harness.Runtime.NoteEdit();
        await harness.Timers.Maintain();
        Assert.Single(harness.Store.Maintenance.Requests);
        harness.Advance(TimeSpan.FromMinutes(3));
        await harness.Timers.Maintain();
        Assert.Equal(2, harness.Store.Maintenance.Requests.Count);
    }

    [Fact]
    public async Task ShutdownStopsTimersClosesTheStorageAndIgnoresLaterSettings()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);

        Assert.True(await harness.Runtime.ShutdownAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, harness.Timers.Disposed);
        Assert.Equal(1, harness.Store.Disposals);
        await harness.Runtime.ApplyAsync(Enabled);
        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(1, harness.Opens);
        Assert.True(await harness.Runtime.ShutdownAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>開檔卡住時關閉 SSMS 仍受時限約束；晚到的儲存不會被掛上去，而是直接釋放。</summary>
    [Fact]
    public async Task ShutdownGivesUpOnAStuckOpenAndTheLateStorageIsNeverAttached()
    {
        var opening = new TaskCompletionSource<ISqlMemoryStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new FakeSqlMemoryStore();
        var harness = new Harness(open: _ => opening.Task);
        var enable = harness.Runtime.ApplyAsync(Enabled);

        Assert.False(await harness.Runtime.ShutdownAsync(TimeSpan.FromMilliseconds(50)));
        opening.SetResult(storage);
        await enable.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(1, storage.Disposals);
    }

    /// <summary>關閉途中還沒回來的讀取被取消，並以分類例外告訴畫面「重新整理」，不是儲存故障。</summary>
    [Fact]
    public async Task AReadInterruptedByClosingReportsUnavailableInsteadOfCancellation()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);
        harness.Store.BlockReads = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = harness.Runtime.ReadContentAsync("sha256:any", Token);

        await harness.Runtime.ApplyAsync(SqlMemoryConfiguration.Disabled);

        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => read);
        Assert.Equal(SqlMemoryStorageErrorKind.Unavailable, error.Kind);
    }

    [Fact]
    public void ConfigurationFollowsBothSwitchesAndOnlyReclaimsRecoveryWhenRecoveryIsKept()
    {
        Assert.False(SqlMemoryConfiguration.Disabled.Enabled);
        Assert.False(SqlMemoryConfiguration.From(new SqlAssistSettings { Enabled = false, SqlMemoryEnabled = true }).Enabled);

        var kept = SqlMemoryConfiguration.From(new SqlAssistSettings { SqlMemoryEnabled = true, SqlMemoryRecoveryRetentionDays = 3 });
        Assert.True(kept.Enabled);
        Assert.True(kept.Policy.RecoveryEnabled);
        Assert.Equal(TimeSpan.FromDays(3), kept.Plan.RecoveryRetention);

        var dropped = SqlMemoryConfiguration.From(new SqlAssistSettings { SqlMemoryEnabled = true, SqlMemoryCaptureRecovery = false });
        Assert.False(dropped.Policy.RecoveryEnabled);
        Assert.Null(dropped.Plan.RecoveryRetention);

        var noDrafts = SqlMemoryConfiguration.From(new SqlAssistSettings { SqlMemoryEnabled = true, SqlMemoryCaptureDrafts = false });
        Assert.False(noDrafts.Policy.CaptureUnexecutedDrafts);
        Assert.False(noDrafts.Policy.RecoveryEnabled);
        Assert.Equal(TimeSpan.FromTicks(kept.MaintenanceInterval.Ticks / 4), kept.IdleMinimumGap);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10, Token);
        Assert.True(condition());
    }
    [Fact]
    public async Task ClosedRecoveryCleanupWaitsForTheHeartbeatAndEveryOutcomeIsRecorded()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);
        var request = new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.ClosedRecovery);

        // 沒有租約時分不出自己開著的視窗，必須拒絕，而且不能碰儲存。
        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => harness.Runtime.CleanupAsync(request, null, Token));
        Assert.Equal(SqlMemoryStorageErrorKind.Unavailable, error.Kind);
        Assert.Empty(harness.Store.Maintenance.CleanupRequests);

        await harness.Timers.Beat();
        Assert.True(harness.Runtime.MaintenanceOverview.HeartbeatActive);
        await harness.Runtime.CleanupAsync(request, null, Token);
        Assert.Single(harness.Store.Maintenance.CleanupRequests);

        var activities = (await harness.Runtime.ReadUsageSnapshotAsync(Token)).Activities;
        Assert.Equal(new[] { true, false }, activities.Select(activity => activity.Succeeded));
        Assert.All(activities, activity => Assert.Equal(SqlMemoryActivityKind.Cleanup, activity.Kind));
    }

    [Fact]
    public async Task ScheduledMaintenanceUpdatesTheOverviewAndRaisesCapacityChanges()
    {
        var harness = new Harness();
        var changes = new List<SqlMemoryCapacityChangedEventArgs>();
        harness.Runtime.CapacityChanged += (_, change) => changes.Add(change);
        await harness.Runtime.ApplyAsync(SqlMemoryConfiguration.From(new SqlAssistSettings
        {
            SqlMemoryEnabled = true, SqlMemoryStorage = SqlMemoryStorageLimit.Megabytes256,
        }));
        harness.Store.Maintenance.Enqueue(new SqlMemoryMaintenanceResult(10, 7, null, false,
            new SqlMemoryUsage(250L * 1024 * 1024, 0, 0), SqlMemoryCapacityStatus.MoreWorkRequired));

        await harness.Timers.Maintain();

        Assert.Equal(Start, harness.Runtime.MaintenanceOverview.LastMaintainedAt);
        Assert.Equal(SqlMemoryUsageSeverity.Critical, harness.Runtime.CapacitySeverity);
        var change = Assert.Single(changes);
        Assert.True(change.Notify);
        var activity = Assert.Single((await harness.Runtime.ReadUsageSnapshotAsync(Token)).Activities);
        Assert.Equal((SqlMemoryActivityKind.ScheduledMaintenance, 7L), (activity.Kind, activity.DeletedRows));

        // 停用換掉儲存：分級回到 Normal，舊資料庫的警示不留在畫面上。
        await harness.Runtime.ApplyAsync(SqlMemoryConfiguration.Disabled);
        Assert.Equal(SqlMemoryUsageSeverity.Normal, harness.Runtime.CapacitySeverity);
        Assert.Equal(SqlMemoryUsageSeverity.Normal, changes.Last().Severity);
    }

    /// <summary>背景保留清理失敗要讓宿主看得見；擷取照常，下一輪重跑同一個游標。</summary>
    [Fact]
    public async Task ScheduledMaintenanceFailuresAreReportedToTheHostWithoutStoppingCapture()
    {
        var harness = new Harness();
        var failures = new List<string>();
        harness.Runtime.MaintenanceFailed += failures.Add;
        await harness.Runtime.ApplyAsync(Enabled);
        harness.Store.Maintenance.EnqueueFailure(SqlMemoryStorageErrorKind.Busy);

        await harness.Timers.Maintain();

        Assert.Contains("測試：Busy", Assert.Single(failures));
        Assert.True(harness.Runtime.IsCapturing);
        Assert.Equal(SqlCaptureEnqueueResult.Accepted, harness.Runtime.TryEnqueue(Capture()));

        // 關閉途中的取消不是失敗，不再送一次。
        await harness.Runtime.ApplyAsync(SqlMemoryConfiguration.Disabled);
        await harness.Timers.Maintain();
        Assert.Single(failures);
    }
}
