using System;
using System.Reflection;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Hosting;

/// <summary>代理回程以組件名稱解析型別；只映射已載入的 Query Memory 契約，不接管宿主相依。</summary>
internal sealed class QueryMemoryRemotingScope : IDisposable
{
    private readonly ResolveEventHandler _resolve;

    public QueryMemoryRemotingScope()
    {
        var hosting = typeof(SqliteWorker).Assembly;
        var core = typeof(QueryContent).Assembly;
        _resolve = (_, request) => Resolve(request.Name, hosting, core);
        AppDomain.CurrentDomain.AssemblyResolve += _resolve;
    }

    private static Assembly? Resolve(string name, Assembly hosting, Assembly core)
    {
        // 不讀檔、不猜版本，也不處理 SQLitePCLRaw 或 System.*，避免污染 SSMS 的載入政策。
        if (string.Equals(name, hosting.FullName, StringComparison.Ordinal)) return hosting;
        if (string.Equals(name, core.FullName, StringComparison.Ordinal)) return core;
        return null;
    }

    public void Dispose() => AppDomain.CurrentDomain.AssemblyResolve -= _resolve;
}
