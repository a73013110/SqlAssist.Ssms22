using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlAssist.Ssms22.SqlMemory;

[Guid("0b670847-3f0c-4523-b5ce-e987833ade19")]
public sealed class SqlMemoryToolWindow : ToolWindowPane
{
    private readonly ContentControl _host = new();
    public SqlMemoryToolWindow() : base(null) { Caption = "SQL Memory"; Content = _host; }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立 SQL Memory 工具窗", () =>
            _host.Content = new SqlMemoryBrowser((SqlAssistPackage)Package));
    }

    internal static void Show(SqlAssistPackage package, bool favorites = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 主動命令失敗必須可見，不交給 Guard 吞掉。
        try
        {
            var pane = package.FindToolWindow(typeof(SqlMemoryToolWindow), 0, true);
            if (pane?.Frame is not IVsWindowFrame frame)
                throw new InvalidOperationException("SSMS 未建立 SQL Memory 工具窗。");
            ErrorHandler.ThrowOnFailure(frame.Show());
            if (pane is SqlMemoryToolWindow window && window._host.Content is SqlMemoryBrowser browser)
                browser.ShowPage(favorites);
        }
        catch (Exception error)
        {
            VsShellUtilities.ShowMessageBox(package, error.Message, "開啟 SQL Memory 失敗",
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _host.Content is SqlMemoryBrowser browser) browser.Dispose();
        base.Dispose(disposing);
    }
}
