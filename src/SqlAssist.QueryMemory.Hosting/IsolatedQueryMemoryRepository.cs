using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Hosting;

/// <summary>只隔離 SQLite provider 的靜態狀態與 binding redirect；不另造通用宿主框架。</summary>
public sealed class IsolatedQueryMemoryRepository : IQueryMemoryRepository, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppDomain _domain;
    private readonly SqliteWorker _worker;
    private bool _disposed;

    private IsolatedQueryMemoryRepository(AppDomain domain, SqliteWorker worker) { _domain = domain; _worker = worker; }

    public static Task<IsolatedQueryMemoryRepository> OpenAsync(string databasePath, string? ssmsIdeDirectory,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var folder = Path.GetDirectoryName(typeof(IsolatedQueryMemoryRepository).Assembly.Location)
                ?? throw new InvalidOperationException("找不到 Query Memory 組件目錄。");
            var config = Path.Combine(folder, "QueryMemory.Sqlite.config");
            if (!File.Exists(config)) throw new FileNotFoundException("缺少 Query Memory 隔離載入設定。", config);
            var domain = AppDomain.CreateDomain("SqlAssist.QueryMemory." + Guid.NewGuid().ToString("N"), null,
                new AppDomainSetup { ApplicationBase = folder, ConfigurationFile = config });
            try
            {
                var worker = (SqliteWorker)domain.CreateInstanceAndUnwrap(typeof(SqliteWorker).Assembly.FullName, typeof(SqliteWorker).FullName);
                worker.Initialize(databasePath, ssmsIdeDirectory);
                return new IsolatedQueryMemoryRepository(domain, worker);
            }
            catch { AppDomain.Unload(domain); throw; }
        }, cancellationToken);
    }

    public Task<QuerySessionState?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Invoke(() => _worker.ReadSession(sessionId), cancellationToken);
    public Task<QueryMemoryCommitResult> CommitAsync(QueryMemoryWrite write, CancellationToken cancellationToken) =>
        Invoke(() => _worker.Commit(write), cancellationToken);
    public Task<QueryMemoryPage<QueryHistoryItem>> ReadHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken) =>
        Invoke(() => _worker.ReadHistory(request), cancellationToken);
    public Task<QueryContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken) =>
        Invoke(() => _worker.ReadContent(contentId), cancellationToken);
    public Task<string> ProbeAsync(CancellationToken cancellationToken) => Invoke(() => _worker.Probe(), cancellationToken);

    private async Task<T> Invoke<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        // 等待同一 worker 時不占住 ThreadPool 執行緒，避免大量預覽取消造成執行緒飢餓。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IsolatedQueryMemoryRepository));
            cancellationToken.ThrowIfCancellationRequested();
            // 避免取消與跨 AppDomain commit 競賽：派送後以交易結果為準，不宣稱已取消成功寫入。
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>宿主先排空 BackgroundWriter，再於背景卸載此 AppDomain。</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            AppDomain.Unload(_domain);
        }
        finally { _gate.Release(); }
    }
}
