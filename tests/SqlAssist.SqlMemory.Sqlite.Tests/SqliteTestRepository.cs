using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

/// <summary>
/// 以 Core 契約驅動同步的 SQLite store，讓 processor、維護與租約測試不必每次建立 AppDomain。
/// </summary>
/// <remarks>
/// 與隔離邊界相同，每個操作排一次背景，並把 token 交給同步核心；取消與持鎖等待的語意因此一致。
/// 這層只存在於測試，正式路徑一律經由 <c>IsolatedSqlMemoryStore</c>。
/// </remarks>
internal sealed class SqliteTestRepository : ISqlMemoryStore
{
    private readonly SqliteCaptureStore _captures;
    private readonly SqliteFavoriteStore _favorites;
    private readonly SqliteMaintenanceStore _maintenance;
    private readonly SqliteUsageStore _usage;
    private readonly SqliteLeaseStore _leases;

    private SqliteTestRepository(SqliteDatabase database, SqliteSearchBudget? searchBudget)
    {
        _captures = new SqliteCaptureStore(database, searchBudget);
        _favorites = new SqliteFavoriteStore(database, searchBudget);
        _maintenance = new SqliteMaintenanceStore(database);
        _usage = new SqliteUsageStore(database);
        _leases = new SqliteLeaseStore(database);
    }

    public static Task<SqliteTestRepository> OpenAsync(string path, CancellationToken cancellationToken, int busyTimeoutSeconds = 5,
        SqliteSearchBudget? searchBudget = null) =>
        Task.Run(() => new SqliteTestRepository(SqliteDatabase.Open(path, cancellationToken, busyTimeoutSeconds), searchBudget), cancellationToken);

    public Task<SqlSessionHead?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Run(token => _captures.ReadSession(sessionId, token), cancellationToken);
    public Task<SqlHistoryCommitResult> CommitAsync(SqlCaptureCommit write, string? leaseId, CancellationToken cancellationToken) =>
        Run(token => _captures.Commit(write, leaseId, token), cancellationToken);
    public Task<SqlMemoryPage<SqlHistoryItem>> ReadHistoryAsync(SqlHistoryRequest request, CancellationToken cancellationToken) =>
        Run(token => _captures.ReadHistory(request, token), cancellationToken);
    public Task<IReadOnlyList<string>> ReadConnectionFacetsAsync(SqlConnectionFacetRequest request, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<string>>(token => _captures.ReadConnectionFacets(request, token), cancellationToken);
    public Task<SqlContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken) =>
        Run(token => _captures.ReadContent(contentId, token), cancellationToken);
    public Task<SqlHistoryDeleteResult> DeleteHistoryAsync(SqlHistoryItem item, CancellationToken cancellationToken) =>
        Run(token => _captures.DeleteHistory(item, token), cancellationToken);

    public Task<SqlFavoriteItem?> ReadFavoriteAsync(Guid favoriteId, CancellationToken cancellationToken) =>
        Run(token => _favorites.ReadFavorite(favoriteId, token), cancellationToken);
    public Task<SqlMemoryPage<SqlFavoriteItem>> ReadFavoritesAsync(SqlFavoriteRequest request, CancellationToken cancellationToken) =>
        Run(token => _favorites.ReadFavorites(request, token), cancellationToken);
    public Task<SqlFavoriteWriteResult> SaveFavoriteAsync(SqlFavoriteSave save, CancellationToken cancellationToken) =>
        Run(token => _favorites.SaveFavorite(save, token), cancellationToken);
    public Task<SqlFavoriteWriteResult> DeleteFavoriteAsync(Guid favoriteId, Guid expectedVersion, CancellationToken cancellationToken) =>
        Run(token => _favorites.DeleteFavorite(favoriteId, expectedVersion, token), cancellationToken);
    public Task<SqlMemoryPage<SqlFavoriteRevisionItem>> ReadFavoriteRevisionsAsync(SqlFavoriteRevisionRequest request,
        CancellationToken cancellationToken) =>
        Run(token => _favorites.ReadFavoriteRevisions(request, token), cancellationToken);

    public Task<SqlMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) =>
        Run(token => _maintenance.ReadUsage(token), cancellationToken);
    public Task<SqlMemoryMaintenanceResult> MaintainAsync(SqlMemoryMaintenanceRequest request, CancellationToken cancellationToken) =>
        Run(token => _maintenance.Maintain(request, token), cancellationToken);
    public Task<SqlMemoryMaintenanceState?> ReadMaintenanceStateAsync(CancellationToken cancellationToken) =>
        Run(token => _maintenance.ReadMaintenanceState(token), cancellationToken);
    public Task<SqlMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken) =>
        Run(token => _maintenance.Checkpoint(token), cancellationToken);
    public Task<SqlMemoryUsage> CompactAsync(CancellationToken cancellationToken) =>
        Run(token => _maintenance.Compact(token), cancellationToken);
    public Task<SqlMemoryUsageReport> ReadUsageReportAsync(CancellationToken cancellationToken) =>
        Run(token => _usage.ReadReport(token), cancellationToken);
    public Task<SqlMemoryCleanupEstimate> EstimateCleanupAsync(SqlMemoryCleanupRequest request, CancellationToken cancellationToken) =>
        Run(token => _usage.Estimate(request, token), cancellationToken);
    public Task<SqlMemoryCleanupBatch> CleanupHistoryAsync(SqlMemoryCleanupRequest request, string? cursor, int limit,
        CancellationToken cancellationToken) =>
        Run(token => _usage.CleanupHistory(request, cursor, limit, token), cancellationToken);
    public Task<long> BackupAsync(string destinationPath, CancellationToken cancellationToken) =>
        Run(token => _usage.Backup(destinationPath, token), cancellationToken);

    public Task<string> OpenLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken) =>
        Run(token => _leases.OpenLease(owner, now, token), cancellationToken);
    public Task<bool> RenewLeaseAsync(string leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Run(token => _leases.RenewLease(leaseId, now, token), cancellationToken);
    public Task<IReadOnlyList<SqlMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit, string? excludedLeaseId,
        CancellationToken cancellationToken) =>
        Run(token => _leases.ReadExpiredLeases(expiredBefore, limit, excludedLeaseId, token), cancellationToken);
    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Run(token => _leases.ReleaseLeases(leaseIds, expiredBefore, token), cancellationToken);
    public Task<bool> TryAcquireMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Run(token => _leases.TryAcquireMaintenanceLease(owner, now, expiredBefore, token), cancellationToken);
    public Task<bool> ReleaseMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, CancellationToken cancellationToken) =>
        Run(token => _leases.ReleaseMaintenanceLease(owner, token), cancellationToken);

    /// <summary>連線不保留 pool，沒有要釋放的東西；實作 <see cref="IDisposable"/> 只為了符合宿主的儲存契約。</summary>
    public void Dispose() { }

    private static Task<T> Run<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken) =>
        Task.Run(() => operation(cancellationToken), cancellationToken);
}
