using System;
using System.Collections.Generic;
using SqlAssist.Core.Localization;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 過濾按鈕上那一句摘要：沒有勾、勾一個、勾很多三種寫法。
/// </summary>
/// <remarks>
/// SQL Memory 與 SQL Search 的每一顆過濾按鈕共用這一份。各寫一份的症狀是其中一邊在只勾一個時
/// 寫「1 個」——而那一刻按鈕上明明有位置寫得下那個名字，使用者卻要打開面板才知道自己選了什麼。
/// 數量那一句由呼叫端給：只剩數字的按鈕分不出那是幾種還是幾個，而量詞的位置與單複數各語言不同，
/// 要整句翻，不能拿數字接一個量詞。
/// </remarks>
internal static class SqlFilterSummary
{
    /// <param name="allLabel">一個都沒勾時的字；它是一個實際的預設，不是空白。</param>
    /// <param name="single">只勾一個時的那個名字；null 或空字串表示說不出來，退回數量。</param>
    /// <param name="countText">勾了好幾個時的那一句，例如 <c>ChromeText.DatabaseCount</c>。</param>
    public static string Of(int count, string allLabel, string? single, Func<int, string> countText)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (allLabel is null) throw new ArgumentNullException(nameof(allLabel));
        if (countText is null) throw new ArgumentNullException(nameof(countText));
        return count == 0 ? allLabel
            : single is { Length: > 0 } name ? name
            : countText(count);
    }

    /// <summary>勾起來的那幾個名稱；按鈕的 Tooltip 用它列出完整名單，摘要上只剩數量時才說得出是哪幾個。</summary>
    public static string Detail(IReadOnlyList<string> names, string? separator = null)
    {
        if (names is null) throw new ArgumentNullException(nameof(names));
        return names.Count == 0 ? "" : string.Join(separator ?? CommonText.ListSeparator, names);
    }
}
