using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Core.Settings;
using Xunit;
using static SqlAssist.Core.Tests.QueryMemory.QueryMemoryTestData;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryRuntimeTests
{
    private static readonly QueryMemoryLeaseOwner Owner = new("LIBRARYPC", 4242, Start.AddHours(-1));

    private static readonly QueryMemoryConfiguration Enabled =
        QueryMemoryConfiguration.From(new SqlAssistSettings { QueryMemoryEnabled = true });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Harness
    {
        private DateTimeOffset _now = Start;

        public Harness(QueryMemoryRuntimeOptions? options = null, Func<CancellationToken, Task<IQueryMemoryStorage>>? open = null)
        {
            Runtime = new QueryMemoryRuntime(open ?? (_ => { Opens++; return Task.FromResult<IQueryMemoryStorage>(Storage); }),
                () => Owner, new QueryMemoryLeaseReaper("LIBRARYPC", _ => true), Timers, Log, () => _now,
                options ?? new QueryMemoryRuntimeOptions { StartupDelay = TimeSpan.Zero });
            Runtime.StatusChanged += (_, status) => { lock (Statuses) Statuses.Add(status); };
            Runtime.CaptureDropped += (_, drop) => { lock (Drops) Drops.Add(drop); };
            Runtime.Start();
        }

        public QueryMemoryRuntime Runtime { get; }
        public FakeQueryMemoryStorage Storage { get; } = new();
        public ManualQueryMemoryTimers Timers { get; } = new();
        public RecordingQueryMemoryLog Log { get; } = new();
        public List<QueryMemoryRuntimeStatus> Statuses { get; } = new();
        public List<QueryMemoryCaptureDroppedEventArgs> Drops { get; } = new();
        public int Opens { get; set; }

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    [Fact]
    public async Task EnablingOpensTheStorageOnceAndPublishesANewGeneration()
    {
        var harness = new Harness();
        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(QueryMemoryRuntimePhase.Disabled, harness.Runtime.Status.Phase);

        await harness.Runtime.ApplyAsync(Enabled);

        Assert.True(harness.Runtime.IsAvailable);
        Assert.Equal(QueryMemoryRuntimePhase.Ready, harness.Runtime.Status.Phase);
        Assert.Equal(1, harness.Runtime.Generation);
        Assert.Equal("", harness.Runtime.Status.Message);
        Assert.Equal(new[] { QueryMemoryRuntimePhase.Opening, QueryMemoryRuntimePhase.Ready },
            harness.Statuses.Select(status => status.Phase));

        // 只換設定不重開儲存，也不換世代；舊畫面的回應仍然有效。
        await harness.Runtime.ApplyAsync(QueryMemoryConfiguration.From(new SqlAssistSettings
        {
            QueryMemoryEnabled = true, QueryMemoryIdleSeconds = 9,
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
        Assert.Equal(QueryMemoryEnqueueResult.Accepted, harness.Runtime.TryEnqueue(Capture()));

        await harness.Runtime.ApplyAsync(QueryMemoryConfiguration.Disabled);

        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(2, harness.Runtime.Generation);
        // 已接受的擷取先提交才釋放儲存。
        Assert.Single(harness.Storage.Captures.Writes);
        Assert.Equal(1, harness.Storage.Disposals);
        Assert.Equal(1, harness.Storage.Maintenance.MaintenanceLeaseReleases);
        Assert.Equal(QueryMemoryEnqueueResult.Stopped, harness.Runtime.TryEnqueue(Capture(2)));
        var error = await Assert.ThrowsAsync<QueryMemoryStorageException>(() =>
            harness.Runtime.ReadContentAsync("sha256:missing", Token));
        Assert.Equal(QueryMemoryStorageErrorKind.Unavailable, error.Kind);
    }

    /// <summary>連續切換開關時收斂到最後一份設定；排隊中的轉換不會把已關掉的儲存再掛回去。</summary>
    [Fact]
    public async Task TogglingWhileTheStorageIsStillOpeningConvergesOnTheLatestConfiguration()
    {
        var opening = new TaskCompletionSource<IQueryMemoryStorage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new FakeQueryMemoryStorage();
        var harness = new Harness(open: _ => opening.Task);

        var enable = harness.Runtime.ApplyAsync(Enabled);
        var disable = harness.Runtime.ApplyAsync(QueryMemoryConfiguration.Disabled);
        Assert.False(harness.Runtime.IsAvailable);
        opening.SetResult(storage);
        await Task.WhenAll(enable, disable).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(QueryMemoryRuntimePhase.Disabled, harness.Runtime.Status.Phase);
        Assert.Equal(1, storage.Disposals);
    }

    [Fact]
    public async Task AnOpenFailureIsVisibleClassifiedAndRetriedByTheNextSettingsChange()
    {
        var attempts = 0;
        var storage = new FakeQueryMemoryStorage();
        var harness = new Harness(open: _ => ++attempts == 1
            ? throw new QueryMemoryStorageException(QueryMemoryStorageErrorKind.Incompatible, "schema 版本不相容")
            : Task.FromResult<IQueryMemoryStorage>(storage));

        var failure = await Assert.ThrowsAsync<QueryMemoryStorageException>(() => harness.Runtime.ApplyAsync(Enabled));

        Assert.Equal(QueryMemoryStorageErrorKind.Incompatible, failure.Kind);
        Assert.Equal(QueryMemoryRuntimePhase.OpenFailed, harness.Runtime.Status.Phase);
        Assert.Equal(QueryMemoryStorageErrorKind.Incompatible, harness.Runtime.Status.ErrorKind);
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
        var failed = new TaskCompletionSource<QueryMemoryRuntimeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runtime.StatusChanged += (_, status) =>
        {
            if (status.Phase == QueryMemoryRuntimePhase.WriterFailed) failed.TrySetResult(status);
        };
        harness.Storage.Captures.CommitException = new QueryMemoryStorageException(QueryMemoryStorageErrorKind.Corrupt, "頁面損毀");

        harness.Runtime.TryEnqueue(Capture());
        var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(2, status.Generation);
        Assert.Contains("寫入失敗", status.Message);
        Assert.False(harness.Runtime.IsCapturing);
        await WaitUntil(() => harness.Storage.Disposals == 1);
        Assert.Contains(harness.Log.Messages, message => message.Contains("背景寫入器已停止"));
    }

    [Fact]
    public async Task ARejectedCaptureIsVisibleInTheStatusAndNotifiesEveryTimeWhenTheSnapshotIsTooLarge()
    {
        var harness = new Harness(new QueryMemoryRuntimeOptions { StartupDelay = TimeSpan.Zero, MaximumPendingTextBytes = 8 });
        await harness.Runtime.ApplyAsync(Enabled);

        Assert.Equal(QueryMemoryEnqueueResult.SnapshotTooLarge, harness.Runtime.TryEnqueue(Capture()));
        Assert.Equal(QueryMemoryEnqueueResult.SnapshotTooLarge, harness.Runtime.TryEnqueue(Capture(2)));

        Assert.Equal(QueryMemoryCaptureDrop.SnapshotTooLarge, harness.Runtime.Status.LastDrop);
        Assert.Equal("這份 SQL 太大，本次未記錄。", harness.Runtime.Status.Message);
        Assert.Equal(new[] { true, true }, harness.Drops.Select(drop => drop.ShouldNotify));
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
        Assert.Equal(new string?[] { null }, harness.Storage.Maintenance.ExcludedLeaseIds);

        await harness.Timers.Beat();
        Assert.Equal(1, harness.Storage.Maintenance.Opens);
        harness.Advance(TimeSpan.FromHours(2));
        await harness.Timers.Maintain();
        Assert.Equal(new string?[] { null, "lease-1" }, harness.Storage.Maintenance.ExcludedLeaseIds);

        harness.Runtime.TryEnqueue(Capture());
        await harness.Runtime.ApplyAsync(QueryMemoryConfiguration.Disabled);
        Assert.Equal(new string?[] { "lease-1" }, harness.Storage.Captures.LeaseIds);
    }

    [Fact]
    public async Task MaintenanceWaitsForTheHostToBeIdleBeforeTheIntervalElapses()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);
        await harness.Timers.Maintain();
        Assert.Single(harness.Storage.Maintenance.Requests);

        // 間隔未到、剛剛有編輯：不跑。閒置超過門檻而且過了最小間隔：提前跑。
        harness.Advance(TimeSpan.FromMinutes(20));
        harness.Runtime.NoteEdit();
        await harness.Timers.Maintain();
        Assert.Single(harness.Storage.Maintenance.Requests);
        harness.Advance(TimeSpan.FromMinutes(3));
        await harness.Timers.Maintain();
        Assert.Equal(2, harness.Storage.Maintenance.Requests.Count);
    }

    [Fact]
    public async Task ShutdownStopsTimersClosesTheStorageAndIgnoresLaterSettings()
    {
        var harness = new Harness();
        await harness.Runtime.ApplyAsync(Enabled);

        Assert.True(await harness.Runtime.ShutdownAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, harness.Timers.Disposed);
        Assert.Equal(1, harness.Storage.Disposals);
        await harness.Runtime.ApplyAsync(Enabled);
        Assert.False(harness.Runtime.IsCapturing);
        Assert.Equal(1, harness.Opens);
        Assert.True(await harness.Runtime.ShutdownAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>開檔卡住時關閉 SSMS 仍受時限約束；晚到的儲存不會被掛上去，而是直接釋放。</summary>
    [Fact]
    public async Task ShutdownGivesUpOnAStuckOpenAndTheLateStorageIsNeverAttached()
    {
        var opening = new TaskCompletionSource<IQueryMemoryStorage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new FakeQueryMemoryStorage();
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
        harness.Storage.BlockReads = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = harness.Runtime.ReadContentAsync("sha256:any", Token);

        await harness.Runtime.ApplyAsync(QueryMemoryConfiguration.Disabled);

        var error = await Assert.ThrowsAsync<QueryMemoryStorageException>(() => read);
        Assert.Equal(QueryMemoryStorageErrorKind.Unavailable, error.Kind);
    }

    [Fact]
    public void ConfigurationFollowsBothSwitchesAndOnlyReclaimsUnsavedDraftsWhenRecoveryIsKept()
    {
        Assert.False(QueryMemoryConfiguration.Disabled.Enabled);
        Assert.False(QueryMemoryConfiguration.From(new SqlAssistSettings { Enabled = false, QueryMemoryEnabled = true }).Enabled);

        var kept = QueryMemoryConfiguration.From(new SqlAssistSettings { QueryMemoryEnabled = true, QueryMemoryUnsavedDraftRetentionDays = 3 });
        Assert.True(kept.Enabled);
        Assert.True(kept.Policy.RecoveryEnabled);
        Assert.Equal(TimeSpan.FromDays(3), kept.Plan.UnsavedDraftRetention);

        var dropped = QueryMemoryConfiguration.From(new SqlAssistSettings { QueryMemoryEnabled = true, QueryMemoryRecoverUnsavedDrafts = false });
        Assert.False(dropped.Policy.RecoveryEnabled);
        Assert.Null(dropped.Plan.UnsavedDraftRetention);

        var noDrafts = QueryMemoryConfiguration.From(new SqlAssistSettings { QueryMemoryEnabled = true, QueryMemoryCaptureDrafts = false });
        Assert.False(noDrafts.Policy.CaptureUnexecutedDrafts);
        Assert.False(noDrafts.Policy.RecoveryEnabled);
        Assert.Equal(TimeSpan.FromTicks(kept.MaintenanceInterval.Ticks / 4), kept.IdleMinimumGap);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10, Token);
        Assert.True(condition());
    }
}
