using System;
using System.Runtime.InteropServices;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Dictionary;

[Guid("3b8e51c7-9a24-4f6d-b0e3-5c17d2a94f68")]
public sealed class SqlDictionaryToolWindow : SqlToolWindowPane
{
    public SqlDictionaryToolWindow()
    {
        Caption = DictionaryText.Caption;
    }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立資料字典工具窗", () =>
            Host.Content = new SqlDictionaryBrowser((SqlAssistPackage)Package));
        SqlLanguageSwitch.Changed += OnLanguageChanged;
    }

    /// <summary>
    /// 換語言時整份內容重建，模型交給新的一份。
    /// </summary>
    /// <remarks>
    /// 理由與 SQL Search 相同：建構時取字的元件（條件面板、膠囊、欄位標題、選單）一起換。
    /// 模型跟著走，還沒套用的說明變更才不會因為換了介面語言就不見。
    /// </remarks>
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        Caption = DictionaryText.Caption;
        if (Host.Content is not SqlDictionaryBrowser old) return;
        var model = old.Model;
        old.Dispose();
        Host.Content = new SqlDictionaryBrowser((SqlAssistPackage)Package, model);
    }

    internal static void Show(SqlAssistPackage package) =>
        Open<SqlDictionaryToolWindow>(package, DictionaryText.ToolWindowMissing, DictionaryText.OpenFailed,
            window => (window.Host.Content as SqlDictionaryBrowser)?.FocusFilter());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqlLanguageSwitch.Changed -= OnLanguageChanged;
            if (Host.Content is SqlDictionaryBrowser browser) browser.Dispose();
        }

        base.Dispose(disposing);
    }
}
