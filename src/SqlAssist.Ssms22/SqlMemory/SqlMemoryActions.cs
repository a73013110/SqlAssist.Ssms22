using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Ssms22.Commands;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

internal static class SqlMemoryActions
{
    // 這是使用者動作的可見錯誤邊界；不把儲存失敗轉成「成功但沒有資料」。
    public static async Task RunAsync(Func<Task> action, Action<string> report)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            SqlAssistDiagnostics.WriteAlways("SQL Memory 操作失敗：" + error.Message);
            report(error.Message);
        }
    }

    public static void Run(Action action, Action<string> report) =>
        _ = RunAsync(() => { action(); return Task.CompletedTask; }, report);

    public static void OpenQuery(SqlAssistPackage package, string sql)
    {
        var view = SsmsScriptWindow.TryCreateBlankQuery(package, out var failure);
        if (view is null) throw new InvalidOperationException(failure);
        if (!new TextViewEditCoordinator(view).InsertIntoBlank(new TextReplacement(sql,
                SqlAssistActivityKind.SqlMemoryOpened, "已從 SQL Memory 開啟 SQL；未執行。", caretOffset: 0)))
            throw new InvalidOperationException("新查詢不是空白或已關閉；未寫入 SQL。");
    }

    public static void ConfigureWindow(Window window, SqlAssistPackage package, string title, double width, double height)
    {
        SqlAssistDialogs.Configure(window, title, width, height, minWidth: 480, minHeight: 320);
        if (((IServiceProvider)package).GetService(typeof(SVsUIShell)) is IVsUIShell shell)
        {
            ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
            new WindowInteropHelper(window).Owner = owner;
        }
    }

    public static void OpenSettings(SqlAssistPackage package)
    {
        if (!SqlAssistCommands.TryOpenSettings(package))
            throw new InvalidOperationException("請到工具 → 選項搜尋 SqlAssist。");
    }
}
