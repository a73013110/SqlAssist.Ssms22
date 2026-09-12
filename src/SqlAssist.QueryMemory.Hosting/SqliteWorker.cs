using System;
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
public sealed class SqliteWorker : MarshalByRefObject
{
    private SqliteQueryMemoryRepository? _repository;
    private string? _databasePath;
    private SqliteQueryMemoryRepository Repository => _repository ?? throw new InvalidOperationException("尚未初始化 SQLite worker。");

    public override object? InitializeLifetimeService() => null;

    public void Initialize(string path, string? ssmsIdeDirectory)
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
        InitializeStorage(path);
        Probe();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InitializeStorage(string path)
    {
        Run(() =>
        {
            _repository = SqliteQueryMemoryRepository.OpenAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            _databasePath = path;
            return true;
        });
    }

    public QuerySessionState? ReadSession(Guid sessionId) => Run(() => Repository.ReadSessionAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult());
    public QueryMemoryCommitResult Commit(QueryMemoryWrite write) => Run(() => Repository.CommitAsync(write, CancellationToken.None).GetAwaiter().GetResult());
    public QueryMemoryPage<QueryHistoryItem> ReadHistory(QueryHistoryRequest request) => Run(() => Repository.ReadHistoryAsync(request, CancellationToken.None).GetAwaiter().GetResult());
    public QueryContent? ReadContent(string contentId) => Run(() => Repository.ReadContentAsync(contentId, CancellationToken.None).GetAwaiter().GetResult());

    public SavedQueryEntry? ReadSavedQuery(Guid id) => Run(() => Repository.ReadSavedQueryAsync(id, CancellationToken.None).GetAwaiter().GetResult());
    public QueryMemoryPage<SavedQueryEntry> ReadSavedQueries(SavedQueryRequest request) => Run(() => Repository.ReadSavedQueriesAsync(request, CancellationToken.None).GetAwaiter().GetResult());
    public SavedQueryWriteResult WriteSavedQuery(SavedQueryWrite write) => Run(() => Repository.WriteSavedQueryAsync(write, CancellationToken.None).GetAwaiter().GetResult());
    public SavedQueryWriteResult DeleteSavedQuery(Guid id, Guid version) => Run(() => Repository.DeleteSavedQueryAsync(id, version, CancellationToken.None).GetAwaiter().GetResult());
    public SavedQueryWriteResult EditSavedQuerySql(SavedQueryEdit edit) => Run(() => Repository.EditSavedQuerySqlAsync(edit, CancellationToken.None).GetAwaiter().GetResult());

    public QueryMemoryUsage ReadUsage() => Run(() => Repository.ReadUsageAsync(CancellationToken.None).GetAwaiter().GetResult());
    public QueryMemoryMaintenanceResult Maintain(QueryMemoryMaintenanceRequest request) =>
        Run(() => Repository.MaintainAsync(request, CancellationToken.None).GetAwaiter().GetResult());

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

    private static T Run<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error)
        {
            // provider 例外未必能跨 AppDomain 序列化；轉成 BCL 例外並保留失敗，不吞掉交易錯誤。
            throw new InvalidOperationException("Query Memory 儲存失敗（" + error.GetType().Name + "）：" + error.GetBaseException().Message);
        }
    }
}
