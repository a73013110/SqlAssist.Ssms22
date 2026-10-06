using System;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// Ctrl＋點擊時，滑鼠下哪一段文字算是可以點的連結。
/// </summary>
/// <remarks>
/// 這一關跑在滑鼠移動的路徑上，所以先只看<b>那一行</b>，也不問中繼資料：和 F12 的第一關
/// 一樣寬鬆，名稱到底認不認得由點下去之後的背景解析回答。寬鬆的代價是偶爾點了一個
/// 查不到的名稱、狀態列說一句；嚴格的代價是底線只在快取剛好載過的名稱上出現。
///
/// 連結蓋住的是點下去會打開的那一整個東西：<c>ALTER TABLE</c> 兩個字是一份語句說明，
/// <c>attachments.Attachments</c> 停在哪一段都是那張表，所以各是一條底線。唯一要讀整份文字
/// 的情形是停在一串名稱的第一段上——那一段是別名（<c>l.CopyNo</c> 的 <c>l</c>，後面是欄位）
/// 還是結構描述（<c>dbo.Loan</c> 的 <c>dbo</c>，後面是同一個物件），只有範圍分得出來；
/// 其餘位置連整份文字都不取。
///
/// 只擋一定不是物件的兩種：沒加括號的保留字（<c>SELECT</c>、<c>FROM</c> 當名字寫就是
/// 語法錯誤，加底線只是騙人去點），以及內建名稱——內建函式在結構預覽裡有自己的說明，
/// 但沒有定義可以開。非保留的關鍵字不擋，<c>Type</c>、<c>Status</c> 這類欄位名很常見。
/// </remarks>
public static class SqlClickTarget
{
    /// <summary>
    /// 取得 <paramref name="column"/> 所在、可以點的連結在那一行裡的範圍；不是的話回傳 null。
    /// </summary>
    /// <param name="lineText">滑鼠所在的那一行。</param>
    /// <param name="column">滑鼠在那一行裡的位置。</param>
    /// <param name="includeBuiltIns">
    /// 這個動作答得出內建名稱嗎：結構預覽答得出（開完整說明），定義答不出。
    /// </param>
    /// <param name="documentText">整份文字；只有停在一串名稱的第一段上才會取。</param>
    /// <param name="lineStart">這一行在整份文字裡的起點。</param>
    public static SqlTextSpan? FindAt(
        string lineText,
        int column,
        bool includeBuiltIns,
        Func<string> documentText,
        int lineStart)
    {
        if (documentText is null)
        {
            throw new ArgumentNullException(nameof(documentText));
        }

        var reference = SqlIdentifierScanner.FindNameAt(
            lineText,
            column,
            first => SqlScopeAnalyzer.NamesColumnOwner(documentText(), lineStart + first.Start, first.Name));

        if (reference is null)
        {
            return null;
        }

        // 內建名稱先判：CONVERT、DATEADD 不是保留字，放到下一關會被當成物件名稱。
        if (SqlBuiltInDocCatalog.TryGetAt(lineText, reference, out var doc, out var span))
        {
            // 裝不滿一個視窗的內建名稱，Ctrl+F12 也不開（見 SqlBuiltInDoc.DeservesWindow）。
            return includeBuiltIns && doc.DeservesWindow ? span : null;
        }

        var bare = reference.Qualifier is null && reference.Length == reference.Name.Length;
        return bare && SqlKeywordCatalog.IsReservedIdentifier(reference.Name) ? null : reference.Span;
    }
}
