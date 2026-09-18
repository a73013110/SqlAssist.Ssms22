using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Ssms22.Notifications;

namespace SqlAssist.Ssms22.Search;

[Guid("7d2f8c14-6b3a-4e91-9c05-2a8f4d61b7e3")]
public sealed class SqlSearchToolWindow : ToolWindowPane
{
    private readonly ContentControl _host = new();
    private IDisposable? _notifications;

    // 自己包一層 AdornerDecorator：沒有的話通知卡片會掛到殼層主視窗的圖層，不受工具窗邊界裁切。
    public SqlSearchToolWindow() : base(null)
    {
        Caption = "SQL Search";
        Content = new AdornerDecorator { Child = _host };
    }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立 SQL Search 工具窗", () =>
        {
            _host.Content = new SqlSearchBrowser((SqlAssistPackage)Package);
            _notifications = NotificationWindowHost.Register(_host);
        });
    }

    internal static void Show(SqlAssistPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 主動命令失敗必須可見，不交給 Guard 吞掉。
        try
        {
            var pane = package.FindToolWindow(typeof(SqlSearchToolWindow), 0, true);
            if (pane?.Frame is not IVsWindowFrame frame)
                throw new InvalidOperationException("SSMS 未建立 SQL Search 工具窗。");
            ErrorHandler.ThrowOnFailure(frame.Show());
            if (pane is SqlSearchToolWindow window && window._host.Content is SqlSearchBrowser browser)
                browser.FocusSearch();
        }
        catch (Exception error)
        {
            VsShellUtilities.ShowMessageBox(package, error.Message, "開啟 SQL Search 失敗",
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifications?.Dispose();
            if (_host.Content is SqlSearchBrowser browser) browser.Dispose();
        }
        base.Dispose(disposing);
    }
}
