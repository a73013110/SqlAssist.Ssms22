using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

/// <summary>
/// 以 Core 契約驅動同步的 SQLite repository，讓 processor、維護與租約測試不必每次建立 AppDomain。
/// </summary>
/// <remarks>
/// 與隔離邊界相同，每個操作排一次背景，並把 token 交給同步核心；取消與持鎖等待的語意因此一致。
/// 這層只存在於測試，正式路徑一律經由 <c>IsolatedQueryMemoryRepository</c>。
/// </remarks>
internal sealed class SqliteTestRepository : IQueryMemoryRepository, IFavoriteQueryRepository,
    IQueryMemoryMaintenanceRepository, IQueryMemoryLeaseRepository
{
    private readonly SqliteQueryMemoryRepository _storage;

    private SqliteTestRepository(SqliteQueryMemoryRepository storage) => _storage = storage;

    public static Task<SqliteTestRepository> OpenAsync(string path, CancellationToken cancellationToken, int busyTimeoutSeconds = 5) =>
        Task.Run(() => new SqliteTestRepository(SqliteQueryMemoryRepository.Open(path, cancellationToken, busyTimeoutSeconds)), cancellationToken);

    public Task<QuerySessionState?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Run(token => _storage.ReadSession(sessionId, token), cancellationToken);
    public Task<QueryMemoryCommitResult> CommitAsync(QueryMemoryWrite write, string? leaseId, CancellationToken cancellationToken) =>
        Run(token => _storage.Commit(write, leaseId, token), cancellationToken);
    public Task<QueryMemoryPage<QueryHistoryItem>> ReadHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken) =>
        Run(token => _storage.ReadHistory(request, token), cancellationToken);
    public Task<string[]> ReadConnectionFacetsAsync(QueryConnectionFacetRequest request, CancellationToken token) =>
        Run(inner => _storage.ReadConnectionFacets(request, inner), token);
    public Task<QueryContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken) =>
        Run(token => _storage.ReadContent(contentId, token), cancellationToken);

    public Task<FavoriteQueryEntry?> ReadFavoriteQueryAsync(Guid favoriteQueryId, CancellationToken cancellationToken) =>
        Run(token => _storage.ReadFavoriteQuery(favoriteQueryId, token), cancellationToken);
    public Task<QueryMemoryPage<FavoriteQueryEntry>> ReadFavoriteQueriesAsync(FavoriteQueryRequest request, CancellationToken cancellationToken) =>
        Run(token => _storage.ReadFavoriteQueries(request, token), cancellationToken);
    public Task<FavoriteQueryWriteResult> WriteFavoriteQueryAsync(FavoriteQueryWrite write, CancellationToken cancellationToken) =>
        Run(token => _storage.WriteFavoriteQuery(write, token), cancellationToken);
    public Task<FavoriteQueryWriteResult> DeleteFavoriteQueryAsync(Guid favoriteQueryId, Guid expectedVersion, CancellationToken cancellationToken) =>
        Run(token => _storage.DeleteFavoriteQuery(favoriteQueryId, expectedVersion, token), cancellationToken);
    public Task<FavoriteQueryWriteResult> EditFavoriteQuerySqlAsync(FavoriteQueryEdit edit, CancellationToken cancellationToken) =>
        Run(token => _storage.EditFavoriteQuerySql(edit, token), cancellationToken);

    public Task<QueryMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) =>
        Run(token => _storage.ReadUsage(token), cancellationToken);
    public Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request, CancellationToken cancellationToken) =>
        Run(token => _storage.Maintain(request, token), cancellationToken);
    public Task<QueryMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken) =>
        Run(token => _storage.Checkpoint(token), cancellationToken);
    public Task<QueryMemoryUsage> CompactAsync(CancellationToken cancellationToken) =>
        Run(token => _storage.Compact(token), cancellationToken);

    public Task<string> OpenLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken) =>
        Run(token => _storage.OpenLease(owner, now, token), cancellationToken);
    public Task<bool> RenewLeaseAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Run(token => _storage.RenewLease(now, token), cancellationToken);
    public Task<IReadOnlyList<QueryMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit, CancellationToken cancellationToken) =>
        Run(token => _storage.ReadExpiredLeases(expiredBefore, limit, token), cancellationToken);
    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Run(token => _storage.ReleaseLeases(leaseIds, expiredBefore, token), cancellationToken);
    public Task<bool> TryAcquireMaintenanceLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Run(token => _storage.TryAcquireMaintenanceLease(owner, now, expiredBefore, token), cancellationToken);

    private static Task<T> Run<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken) =>
        Task.Run(() => operation(cancellationToken), cancellationToken);
}
