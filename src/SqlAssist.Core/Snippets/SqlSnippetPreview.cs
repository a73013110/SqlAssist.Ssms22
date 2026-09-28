using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 片段在說明面板與浮動預覽裡顯示的內容：選下去之後實際插入的那一份文字。
/// </summary>
/// <remarks>
/// 讀的是 <see cref="SqlSnippet.Expansion"/>——欄位已經填了預設值、游標標記已經拿掉，
/// 與一般插入、原生 Expansion 共用同一次剖析。另外把樣板畫一次的話，預覽寫的是
/// <c>$table$</c>，插進去的卻是 <c>dbo.Lib_Reader</c>，兩邊對不上而且沒有任何徵兆。
///
/// 兩個表面的分工與內建說明相同：說明面板給一眼看得完的份量，看不完的交給捲得動、選得起來
/// 的浮動預覽。多數片段只有幾行，說明面板就印完了，那時向右鍵照常右移游標。
/// </remarks>
public static class SqlSnippetPreview
{
    /// <summary>說明面板最多印幾行程式碼；超過才值得開浮動預覽。</summary>
    public const int PanelLines = 10;

    /// <summary>實際會插入的文字，一行一格；結尾的空白行不算。</summary>
    public static IReadOnlyList<string> Lines(SqlSnippet snippet)
    {
        if (snippet is null)
        {
            throw new ArgumentNullException(nameof(snippet));
        }

        var lines = new List<string>(snippet.Expansion.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

        while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>浮動預覽裡顯示與複製的整份文字。</summary>
    public static string Text(SqlSnippet snippet) => string.Join("\n", Lines(snippet));

    /// <summary>說明面板印不完：多出來的那幾行只有浮動預覽裝得下。</summary>
    public static bool DeservesWindow(SqlSnippet snippet) => Lines(snippet).Count > PanelLines;
}
