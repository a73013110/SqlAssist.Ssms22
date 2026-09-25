using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Search;

[Guid("7d2f8c14-6b3a-4e91-9c05-2a8f4d61b7e3")]
public sealed class SqlSearchToolWindow : ToolWindowPane
{
    private readonly ContentControl _host = new();

    public SqlSearchToolWindow() : base(null)
    {
        Caption = "SQL Search";
        Content = _host;
    }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立 SQL Search 工具窗", () =>
            _host.Content = new SqlSearchBrowser((SqlAssistPackage)Package));
        SqlLanguageSwitch.Changed += OnLanguageChanged;
    }

    /// <summary>
    /// 換語言時整份內容重建，只帶搜尋字串過去。
    /// </summary>
    /// <remarks>
    /// 逐一改字要每個元件各記得自己的文字從哪來，漏一個就是半新半舊；重建讓建構時取字的那些
    /// （篩選面板、分段開關、選單、朗讀名稱）一起換。範圍與結果跟著搜尋字串重搜，比對方式本來就存在狀態裡。
    /// </remarks>
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (_host.Content is not SqlSearchBrowser old) return;
        var searchText = old.SearchText;
        var focused = old.IsKeyboardFocusWithin;
        old.Dispose();
        var browser = new SqlSearchBrowser((SqlAssistPackage)Package) { SearchText = searchText };
        _host.Content = browser;
        if (focused) browser.FocusSearch();
    }

    internal static void Show(SqlAssistPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 主動命令失敗必須可見，不交給 Guard 吞掉。
        try
        {
            var pane = package.FindToolWindow(typeof(SqlSearchToolWindow), 0, true);
            if (pane?.Frame is not IVsWindowFrame frame)
                throw new InvalidOperationException(SqlSearchText.ToolWindowMissing);
            ErrorHandler.ThrowOnFailure(frame.Show());
            if (pane is SqlSearchToolWindow window && window._host.Content is SqlSearchBrowser browser)
                browser.FocusSearch();
        }
        catch (Exception error)
        {
            VsShellUtilities.ShowMessageBox(package, error.Message, SqlSearchText.OpenFailed,
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqlLanguageSwitch.Changed -= OnLanguageChanged;
            if (_host.Content is SqlSearchBrowser browser) browser.Dispose();
        }

        base.Dispose(disposing);
    }
}
