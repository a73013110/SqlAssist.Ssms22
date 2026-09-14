using System;
using System.Reflection;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Isolation;

/// <summary>代理回程以組件名稱解析型別；只映射已載入的 SQL Memory 契約，不接管宿主相依。</summary>
internal sealed class IsolationAssemblyResolveScope : IDisposable
{
    private readonly ResolveEventHandler _resolve;

    public IsolationAssemblyResolveScope()
    {
        var isolation = typeof(SqlMemoryIsolatedWorker).Assembly;
        var core = typeof(SqlContent).Assembly;
        _resolve = (_, request) => Resolve(request.Name, isolation, core);
        AppDomain.CurrentDomain.AssemblyResolve += _resolve;
    }

    private static Assembly? Resolve(string name, Assembly isolation, Assembly core)
    {
        // 不讀檔、不猜版本，也不處理 SQLitePCLRaw 或 System.*，避免污染 SSMS 的載入政策。
        if (string.Equals(name, isolation.FullName, StringComparison.Ordinal)) return isolation;
        if (string.Equals(name, core.FullName, StringComparison.Ordinal)) return core;
        return null;
    }

    public void Dispose() => AppDomain.CurrentDomain.AssemblyResolve -= _resolve;
}
