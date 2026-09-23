using System;
using System.Globalization;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 查詢視窗的「新增至收藏」：把編輯中的 SQL 交給收藏對話框。
/// </summary>
/// <remarks>
/// 手上沒有 <see cref="SqlMemoryRow"/>，也不先產生一筆擷取——擷取是設定可以關掉的背景行為，
/// 而收藏是使用者當場按下去的動作，不該因為草稿擷取關著就收不起來，也不該順手在 History 多留一筆。
/// 收藏因此自己建立一份不屬於任何 Session 的版本，契約見
/// <see cref="ISqlFavoriteStore.SaveFavoriteAsync"/>。
/// </remarks>
internal static class SqlMemoryFavoriteAction
{
    /// <summary>命令狀態：SqlAssist 與 SQL Memory 都開著、有 SQL 編輯器，而且有東西可以收。</summary>
    /// <remarks>
    /// 開選單時每一項都會問一次，所以只看長度，不在這裡展開全文；只有空白字元要等到真的按下去才擋得掉。
    /// </remarks>
    public static bool IsAvailable() => IsAvailable(ActiveSqlEditor.Current);

    public static bool IsAvailable(IWpfTextView? view) =>
        SqlAssistSettingsStore.Current.Enabled && SqlMemoryHost.Runtime.IsAvailable &&
        view is { IsClosed: false } editor && Length(editor) > 0;

    /// <summary>開啟收藏對話框。</summary>
    /// <returns>要寫到狀態列的一句話；使用者取消時是空字串。</returns>
    public static string Begin(IWpfTextView view, SqlAssistPackage package)
    {
        if (view is null || view.IsClosed) return "查詢視窗已關閉。";

        // 有選取就收選取，與選取執行同一條界線；沒有選取才是整份文件。
        var selection = SqlCaptureTracker.SelectedText(view.Selection);
        var sql = selection is { } selected ? selected.GetText() : view.TextBuffer.CurrentSnapshot.GetText();
        // 與擷取同一份判斷：收得起來的與記得下來的，不該是兩套標準。
        if (SqlContent.IsBlank(sql))
            return selection is null ? "查詢視窗沒有可以收藏的 SQL。" : "選取範圍沒有可以收藏的 SQL。";

        return FavoriteEditorWindow.Create(package, sql, ActiveSqlEditor.GetDocumentName(view.TextBuffer),
            Connection(view, package), null, Summary(sql, selection is not null))
            ? SqlMemoryItemCommands.AddedToFavorites : "";
    }

    /// <summary>收藏的是選取範圍還是整份查詢，只有使用者自己看得出來對不對，所以寫在對話框第一列。</summary>
    private static string Summary(string sql, bool selected)
    {
        var lines = 1;
        foreach (var character in sql) if (character == '\n') lines++;
        var size = string.Format(CultureInfo.CurrentCulture, "{0:N0} 行、{1:N0} 字元", lines, sql.Length);
        return selected ? $"收藏選取範圍（{size}）。" : $"收藏整份查詢（{size}）。";
    }

    private static int Length(IWpfTextView view)
    {
        if (view.Selection.IsEmpty) return view.TextBuffer.CurrentSnapshot.Length;
        var length = 0;
        foreach (var span in view.Selection.SelectedSpans) length += span.Length;
        return length;
    }

    /// <summary>連線讀快取那一份，與擷取同源；按鍵與選單路徑都不向 SSMS 同步問連線。</summary>
    private static SqlConnectionLabel? Connection(IWpfTextView view, IServiceProvider services) =>
        SqlWindowConnections.Get(SqlAssistPlatformGuard.Probe("取得查詢視窗識別",
            () => SqlCompletionServices.GetMetadataService(view, services).EditorMoniker, fallback: null));
}
