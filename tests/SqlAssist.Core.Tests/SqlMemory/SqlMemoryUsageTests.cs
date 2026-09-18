using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.Core.Tests.SqlMemory.SqlMemoryTestData;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryUsageTests
{
    private const long Megabyte = 1024L * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SqlRetentionSettings Plan(long? capacity = 100 * Megabyte, int? executions = 10000, int? favorites = 50) =>
        new(TimeSpan.FromDays(30), TimeSpan.FromDays(30), TimeSpan.FromDays(7), capacity, executions, 200, favorites);

    private static SqlMemoryUsageReport Report(long contentBytes, long free = 0, SqlMemoryUsageShare[]? servers = null,
        long executionEvents = 0, long largestFavorite = 0) =>
        new(new SqlMemoryUsage(contentBytes, 2 * contentBytes, 0), free,
            new SqlMemoryUsageCounts(12, executionEvents, 3, 2, 1, 4, 5, 9, largestFavorite, 20),
            servers ?? Array.Empty<SqlMemoryUsageShare>(), servers == null ? null : Start, servers == null ? null : Start.AddDays(3));

    private static SqlMemoryUsageSnapshot Snapshot(SqlMemoryUsageReport report, SqlRetentionSettings? plan = null,
        SqlMemoryCapacityStatus? status = null, bool pending = false, params SqlMemoryActivity[] activities) =>
        new(report, status is { } value
                ? new SqlMemoryMaintenanceState(1, new SqlMemoryMaintenanceRound("plan", Start, 0, true, SqlMemoryMaintenanceScan.Indexed, 0),
                    null, false, value)
                : null,
            plan ?? Plan(), new SqlMemoryMaintenanceOverview(Start.AddMinutes(30), Start.AddMinutes(-5), 0, pending, true), activities);

    [Theory]
    [InlineData(0L, SqlMemoryUsageSeverity.Normal)]
    [InlineData(69L, SqlMemoryUsageSeverity.Normal)]
    [InlineData(70L, SqlMemoryUsageSeverity.Warning)]
    [InlineData(90L, SqlMemoryUsageSeverity.Critical)]
    [InlineData(150L, SqlMemoryUsageSeverity.Critical)]
    public void SeverityFollowsTheSharedThresholds(long used, SqlMemoryUsageSeverity expected) =>
        Assert.Equal(expected, SqlMemoryCapacity.Severity(SqlMemoryCapacity.Ratio(used, 100)));

    [Fact]
    public void UnlimitedCapacityHasNoRatioButCannotReclaimIsStillCritical()
    {
        Assert.Null(SqlMemoryCapacity.Ratio(500, null));
        Assert.Equal(SqlMemoryUsageSeverity.Normal, SqlMemoryCapacity.Severity(null));
        Assert.Equal(SqlMemoryUsageSeverity.Critical, SqlMemoryCapacity.Severity(new SqlMemoryUsage(10, 0, 0), 100,
            SqlMemoryCapacityStatus.CannotReclaimWithinPolicy));
        Assert.Equal(double.PositiveInfinity, SqlMemoryCapacity.Ratio(1, 0));
    }

    [Fact]
    public void MonitorNotifiesOnceUntilUsageFallsBelowTheRearmRatio()
    {
        var monitor = new SqlMemoryCapacityMonitor();
        SqlMemoryUsage Used(long bytes) => new(bytes, 0, 0);

        Assert.Equal((true, false), monitor.Observe(Used(75), 100, null));
        Assert.Equal((true, true), monitor.Observe(Used(92), 100, null));
        // 在門檻附近來回不重複通知；降到 80% 以下才重新武裝。
        Assert.Equal((false, false), monitor.Observe(Used(95), 100, null));
        Assert.Equal((true, false), monitor.Observe(Used(85), 100, null));
        Assert.Equal((true, false), monitor.Observe(Used(91), 100, null));
        Assert.Equal((true, false), monitor.Observe(Used(50), 100, null));
        Assert.Equal((true, true), monitor.Observe(Used(99), 100, null));

        Assert.True(monitor.Reset());
        Assert.Equal(SqlMemoryUsageSeverity.Normal, monitor.Severity);
        Assert.Null(monitor.Ratio);
        Assert.False(monitor.Reset());
    }

    /// <summary>容量通知上的原因短語只有這一份；宿主不自己拼一句。</summary>
    [Fact]
    public void TheCapacityEventCarriesTheReasonPhrase()
    {
        Assert.Equal("內容已用 92%", new SqlMemoryCapacityChangedEventArgs(SqlMemoryUsageSeverity.Critical, 0.92, true).Reason);
        Assert.Equal("內容已超過容量上限",
            new SqlMemoryCapacityChangedEventArgs(SqlMemoryUsageSeverity.Critical, double.PositiveInfinity, true).Reason);
        // 沒有比例可言時只剩一種情形：容量不限，卻因為保留規則清不下來而升到 Critical。
        Assert.Equal("目前的保留規則清不下來",
            new SqlMemoryCapacityChangedEventArgs(SqlMemoryUsageSeverity.Critical, null, true).Reason);
    }

    [Fact]
    public void ActivityLogMergesConsecutiveScheduledMaintenanceAndKeepsTheNewestFirst()
    {
        var log = new SqlMemoryActivityLog();
        log.Record(new SqlMemoryActivity(Start, SqlMemoryActivityKind.ScheduledMaintenance, 10, 100));
        log.Record(new SqlMemoryActivity(Start.AddMinutes(1), SqlMemoryActivityKind.ScheduledMaintenance, 5, 50));
        log.Record(new SqlMemoryActivity(Start.AddMinutes(2), SqlMemoryActivityKind.Compact, 0, 1000));
        log.Record(new SqlMemoryActivity(Start.AddMinutes(3), SqlMemoryActivityKind.ScheduledMaintenance, 1, 0));
        log.Record(new SqlMemoryActivity(Start.AddHours(2), SqlMemoryActivityKind.ScheduledMaintenance, 2, 0));

        var items = log.Snapshot();
        Assert.Equal(new[] { 2L, 1L, 0L, 15L }, items.Select(item => item.DeletedRows));
        Assert.Equal(Start.AddMinutes(1), items[3].At);

        for (var i = 0; i < SqlMemoryActivityLog.Capacity + 5; i++)
            log.Record(new SqlMemoryActivity(Start.AddDays(1).AddMinutes(i), SqlMemoryActivityKind.Backup, 0, i));
        Assert.Equal(SqlMemoryActivityLog.Capacity, log.Snapshot().Count);
        Assert.Equal(SqlMemoryActivityLog.Capacity + 4, log.Snapshot()[0].Bytes);
    }

    [Fact]
    public void SummaryDescribesCapacityQuotasServersAndActivities()
    {
        var servers = new[] { new SqlMemoryUsageShare("BranchA", 40), new SqlMemoryUsageShare("BranchB", 10) };
        var summary = SqlMemoryUsageSummary.Create(Snapshot(Report(75 * Megabyte, 2 * Megabyte, servers, 9000, 20),
            activities: new[]
            {
                new SqlMemoryActivity(Start, SqlMemoryActivityKind.Cleanup, 1234, 3 * Megabyte),
                new SqlMemoryActivity(Start, SqlMemoryActivityKind.Backup, 0, 0, "磁碟已滿"),
            }), Start);

        Assert.Equal("75 MB / 100 MB", summary.Capacity.Value);
        Assert.Equal(0.75, summary.Capacity.Ratio);
        Assert.Equal(SqlMemoryUsageSeverity.Warning, summary.Capacity.Severity);
        Assert.Equal(SqlMemoryUsageSeverity.Warning, summary.Health);
        Assert.Equal("容量偏高", summary.HealthTitle);
        Assert.True(summary.CompactRecommended);
        Assert.Contains("可回收約 2 MB", summary.Disk);

        Assert.Equal(new[] { "9,000 / 10,000", "20 / 50" }, summary.Quotas.Select(quota => quota.Value));
        Assert.Equal(SqlMemoryUsageSeverity.Critical, summary.Quotas[0].Severity);
        Assert.Equal(new[] { 1.0, 0.25 }, summary.Servers.Select(server => server.Ratio));
        Assert.Contains("下次約 30 分鐘後", summary.Maintenance);
        Assert.StartsWith("紀錄範圍 ", summary.Range);

        Assert.Equal("手動清理", summary.Activities[0].Title);
        Assert.Equal("刪除 1,234 列，釋出 3 MB", summary.Activities[0].Detail);
        Assert.True(summary.Activities[1].Failed);
        Assert.Equal("磁碟已滿", summary.Activities[1].Detail);
    }

    [Fact]
    public void SummaryHealthPrefersTheStrongestSignal()
    {
        Assert.Equal("目前的保留規則清不下來", SqlMemoryUsageSummary.Create(
            Snapshot(Report(120 * Megabyte), status: SqlMemoryCapacityStatus.CannotReclaimWithinPolicy), Start).HealthTitle);
        Assert.Equal("已超過容量上限", SqlMemoryUsageSummary.Create(Snapshot(Report(120 * Megabyte)), Start).HealthTitle);
        Assert.Equal("容量接近上限", SqlMemoryUsageSummary.Create(Snapshot(Report(95 * Megabyte)), Start).HealthTitle);
        Assert.Equal("背景整理中", SqlMemoryUsageSummary.Create(Snapshot(Report(Megabyte), pending: true), Start).HealthTitle);

        var unlimited = SqlMemoryUsageSummary.Create(Snapshot(Report(Megabyte), Plan(null, null, null)), Start);
        Assert.Equal("狀態良好", unlimited.HealthTitle);
        Assert.Null(unlimited.Capacity.Ratio);
        Assert.Equal("1 MB", unlimited.Capacity.Value);
        Assert.All(unlimited.Quotas, quota => Assert.Null(quota.Ratio));
        Assert.False(unlimited.CompactRecommended);
        Assert.Equal("還沒有任何紀錄", unlimited.Range);
    }

    [Fact]
    public async Task CleanupDrainsHistoryBatchesThenFavoriteRevisionsAndCheckpointsOnce()
    {
        var store = new FakeSqlMemoryMaintenance();
        store.Usages.Enqueue(new SqlMemoryUsage(500, 1000, 10));
        store.CleanupBatches.Enqueue(new SqlMemoryCleanupBatch(200, 150, 400, "next"));
        store.CleanupBatches.Enqueue(new SqlMemoryCleanupBatch(20, 20, 30, null));
        store.Enqueue(new SqlMemoryMaintenanceResult(5, 4, null, true, new SqlMemoryUsage(0, 0, 0), SqlMemoryCapacityStatus.WithinLimit));
        store.Enqueue(new SqlMemoryMaintenanceResult(0, 0, null, false, new SqlMemoryUsage(0, 0, 0), SqlMemoryCapacityStatus.WithinLimit));
        var request = new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Executions | SqlMemoryCleanupTargets.FavoriteRevisions,
            keepFavoriteRevisions: 3);
        var progress = new SynchronousProgress();

        var result = await SqlMemoryCleanup.RunAsync(store, request, progress, Token);

        Assert.Equal(new string?[] { null, "next" }, store.CleanupRequests.Select(call => call.Cursor));
        Assert.All(store.CleanupRequests, call => Assert.Equal(SqlMemoryCleanup.BatchSize, call.Limit));
        // 收藏版本只帶每收藏配額；其他期限與配額不能趁機生效。
        Assert.Equal(2, store.Requests.Count);
        Assert.All(store.Requests, maintain =>
        {
            Assert.Equal(3, maintain.Policy.MaxRevisionsPerFavorite);
            Assert.Null(maintain.Policy.DraftBefore);
            Assert.Null(maintain.Policy.ExecutionBefore);
            Assert.Null(maintain.Policy.MaxExecutionEvents);
            Assert.Null(maintain.Policy.RecoveryBefore);
            Assert.Null(maintain.Claim);
        });
        Assert.Equal(1, store.Checkpoints);
        Assert.Equal(170, result.DeletedEntries);
        Assert.Equal(434, result.DeletedRows);
        Assert.Equal(500, result.ReleasedContentBytes);
        Assert.Equal(new long[] { 400, 430, 434, 434 }, progress.Values);
    }

    [Fact]
    public async Task CleanupWithoutDeletionsDoesNotCheckpoint()
    {
        var store = new FakeSqlMemoryMaintenance();
        var result = await SqlMemoryCleanup.RunAsync(store, new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Drafts), null, Token);
        Assert.Equal(0, store.Checkpoints);
        Assert.Equal(0, result.DeletedRows);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task ManualMaintenanceStopsAfterTheMaximumPasses()
    {
        var store = new FakeSqlMemoryMaintenance();
        for (var i = 0; i < SqlMemoryCleanup.MaximumPasses + 2; i++)
            store.Enqueue(new SqlMemoryMaintenanceResult(1, 1, null, true, new SqlMemoryUsage(0, 0, 0), SqlMemoryCapacityStatus.WithinLimit));
        var policy = Plan().BuildLadder(Start, true)[0];

        var result = await SqlMemoryCleanup.MaintainAsync(store, policy, null, Token);

        Assert.Equal(SqlMemoryCleanup.MaximumPasses, store.Requests.Count);
        Assert.Equal(SqlMemoryCleanup.MaximumPasses, result.DeletedRows);
        Assert.All(store.Requests, request => Assert.Same(policy, request.Policy));
    }

    [Fact]
    public void CleanupEstimateTextSaysAtMostAndListsOnlyNonEmptyTargets()
    {
        var estimate = new SqlMemoryCleanupEstimate(1200, 0, 3, 45);
        Assert.Equal("最多清除 1,248 筆", SqlMemoryUsageSummary.CleanupHeadline(estimate));
        Assert.Equal("執行紀錄 1,200 · 回復內容 3 · 收藏版本 45", SqlMemoryUsageSummary.CleanupBreakdown(estimate));

        var none = new SqlMemoryCleanupEstimate(0, 0, 0, 0);
        Assert.Equal("沒有符合條件的紀錄", SqlMemoryUsageSummary.CleanupHeadline(none));
        Assert.Equal("", SqlMemoryUsageSummary.CleanupBreakdown(none));
    }

    /// <summary><see cref="Progress{T}"/> 會排到同步內容；測試要在回報的當下記錄順序。</summary>
    private sealed class SynchronousProgress : IProgress<long>
    {
        public System.Collections.Generic.List<long> Values { get; } = new();

        public void Report(long value) => Values.Add(value);
    }
}
