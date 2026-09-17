using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Sqlite;

namespace SqlAssist.SqlMemory.Isolation;

/// <summary>只在隔離 AppDomain 建立；跨界僅傳遞可序列化的 Core DTO，不傳 provider 物件。</summary>
/// <remarks>
/// 可由多條執行緒同時呼叫：各 store 每個操作各開連線，並行交給 SQLite WAL 與交易。
/// <see cref="CancellationToken"/> 無法跨 AppDomain，呼叫端先以 <see cref="BeginOperation"/> 取得識別碼，
/// 取消時呼叫 <see cref="CancelOperation"/>；token 在 worker 端建立，KMP 掃描與交易內檢查才接得到。
/// </remarks>
public sealed class SqlMemoryIsolatedWorker : MarshalByRefObject
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _operations = new();
    private long _nextOperation;
    private Stores? _stores;
    private Stores Storage => _stores ?? throw new InvalidOperationException("尚未初始化 SQLite worker。");

    public override object? InitializeLifetimeService() => null;

    /// <param name="searchCandidates">null 用預設搜尋預算；只有測試會指定，兩者必須同時給。</param>
    /// <param name="searchBytes">搜尋預算的 SQL 位元組上限。</param>
    public void Initialize(string path, string? ssmsIdeDirectory, int busyTimeoutSeconds, int? searchCandidates = null, long? searchBytes = null)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            var name = new AssemblyName(request.Name).Name;
            if (name == null || !name.StartsWith("System.", StringComparison.Ordinal)) return null;
            var folders = ssmsIdeDirectory == null
                ? new[] { AppDomain.CurrentDomain.BaseDirectory }
                : new[] { Path.Combine(ssmsIdeDirectory, "PublicAssemblies"), Path.Combine(ssmsIdeDirectory, "PrivateAssemblies"), ssmsIdeDirectory };
            foreach (var folder in folders)
            {
                var file = Path.Combine(folder, name + ".dll");
                if (File.Exists(file)) return Assembly.LoadFrom(file);
            }
            return null;
        };
        InitializeStorage(path, busyTimeoutSeconds, searchCandidates, searchBytes);
        Probe();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InitializeStorage(string path, int busyTimeoutSeconds, int? searchCandidates, long? searchBytes)
    {
        Run(() =>
        {
            var database = SqliteDatabase.Open(path, CancellationToken.None, busyTimeoutSeconds);
            var budget = searchCandidates.HasValue || searchBytes.HasValue
                ? new SqliteSearchBudget(searchCandidates ?? SqliteSearchBudget.Default.Candidates, searchBytes ?? SqliteSearchBudget.Default.Bytes)
                : SqliteSearchBudget.Default;
            _stores = new Stores(database, budget);
            return true;
        });
    }

    public long BeginOperation()
    {
        var id = Interlocked.Increment(ref _nextOperation);
        _operations[id] = new CancellationTokenSource();
        return id;
    }

    /// <summary>未知或已結束的識別碼不做事；取消只是請求，是否已提交仍以操作回傳為準。</summary>
    public void CancelOperation(long id)
    {
        if (_operations.TryGetValue(id, out var source)) source.Cancel();
    }

    /// <remarks>呼叫端必須先解除取消註冊再結束，否則取消可能落在已處置的來源上。</remarks>
    public void EndOperation(long id)
    {
        if (_operations.TryRemove(id, out var source)) source.Dispose();
    }

    public SqlSessionHead? ReadSession(long operation, Guid sessionId) => Run(operation, token => Storage.Captures.ReadSession(sessionId, token));
    public SqlHistoryCommitResult Commit(long operation, SqlCaptureCommit write, string? leaseId) =>
        Run(operation, token => Storage.Captures.Commit(write, leaseId, token));
    public SqlMemoryPage<SqlHistoryItem> ReadHistory(long operation, SqlHistoryRequest request) =>
        Run(operation, token => Storage.Captures.ReadHistory(request, token));
    public string[] ReadConnectionFacets(long operation, SqlConnectionFacetRequest request) =>
        Run(operation, token => Storage.Captures.ReadConnectionFacets(request, token));
    public SqlContent? ReadContent(long operation, string contentId) => Run(operation, token => Storage.Captures.ReadContent(contentId, token));
    public SqlHistoryDeleteResult DeleteHistory(long operation, SqlHistoryItem item) =>
        Run(operation, token => Storage.Captures.DeleteHistory(item, token));

    public SqlFavoriteItem? ReadFavorite(long operation, Guid id) => Run(operation, token => Storage.Favorites.ReadFavorite(id, token));
    public SqlMemoryPage<SqlFavoriteItem> ReadFavorites(long operation, SqlFavoriteRequest request) =>
        Run(operation, token => Storage.Favorites.ReadFavorites(request, token));
    public SqlFavoriteWriteResult SaveFavorite(long operation, SqlFavoriteSave save) =>
        Run(operation, token => Storage.Favorites.SaveFavorite(save, token));
    public SqlFavoriteWriteResult DeleteFavorite(long operation, Guid id, Guid version) =>
        Run(operation, token => Storage.Favorites.DeleteFavorite(id, version, token));
    public SqlMemoryPage<SqlFavoriteRevisionItem> ReadFavoriteRevisions(long operation, SqlFavoriteRevisionRequest request) =>
        Run(operation, token => Storage.Favorites.ReadFavoriteRevisions(request, token));

    public SqlMemoryUsage ReadUsage(long operation) => Run(operation, token => Storage.Maintenance.ReadUsage(token));
    public SqlMemoryMaintenanceResult Maintain(long operation, SqlMemoryMaintenanceRequest request) =>
        Run(operation, token => Storage.Maintenance.Maintain(request, token));
    public SqlMemoryMaintenanceState? ReadMaintenanceState(long operation) =>
        Run(operation, token => Storage.Maintenance.ReadMaintenanceState(token));
    public SqlMemoryCheckpointResult Checkpoint(long operation) => Run(operation, token => Storage.Maintenance.Checkpoint(token));
    public SqlMemoryUsage Compact(long operation) => Run(operation, token => Storage.Maintenance.Compact(token));
    public SqlMemoryUsageReport ReadUsageReport(long operation) => Run(operation, token => Storage.Usage.ReadReport(token));
    public SqlMemoryCleanupEstimate EstimateCleanup(long operation, SqlMemoryCleanupRequest request) =>
        Run(operation, token => Storage.Usage.Estimate(request, token));
    public SqlMemoryCleanupBatch CleanupHistory(long operation, SqlMemoryCleanupRequest request, string? cursor, int limit) =>
        Run(operation, token => Storage.Usage.CleanupHistory(request, cursor, limit, token));
    public long Backup(long operation, string destinationPath) => Run(operation, token => Storage.Usage.Backup(destinationPath, token));

    public string OpenLease(long operation, SqlMemoryLeaseOwner owner, DateTimeOffset now) =>
        Run(operation, token => Storage.Leases.OpenLease(owner, now, token));
    public bool RenewLease(long operation, string leaseId, DateTimeOffset now) =>
        Run(operation, token => Storage.Leases.RenewLease(leaseId, now, token));
    public IReadOnlyList<SqlMemoryLease> ReadExpiredLeases(long operation, DateTimeOffset before, int limit, string? excludedLeaseId) =>
        Run(operation, token => Storage.Leases.ReadExpiredLeases(before, limit, excludedLeaseId, token));
    public int ReleaseLeases(long operation, IReadOnlyList<string> leaseIds, DateTimeOffset before) =>
        Run(operation, token => Storage.Leases.ReleaseLeases(leaseIds, before, token));
    public bool TryAcquireMaintenanceLease(long operation, SqlMemoryLeaseOwner owner, DateTimeOffset now, DateTimeOffset before) =>
        Run(operation, token => Storage.Leases.TryAcquireMaintenanceLease(owner, now, before, token));
    public bool ReleaseMaintenanceLease(long operation, SqlMemoryLeaseOwner owner) =>
        Run(operation, token => Storage.Leases.ReleaseMaintenanceLease(owner, token));

    public string Probe() => Run(() =>
    {
        var version = SqliteRuntime.Probe(Storage.Database.FilePath);
        var folder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m =>
            string.Equals(Path.GetFileName(m.FileName), "e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(Path.GetFullPath(native.FileName), Path.Combine(folder, "e_sqlite3.dll"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite native runtime 不是來自擴充目錄。");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a =>
            a.GetName().Name?.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) == true || a.GetName().Name == "Microsoft.Data.Sqlite"))
            if (!string.Equals(Path.GetDirectoryName(assembly.Location), folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite provider 不是來自擴充目錄。");
        return "SQLite " + version + "；隔離 provider 與 native 路徑正確。";
    });

    private T Run<T>(long operation, Func<CancellationToken, T> action)
    {
        if (!_operations.TryGetValue(operation, out var source))
            throw new InvalidOperationException("SQL Memory 操作識別碼無效或已結束。");
        var token = source.Token;
        try { return action(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 例外帶的 token 屬於這個 AppDomain；只傳訊息回去，由隔離邊界換成呼叫端的 token。
            throw new OperationCanceledException("SQL Memory 操作已取消。");
        }
        catch (Exception error) { throw SqliteStorageErrors.Translate(error); }
    }

    private static T Run<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error)
        {
            // provider 例外未必能跨 AppDomain 序列化；轉成 Core 的分類例外，保留原始錯誤碼，不吞掉交易錯誤。
            throw SqliteStorageErrors.Translate(error);
        }
    }

    /// <summary>同一個資料庫檔案上的各個聚合；一起建立、一起隨 AppDomain 卸載。</summary>
    private sealed class Stores
    {
        public Stores(SqliteDatabase database, SqliteSearchBudget budget)
        {
            Database = database;
            Captures = new SqliteCaptureStore(database, budget);
            Favorites = new SqliteFavoriteStore(database, budget);
            Maintenance = new SqliteMaintenanceStore(database);
            Usage = new SqliteUsageStore(database);
            Leases = new SqliteLeaseStore(database);
        }

        public SqliteDatabase Database { get; }
        public SqliteCaptureStore Captures { get; }
        public SqliteFavoriteStore Favorites { get; }
        public SqliteMaintenanceStore Maintenance { get; }
        public SqliteUsageStore Usage { get; }
        public SqliteLeaseStore Leases { get; }
    }
}
