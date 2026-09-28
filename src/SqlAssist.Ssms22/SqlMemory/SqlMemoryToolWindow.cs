using System;
using System.Runtime.InteropServices;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>工具窗開啟時要顯示的頁面；命令與通知共用。</summary>
internal enum SqlMemoryPage { History, Favorites, Usage }

[Guid("0b670847-3f0c-4523-b5ce-e987833ade19")]
public sealed class SqlMemoryToolWindow : SqlToolWindowPane
{
    public SqlMemoryToolWindow() { Caption = "SQL Memory"; }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立 SQL Memory 工具窗", () =>
            Host.Content = new SqlMemoryBrowser((SqlAssistPackage)Package));
        SqlLanguageSwitch.Changed += OnLanguageChanged;
    }

    /// <summary>換語言時整份內容重建，帶著分頁與搜尋字串；理由同 SQL Search 工具窗。</summary>
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Host.Content is not SqlMemoryBrowser old) return;
        var page = old.Page;
        var searchText = old.SearchText;
        old.Dispose();
        var browser = new SqlMemoryBrowser((SqlAssistPackage)Package);
        browser.Restore(page, searchText);
        Host.Content = browser;
    }

    internal static void Show(SqlAssistPackage package, SqlMemoryPage page = SqlMemoryPage.History) =>
        Open<SqlMemoryToolWindow>(package, SqlMemoryUiText.ToolWindowMissing, SqlMemoryUiText.OpenFailedTitle,
            window => (window.Host.Content as SqlMemoryBrowser)?.ShowPage(page));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqlLanguageSwitch.Changed -= OnLanguageChanged;
            if (Host.Content is SqlMemoryBrowser browser) browser.Dispose();
        }

        base.Dispose(disposing);
    }
}
