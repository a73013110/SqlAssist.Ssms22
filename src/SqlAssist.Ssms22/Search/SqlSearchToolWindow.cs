using System;
using System.Runtime.InteropServices;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Search;

[Guid("7d2f8c14-6b3a-4e91-9c05-2a8f4d61b7e3")]
public sealed class SqlSearchToolWindow : SqlToolWindowPane
{
    public SqlSearchToolWindow()
    {
        Caption = "SQL Search";
    }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立 SQL Search 工具窗", () =>
            Host.Content = new SqlSearchBrowser((SqlAssistPackage)Package));
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
        if (Host.Content is not SqlSearchBrowser old) return;
        var searchText = old.SearchText;
        var focused = old.IsKeyboardFocusWithin;
        old.Dispose();
        var browser = new SqlSearchBrowser((SqlAssistPackage)Package) { SearchText = searchText };
        Host.Content = browser;
        if (focused) browser.FocusSearch();
    }

    internal static void Show(SqlAssistPackage package) =>
        Open<SqlSearchToolWindow>(package, SqlSearchText.ToolWindowMissing, SqlSearchText.OpenFailed,
            window => (window.Host.Content as SqlSearchBrowser)?.FocusSearch());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqlLanguageSwitch.Changed -= OnLanguageChanged;
            if (Host.Content is SqlSearchBrowser browser) browser.Dispose();
        }

        base.Dispose(disposing);
    }
}
