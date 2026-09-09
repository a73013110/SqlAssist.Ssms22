using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 請殼層叫用一次 <c>Edit.ParameterInfo</c>（Ctrl+Shift+Space）。
/// </summary>
/// <remarks>
/// 提交建議時補上的括號是這一次編輯寫進去的，不是使用者敲出來的按鍵，
/// 沒有經過殼層的命令派送——SSMS 的「陳述式完成 → 參數資訊」掛在
/// <c>TYPECHAR</c> 那條路上，因此不會自己浮出來。差的只有那一次派送，
/// 所以這裡只補那一次，不自己畫任何東西。
///
/// 用 <c>PostExecCommand</c> 而不是自己找命令目標：這一次呼叫發生在提交的中途，
/// 文字與 completion session 都還沒定案，而 Post 本來就是排到目前這一輪之後才執行，
/// 不必再多包一層排程。命令送到目前有焦點的那個視窗，也就是剛剛提交的那個編輯器。
///
/// 沒有東西可顯示時是沒有作用的一次，不是失敗：SSMS 那一份參數資訊只涵蓋內建函式
/// （2026-09 實機量測，使用者自訂函式連 Ctrl+Shift+Space 明示叫用都沒有反應），
/// 而使用者也可能在「設定 → 語言 → Transact-SQL → 一般」把它整個關掉。
/// 兩種情形都是命令自己回報未支援，不需要在這裡先判斷一次。
/// </remarks>
internal static class SqlShellParameterInfo
{
    public static void Request()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // 叫不出來只代表少了一個提示，其餘照常——為了它讓整次提交失敗不值得。
        SqlAssistPlatformGuard.Run("叫用參數資訊", () =>
        {
            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) is not IVsUIShell shell)
            {
                return;
            }

            var group = VSConstants.VSStd2K;
            object? argument = null;

            shell.PostExecCommand(
                ref group,
                (uint)VSConstants.VSStd2KCmdID.PARAMINFO,
                0,
                ref argument);
        });
    }
}
