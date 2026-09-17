using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Isolation;

/// <summary>只隔離 SQLite provider 的靜態狀態與 binding redirect；不另造通用宿主框架。</summary>
/// <remarks>
/// 操作之間不互斥：每個操作在 worker 端各開連線，讀取與提交靠 WAL 並行，VACUUM 或全文掃描
/// 不會把 writer、心跳與預覽排在後面。唯一的協調是卸載——操作以共享方式進入，
/// <see cref="Dispose"/> 等全部離開才卸載 AppDomain。
/// </remarks>
public sealed class IsolatedSqlMemoryStore : ISqlMemoryStore
{
    private readonly object _sync = new();
    private readonly AppDomain _domain;
    private readonly SqlMemoryIsolatedWorker _worker;
    private readonly IsolationAssemblyResolveScope _resolution;
    private int _active;
    private bool _disposing;
    private bool _unloaded;

    private IsolatedSqlMemoryStore(AppDomain domain, SqlMemoryIsolatedWorker worker, IsolationAssemblyResolveScope resolution)
    {
        _domain = domain; _worker = worker; _resolution = resolution;
    }

    /// <remarks>失敗一律以 <see cref="SqlMemoryStorageException"/> 回報，分類在隔離 AppDomain 內決定。</remarks>
    public static Task<IsolatedSqlMemoryStore> OpenAsync(string databasePath, string? ssmsIdeDirectory,
        CancellationToken cancellationToken, int busyTimeoutSeconds = 5) =>
        OpenAsync(databasePath, ssmsIdeDirectory, cancellationToken, busyTimeoutSeconds, null, null);

    /// <summary>搜尋預算只給測試放大：隔離層的並行、取消與卸載測試需要一個跑得夠久的搜尋。</summary>
    internal static Task<IsolatedSqlMemoryStore> OpenAsync(string databasePath, string? ssmsIdeDirectory,
        CancellationToken cancellationToken, int busyTimeoutSeconds, int? searchCandidates, long? searchBytes)
    {
        return Task.Run(() =>
        {
            var folder = Path.GetDirectoryName(typeof(IsolatedSqlMemoryStore).Assembly.Location)
                ?? throw new InvalidOperationException("找不到 SQL Memory 組件目錄。");
            var config = Path.Combine(folder, "SqlMemory.Isolation.config");
            if (!File.Exists(config)) throw new FileNotFoundException("缺少 SQL Memory 隔離載入設定。", config);
            var domain = AppDomain.CreateDomain("SqlAssist.SqlMemory." + Guid.NewGuid().ToString("N"), null,
                new AppDomainSetup { ApplicationBase = folder, ConfigurationFile = config });
            var resolution = new IsolationAssemblyResolveScope();
            try
            {
                // 實際檔案限定 worker 來源；回程型別解析另由有限生命週期的 resolution 處理。
                var worker = (SqlMemoryIsolatedWorker)domain.CreateInstanceFromAndUnwrap(
                    typeof(SqlMemoryIsolatedWorker).Assembly.Location, typeof(SqlMemoryIsolatedWorker).FullName);
                worker.Initialize(databasePath, ssmsIdeDirectory, busyTimeoutSeconds, searchCandidates, searchBytes);
                return new IsolatedSqlMemoryStore(domain, worker, resolution);
            }
            catch
            {
                try { AppDomain.Unload(domain); }
                finally { resolution.Dispose(); }
                throw;
            }
        }, cancellationToken);
    }

    public Task<SqlSessionHead?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadSession(operation, sessionId), cancellationToken);
    public Task<SqlHistoryCommitResult> CommitAsync(SqlCaptureCommit write, string? leaseId, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.Commit(operation, write, leaseId), cancellationToken);
    public Task<SqlMemoryPage<SqlHistoryItem>> ReadHistoryAsync(SqlHistoryRequest request, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadHistory(operation, request), cancellationToken);
    public Task<IReadOnlyList<string>> ReadConnectionFacetsAsync(SqlConnectionFacetRequest request, CancellationToken cancellationToken) =>
        Invoke<IReadOnlyList<string>>(operation => _worker.ReadConnectionFacets(operation, request), cancellationToken);

    public Task<SqlContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadContent(operation, contentId), cancellationToken);
    public Task<SqlHistoryDeleteResult> DeleteHistoryAsync(SqlHistoryItem item, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.DeleteHistory(operation, item), cancellationToken);
    public Task<string> ProbeAsync(CancellationToken cancellationToken) => Invoke(_ => _worker.Probe(), cancellationToken);

    public Task<SqlFavoriteItem?> ReadFavoriteAsync(Guid favoriteId, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadFavorite(operation, favoriteId), cancellationToken);
    public Task<SqlMemoryPage<SqlFavoriteItem>> ReadFavoritesAsync(SqlFavoriteRequest request, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadFavorites(operation, request), cancellationToken);
    public Task<SqlFavoriteWriteResult> SaveFavoriteAsync(SqlFavoriteSave save, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.SaveFavorite(operation, save), cancellationToken);
    public Task<SqlFavoriteWriteResult> DeleteFavoriteAsync(Guid favoriteId, Guid expectedVersion, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.DeleteFavorite(operation, favoriteId, expectedVersion), cancellationToken);
    public Task<SqlMemoryPage<SqlFavoriteRevisionItem>> ReadFavoriteRevisionsAsync(SqlFavoriteRevisionRequest request,
        CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadFavoriteRevisions(operation, request), cancellationToken);

    public Task<SqlMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadUsage(operation), cancellationToken);
    public Task<SqlMemoryMaintenanceResult> MaintainAsync(SqlMemoryMaintenanceRequest request, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.Maintain(operation, request), cancellationToken);
    public Task<SqlMemoryMaintenanceState?> ReadMaintenanceStateAsync(CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadMaintenanceState(operation), cancellationToken);
    public Task<string> OpenLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.OpenLease(operation, owner, now), cancellationToken);
    public Task<bool> RenewLeaseAsync(string leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.RenewLease(operation, leaseId, now), cancellationToken);
    public Task<IReadOnlyList<SqlMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit, string? excludedLeaseId,
        CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadExpiredLeases(operation, expiredBefore, limit, excludedLeaseId), cancellationToken);
    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReleaseLeases(operation, leaseIds, expiredBefore), cancellationToken);
    public Task<bool> TryAcquireMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now, DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.TryAcquireMaintenanceLease(operation, owner, now, expiredBefore), cancellationToken);
    public Task<bool> ReleaseMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReleaseMaintenanceLease(operation, owner), cancellationToken);

    public Task<SqlMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken) =>
        Invoke(operation => _worker.Checkpoint(operation), cancellationToken);
    public Task<SqlMemoryUsage> CompactAsync(CancellationToken cancellationToken) =>
        Invoke(operation => _worker.Compact(operation), cancellationToken);
    public Task<SqlMemoryUsageReport> ReadUsageReportAsync(CancellationToken cancellationToken) =>
        Invoke(operation => _worker.ReadUsageReport(operation), cancellationToken);
    public Task<SqlMemoryCleanupEstimate> EstimateCleanupAsync(SqlMemoryCleanupRequest request, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.EstimateCleanup(operation, request), cancellationToken);
    public Task<SqlMemoryCleanupBatch> CleanupHistoryAsync(SqlMemoryCleanupRequest request, string? cursor, int limit,
        CancellationToken cancellationToken) =>
        Invoke(operation => _worker.CleanupHistory(operation, request, cursor, limit), cancellationToken);
    public Task<long> BackupAsync(string destinationPath, CancellationToken cancellationToken) =>
        Invoke(operation => _worker.Backup(operation, destinationPath), cancellationToken);

    private async Task<T> Invoke<T>(Func<long, T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enter();
        try
        {
            // 跨 AppDomain 呼叫會同步占住執行緒；整條路徑只在這裡排一次背景。
            return await Task.Run(() => Dispatch(operation, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { Exit(); }
    }

    private T Dispatch<T>(Func<long, T> operation, CancellationToken cancellationToken)
    {
        var id = _worker.BeginOperation();
        try
        {
            // 處置註冊會等執行中的回呼結束，之後才結束操作，取消不會落在已處置的來源上。
            using (cancellationToken.Register(CancelOperation, id))
                return operation(id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 只有 worker 在提交前放棄才會走到這裡；已提交的操作照常回傳結果，不宣稱已取消。
            throw new OperationCanceledException(cancellationToken);
        }
        finally { _worker.EndOperation(id); }
    }

    private void CancelOperation(object? state) => _worker.CancelOperation((long)state!);

    private void Enter()
    {
        lock (_sync)
        {
            if (_disposing) throw new ObjectDisposedException(nameof(IsolatedSqlMemoryStore));
            _active++;
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            if (--_active == 0 && _disposing) Monitor.PulseAll(_sync);
        }
    }

    /// <summary>拒絕新操作、等進行中的操作離開，再卸載 AppDomain。</summary>
    /// <remarks>
    /// 等待沒有上限，逾時由宿主決定；宿主應先排空 SqlCaptureQueue 並取消自己的操作，
    /// 否則這裡會等到長操作自然結束。
    /// </remarks>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposing = true;
            while (_active != 0) Monitor.Wait(_sync);
            if (_unloaded) return;
            _unloaded = true;
            // 留在鎖內卸載：並行的 Dispose 要等卸載完成才返回；新操作在 _disposing 時已被拒絕，不會卡在鎖上太久。
            try { AppDomain.Unload(_domain); }
            finally { _resolution.Dispose(); }
        }
    }
}
