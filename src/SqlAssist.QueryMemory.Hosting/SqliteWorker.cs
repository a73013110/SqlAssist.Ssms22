using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using SqlAssist.Core.QueryMemory;
using SqlAssist.QueryMemory.Sqlite;

namespace SqlAssist.QueryMemory.Hosting;

/// <summary>只在隔離 AppDomain 建立；跨界僅傳遞可序列化的 Core DTO，不傳 provider 物件。</summary>
/// <remarks>
/// 可由多條執行緒同時呼叫：repository 每個操作各開連線，並行交給 SQLite WAL 與交易。
/// <see cref="CancellationToken"/> 無法跨 AppDomain，呼叫端先以 <see cref="BeginOperation"/> 取得識別碼，
/// 取消時呼叫 <see cref="CancelOperation"/>；token 在 worker 端建立，KMP 掃描與交易內檢查才接得到。
/// </remarks>
public sealed class SqliteWorker : MarshalByRefObject
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _operations = new();
    private long _nextOperation;
    private SqliteQueryMemoryRepository? _repository;
    private string? _databasePath;
    private SqliteQueryMemoryRepository Repository => _repository ?? throw new InvalidOperationException("尚未初始化 SQLite worker。");

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
            _repository = SqliteQueryMemoryRepository.Open(path, CancellationToken.None, busyTimeoutSeconds,
                searchCandidates.HasValue || searchBytes.HasValue
                    ? new SqliteSearchBudget(searchCandidates ?? SqliteSearchBudget.Default.Candidates, searchBytes ?? SqliteSearchBudget.Default.Bytes)
                    : null);
            _databasePath = path;
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

    public QuerySessionState? ReadSession(long operation, Guid sessionId) => Run(operation, token => Repository.ReadSession(sessionId, token));
    public QueryMemoryCommitResult Commit(long operation, QueryMemoryWrite write, string? leaseId) =>
        Run(operation, token => Repository.Commit(write, leaseId, token));
    public QueryMemoryPage<QueryHistoryItem> ReadHistory(long operation, QueryHistoryRequest request) =>
        Run(operation, token => Repository.ReadHistory(request, token));
    public string[] ReadConnectionFacets(long operation, QueryConnectionFacetRequest request) =>
        Run(operation, token => Repository.ReadConnectionFacets(request, token));
    public QueryContent? ReadContent(long operation, string contentId) => Run(operation, token => Repository.ReadContent(contentId, token));

    public FavoriteQueryEntry? ReadFavoriteQuery(long operation, Guid id) => Run(operation, token => Repository.ReadFavoriteQuery(id, token));
    public QueryMemoryPage<FavoriteQueryEntry> ReadFavoriteQueries(long operation, FavoriteQueryRequest request) =>
        Run(operation, token => Repository.ReadFavoriteQueries(request, token));
    public FavoriteQueryWriteResult WriteFavoriteQuery(long operation, FavoriteQueryWrite write) =>
        Run(operation, token => Repository.WriteFavoriteQuery(write, token));
    public FavoriteQueryWriteResult DeleteFavoriteQuery(long operation, Guid id, Guid version) =>
        Run(operation, token => Repository.DeleteFavoriteQuery(id, version, token));
    public FavoriteQueryWriteResult EditFavoriteQuerySql(long operation, FavoriteQueryEdit edit) =>
        Run(operation, token => Repository.EditFavoriteQuerySql(edit, token));

    public QueryMemoryUsage ReadUsage(long operation) => Run(operation, token => Repository.ReadUsage(token));
    public QueryMemoryMaintenanceResult Maintain(long operation, QueryMemoryMaintenanceRequest request) =>
        Run(operation, token => Repository.Maintain(request, token));
    public QueryMemoryMaintenanceState? ReadMaintenanceState(long operation) =>
        Run(operation, token => Repository.ReadMaintenanceState(token));
    public string OpenLease(long operation, QueryMemoryLeaseOwner owner, DateTimeOffset now) =>
        Run(operation, token => Repository.OpenLease(owner, now, token));
    public bool RenewLease(long operation, DateTimeOffset now) => Run(operation, token => Repository.RenewLease(now, token));
    public IReadOnlyList<QueryMemoryLease> ReadExpiredLeases(long operation, DateTimeOffset before, int limit) =>
        Run(operation, token => Repository.ReadExpiredLeases(before, limit, token));
    public int ReleaseLeases(long operation, IReadOnlyList<string> leaseIds, DateTimeOffset before) =>
        Run(operation, token => Repository.ReleaseLeases(leaseIds, before, token));
    public bool TryAcquireMaintenanceLease(long operation, QueryMemoryLeaseOwner owner, DateTimeOffset now, DateTimeOffset before) =>
        Run(operation, token => Repository.TryAcquireMaintenanceLease(owner, now, before, token));
    public bool ReleaseMaintenanceLease(long operation, QueryMemoryLeaseOwner owner) =>
        Run(operation, token => Repository.ReleaseMaintenanceLease(owner, token));

    public QueryMemoryCheckpointResult Checkpoint(long operation) => Run(operation, token => Repository.Checkpoint(token));
    public QueryMemoryUsage Compact(long operation) => Run(operation, token => Repository.Compact(token));

    public string Probe() => Run(() =>
    {
        var version = SqliteRuntime.Probe(_databasePath ?? throw new InvalidOperationException("未初始化。"));
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
}
