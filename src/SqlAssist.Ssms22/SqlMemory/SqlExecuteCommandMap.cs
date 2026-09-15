using System;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 「執行查詢」那個殼層命令的識別碼。
/// </summary>
/// <remarks>
/// 不寫死 GUID：SSMS 的查詢命令不在 <c>VSStd97</c> 也不在 <c>VSStd2K</c> 裡，猜錯的
/// 症狀是執行永遠擷取不到，而且沒有任何訊息。改成啟動時向殼層的命令名稱對應表
/// （<c>IVsCmdNameMapping</c>）以正式名稱換一次——那張表就是「工具 → 選項 → 環境 →
/// 鍵盤」列出來的同一份，使用者把 F5 改綁到別的鍵也仍然對得上。
///
/// 換不到就只是執行擷取不啟用，草稿擷取照常；一律留下紀錄，否則這是一種安靜的失效。
/// 解析只做一次，之後熱路徑上就只有一次靜態旗標讀取與兩次整數／GUID 比對。
/// </remarks>
internal static class SqlExecuteCommandMap
{
    /// <summary>SSMS 給執行查詢的正式名稱；與鍵盤設定頁上看到的字串相同。</summary>
    private const string CanonicalName = "Query.Execute";

    private static int _state;
    private static Guid _group;
    private static uint _commandId;

    /// <summary>已經換到命令識別碼；熱路徑只讀這一個旗標。</summary>
    private static bool Known => Volatile.Read(ref _state) == Resolved;

    private const int Unresolved = 0;
    private const int Resolving = 1;
    private const int Resolved = 2;
    private const int Failed = 3;

    /// <summary>只在 UI 執行緒呼叫（建立編輯器時）；重複呼叫只有第一次真的去問。</summary>
    public static void EnsureResolved(IServiceProvider? serviceProvider)
    {
        if (serviceProvider is null) return;
        if (Interlocked.CompareExchange(ref _state, Resolving, Unresolved) != Unresolved) return;

        var resolved = SqlAssistPlatformGuard.Probe("取得執行查詢的命令識別碼", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (serviceProvider.GetService(typeof(SVsCmdNameMapping)) is not IVsCmdNameMapping mapping)
            {
                SqlAssistDiagnostics.WriteAlways("取不到殼層命令名稱對應表，SQL Memory 不擷取執行事件");
                return false;
            }

            var group = Guid.Empty;
            uint commandId = 0;

            if (ErrorHandler.Failed(mapping.MapNameToGUIDID(CanonicalName, out group, out commandId)))
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"殼層不認得命令 {CanonicalName}，SQL Memory 不擷取執行事件");
                return false;
            }

            _group = group;
            _commandId = commandId;
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 已接上執行命令 {CanonicalName}：{group:B}/{commandId}");
            return true;
        }, fallback: false);

        Volatile.Write(ref _state, resolved ? Resolved : Failed);
    }

    /// <summary>這個命令是不是「執行查詢」。這是按鍵路徑，沒解析出來時只付一次旗標讀取。</summary>
    public static bool Matches(Guid group, uint commandId) =>
        Known && commandId == _commandId && group == _group;
}
