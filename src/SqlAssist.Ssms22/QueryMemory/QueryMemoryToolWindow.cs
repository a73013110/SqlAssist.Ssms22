using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlAssist.Ssms22.QueryMemory;

[Guid("0b670847-3f0c-4523-b5ce-e987833ade19")]
public sealed class QueryMemoryToolWindow : ToolWindowPane
{
    private readonly ContentControl _host = new();
    public QueryMemoryToolWindow() : base(null) { Caption = "查詢記憶"; Content = _host; }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立查詢記憶工具窗", () =>
            _host.Content = new QueryMemoryBrowser((SqlAssistPackage)Package));
    }

    internal static void Show(SqlAssistPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 主動命令失敗必須可見，不交給 Guard 吞掉。
        try
        {
            var pane = package.FindToolWindow(typeof(QueryMemoryToolWindow), 0, true);
            if (pane?.Frame is not IVsWindowFrame frame)
                throw new InvalidOperationException("SSMS 未建立查詢記憶工具窗。");
            ErrorHandler.ThrowOnFailure(frame.Show());
        }
        catch (Exception error)
        {
            VsShellUtilities.ShowMessageBox(package, error.Message, "開啟查詢記憶失敗",
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _host.Content is QueryMemoryBrowser browser) browser.Dispose();
        base.Dispose(disposing);
    }
}
