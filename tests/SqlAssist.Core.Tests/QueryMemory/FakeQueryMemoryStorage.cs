using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Core.Tests.QueryMemory;

/// <summary>宿主測試用的整份儲存：擷取交給記錄式 repository，維護與租約交給記錄式假實作。</summary>
internal sealed class FakeQueryMemoryStorage : IQueryMemoryStorage
{
    public RecordingQueryMemoryRepository Captures { get; } = new();

    public FakeQueryMemoryMaintenance Maintenance { get; } = new();

    public int Disposals { get; private set; }

    /// <summary>設定後，全文讀取會等到它完成；用來觀察關閉途中的讀取。</summary>
    public TaskCompletionSource<bool>? BlockReads { get; set; }

    public Task<QuerySessionState?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Captures.ReadSessionAsync(sessionId, cancellationToken);

    public Task<QueryMemoryCommitResult> CommitAsync(QueryMemoryWrite write, string? leaseId, CancellationToken cancellationToken) =>
        Captures.CommitAsync(write, leaseId, cancellationToken);

    public Task<QueryMemoryPage<QueryHistoryItem>> ReadHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new QueryMemoryPage<QueryHistoryItem>(Array.Empty<QueryHistoryItem>(), null));

    public async Task<QueryContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken)
    {
        if (BlockReads is { } block)
        {
            using (cancellationToken.Register(() => block.TrySetCanceled()))
                await block.Task.ConfigureAwait(false);
        }
        return await Captures.ReadContentAsync(contentId, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> ReadConnectionFacetsAsync(QueryConnectionFacetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { "LibraryServer" });

    public Task<FavoriteQueryEntry?> ReadFavoriteQueryAsync(Guid favoriteQueryId, CancellationToken cancellationToken) =>
        Task.FromResult<FavoriteQueryEntry?>(null);

    public Task<QueryMemoryPage<FavoriteQueryEntry>> ReadFavoriteQueriesAsync(FavoriteQueryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new QueryMemoryPage<FavoriteQueryEntry>(Array.Empty<FavoriteQueryEntry>(), null));

    public Task<FavoriteQueryWriteResult> WriteFavoriteQueryAsync(FavoriteQueryWrite write, CancellationToken cancellationToken) =>
        Task.FromResult(FavoriteQueryWriteResult.Committed);

    public Task<FavoriteQueryWriteResult> DeleteFavoriteQueryAsync(Guid favoriteQueryId, Guid expectedVersion, CancellationToken cancellationToken) =>
        Task.FromResult(FavoriteQueryWriteResult.Committed);

    public Task<FavoriteQueryWriteResult> EditFavoriteQuerySqlAsync(FavoriteQueryEdit edit, CancellationToken cancellationToken) =>
        Task.FromResult(FavoriteQueryWriteResult.Committed);

    public Task<QueryMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) => Maintenance.ReadUsageAsync(cancellationToken);

    public Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request, CancellationToken cancellationToken) =>
        Maintenance.MaintainAsync(request, cancellationToken);

    public Task<QueryMemoryMaintenanceState?> ReadMaintenanceStateAsync(CancellationToken cancellationToken) =>
        Maintenance.ReadMaintenanceStateAsync(cancellationToken);

    public Task<QueryMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken) => Maintenance.CheckpointAsync(cancellationToken);

    public Task<QueryMemoryUsage> CompactAsync(CancellationToken cancellationToken) => Maintenance.CompactAsync(cancellationToken);

    public Task<string> OpenLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken) =>
        Maintenance.OpenLeaseAsync(owner, now, cancellationToken);

    public Task<bool> RenewLeaseAsync(string leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Maintenance.RenewLeaseAsync(leaseId, now, cancellationToken);

    public Task<IReadOnlyList<QueryMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit, string? excludedLeaseId,
        CancellationToken cancellationToken) =>
        Maintenance.ReadExpiredLeasesAsync(expiredBefore, limit, excludedLeaseId, cancellationToken);

    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Maintenance.ReleaseLeasesAsync(leaseIds, expiredBefore, cancellationToken);

    public Task<bool> TryAcquireMaintenanceLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken) =>
        Maintenance.TryAcquireMaintenanceLeaseAsync(owner, now, expiredBefore, cancellationToken);

    public Task<bool> ReleaseMaintenanceLeaseAsync(QueryMemoryLeaseOwner owner, CancellationToken cancellationToken) =>
        Maintenance.ReleaseMaintenanceLeaseAsync(owner, cancellationToken);

    public void Dispose() => Disposals++;
}

/// <summary>手動觸發的計時器；測試決定哪一刻發生哪一次心跳或維護。</summary>
internal sealed class ManualQueryMemoryTimers : IQueryMemoryTimerFactory
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
        private readonly ManualQueryMemoryTimers _owner;
        private bool _disposed;

        public Registration(ManualQueryMemoryTimers owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Disposed++;
        }
    }
}

internal sealed class RecordingQueryMemoryLog : IQueryMemoryRuntimeLog
{
    private readonly object _gate = new();
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages { get { lock (_gate) return _messages.ToArray(); } }

    public void Detail(string message) { lock (_gate) _messages.Add(message); }

    public void Important(string message) { lock (_gate) _messages.Add(message); }
}
