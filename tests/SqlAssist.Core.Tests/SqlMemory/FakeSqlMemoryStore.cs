using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Core.Tests.SqlMemory;

/// <summary>宿主測試用的整份儲存：擷取交給記錄式 store，維護與租約交給記錄式假實作。</summary>
internal sealed class FakeSqlMemoryStore : ISqlMemoryStore
{
    public RecordingSqlHistoryStore Captures { get; } = new();

    public FakeSqlMemoryMaintenance Maintenance { get; } = new();

    public int Disposals { get; private set; }

    /// <summary>設定後，全文讀取會等到它完成；用來觀察關閉途中的讀取。</summary>
    public TaskCompletionSource<bool>? BlockReads { get; set; }

    public Task<SqlSessionHead?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Captures.ReadSessionAsync(sessionId, cancellationToken);

    public Task<SqlHistoryCommitResult> CommitAsync(SqlCaptureCommit write, string? leaseId, CancellationToken cancellationToken) =>
        Captures.CommitAsync(write, leaseId, cancellationToken);

    public Task<SqlMemoryPage<SqlHistoryItem>> ReadHistoryAsync(SqlHistoryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SqlMemoryPage<SqlHistoryItem>(Array.Empty<SqlHistoryItem>(), null));

    public async Task<SqlContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken)
    {
        if (BlockReads is { } block)
        {
            using (cancellationToken.Register(() => block.TrySetCanceled()))
                await block.Task.ConfigureAwait(false);
        }
        return await Captures.ReadContentAsync(contentId, cancellationToken).ConfigureAwait(false);
    }

    public Task<SqlHistoryDeleteResult> DeleteHistoryAsync(SqlHistoryItem item, CancellationToken cancellationToken) =>
        Captures.DeleteHistoryAsync(item, cancellationToken);

    public Task<IReadOnlyList<string>> ReadConnectionFacetsAsync(SqlConnectionFacetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { "LibraryServer" });

    public Task<SqlFavoriteItem?> ReadFavoriteAsync(Guid favoriteId, CancellationToken cancellationToken) =>
        Task.FromResult<SqlFavoriteItem?>(null);

    public Task<SqlMemoryPage<SqlFavoriteItem>> ReadFavoritesAsync(SqlFavoriteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SqlMemoryPage<SqlFavoriteItem>(Array.Empty<SqlFavoriteItem>(), null));

    public Task<SqlFavoriteWriteResult> SaveFavoriteAsync(SqlFavoriteSave save, CancellationToken cancellationToken) =>
        Task.FromResult(SqlFavoriteWriteResult.Committed);

    public Task<SqlFavoriteWriteResult> DeleteFavoriteAsync(Guid favoriteId, Guid expectedVersion, CancellationToken cancellationToken) =>
        Task.FromResult(SqlFavoriteWriteResult.Committed);

    public Task<SqlMemoryPage<SqlFavoriteRevisionItem>> ReadFavoriteRevisionsAsync(SqlFavoriteRevisionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SqlMemoryPage<SqlFavoriteRevisionItem>(Array.Empty<SqlFavoriteRevisionItem>(), null));

    public Task<SqlMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) => Maintenance.ReadUsageAsync(cancellationToken);

    public Task<SqlMemoryMaintenanceResult> MaintainAsync(SqlMemoryMaintenanceRequest request, CancellationToken cancellationToken) =>
        Maintenance.MaintainAsync(request, cancellationToken);

    public Task<SqlMemoryMaintenanceState?> ReadMaintenanceStateAsync(CancellationToken cancellationToken) =>
        Maintenance.ReadMaintenanceStateAsync(cancellationToken);

    public Task<SqlMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken) => Maintenance.CheckpointAsync(cancellationToken);

    public Task<SqlMemoryUsage> CompactAsync(CancellationToken cancellationToken) => Maintenance.CompactAsync(cancellationToken);

    public Task<SqlMemoryUsageReport> ReadUsageReportAsync(CancellationToken cancellationToken) =>
        Maintenance.ReadUsageReportAsync(cancellationToken);

    public Task<SqlMemoryCleanupEstimate> EstimateCleanupAsync(SqlMemoryCleanupRequest request, CancellationToken cancellationToken) =>
        Maintenance.EstimateCleanupAsync(request, cancellationToken);

    public Task<SqlMemoryCleanupBatch> CleanupHistoryAsync(SqlMemoryCleanupRequest request, string? cursor, int limit,
        CancellationToken cancellationToken) =>
        Maintenance.CleanupHistoryAsync(request, cursor, limit, cancellationToken);

    public Task<long> BackupAsync(string destinationPath, CancellationToken cancellationToken) =>
        Maintenance.BackupAsync(destinationPath, cancellationToken);

    public Task<string> OpenLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken) =>
        Maintenance.OpenLeaseAsync(owner, now, cancellationToken);

    public Task<bool> RenewLeaseAsync(string leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Maintenance.RenewLeaseAsync(leaseId, now, cancellationToken);

    public Task<IReadOnlyList<SqlMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit, string? excludedLeaseId,
        CancellationToken cancellationToken) =>
        Maintenance.ReadExpiredLeasesAsync(expiredBefore, limit, excludedLeaseId, cancellationToken);

    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Maintenance.ReleaseLeasesAsync(leaseIds, expiredBefore, cancellationToken);

    public Task<bool> TryAcquireMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken) =>
        Maintenance.TryAcquireMaintenanceLeaseAsync(owner, now, expiredBefore, cancellationToken);

    public Task<bool> ReleaseMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, CancellationToken cancellationToken) =>
        Maintenance.ReleaseMaintenanceLeaseAsync(owner, cancellationToken);

    public void Dispose() => Disposals++;
}

/// <summary>手動觸發的計時器；測試決定哪一刻發生哪一次心跳或維護。</summary>
internal sealed class ManualSqlMemoryTimers : ISqlMemoryTimerFactory
{
    private readonly Dictionary<string, Func<Task>> _ticks = new(StringComparer.Ordinal);

    public int Disposed { get; private set; }

    public IReadOnlyCollection<string> Names => _ticks.Keys;

    public IDisposable Start(string name, TimeSpan period, Func<Task> tick)
    {
        _ticks.Add(name, tick);
        return new Registration(this);
    }

    public Task Maintain() => Tick("SQL Memory 維護排程");

    public Task Beat() => Tick("SQL Memory 租約心跳");

    private Task Tick(string name) => _ticks.TryGetValue(name, out var tick) ? tick() : Task.CompletedTask;

    private sealed class Registration : IDisposable
    {
        private readonly ManualSqlMemoryTimers _owner;
        private bool _disposed;

        public Registration(ManualSqlMemoryTimers owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Disposed++;
        }
    }
}

internal sealed class RecordingSqlMemoryLog : ISqlMemoryRuntimeLog
{
    private readonly object _gate = new();
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages { get { lock (_gate) return _messages.ToArray(); } }

    public void Detail(string message) { lock (_gate) _messages.Add(message); }

    public void Important(string message) { lock (_gate) _messages.Add(message); }
}
