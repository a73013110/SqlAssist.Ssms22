using System;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 片段在浮動預覽裡顯示的內容：選下去之後實際插入的那一份文字。
/// </summary>
/// <remarks>
/// 讀的是 <see cref="SqlSnippet.Expansion"/>——欄位已經填了預設值、游標標記已經拿掉，
/// 與一般插入、原生 Expansion 共用同一次剖析。另外把樣板畫一次的話，預覽寫的是
/// <c>$table$</c>，插進去的卻是 <c>dbo.Lib_Reader</c>，兩邊對不上而且沒有任何徵兆。
/// </remarks>
public static class SqlSnippetPreview
{
    /// <summary>浮動預覽裡顯示與複製的整份文字；換行統一成 LF，結尾的空白不算。</summary>
    public static string Text(SqlSnippet snippet)
    {
        if (snippet is null)
        {
            throw new ArgumentNullException(nameof(snippet));
        }

        return snippet.Expansion.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
    }
}
