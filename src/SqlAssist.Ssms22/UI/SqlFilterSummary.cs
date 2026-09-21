using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 過濾按鈕上那一句摘要：沒有勾、勾一個、勾很多三種寫法。
/// </summary>
/// <remarks>
/// SQL Memory 與 SQL Search 的每一顆過濾按鈕共用這一份。各寫一份的症狀是其中一邊在只勾一個時
/// 寫「1 個」——而那一刻按鈕上明明有位置寫得下那個名字，使用者卻要打開面板才知道自己選了什麼。
/// 量詞由呼叫端給：只剩數字的按鈕分不出那是幾種還是幾個。
/// </remarks>
internal static class SqlFilterSummary
{
    /// <param name="allLabel">一個都沒勾時的字；它是一個實際的預設，不是空白。</param>
    /// <param name="single">只勾一個時的那個名字；null 或空字串表示說不出來，退回數量。</param>
    /// <param name="unit">數量的量詞，例如「 個」「 種」。</param>
    public static string Of(int count, string allLabel, string? single, string unit)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (allLabel is null) throw new ArgumentNullException(nameof(allLabel));
        if (unit is null) throw new ArgumentNullException(nameof(unit));
        return count == 0 ? allLabel
            : single is { Length: > 0 } name ? name
            : count.ToString(CultureInfo.InvariantCulture) + unit;
    }

    /// <summary>勾起來的那幾個名稱；按鈕的 Tooltip 用它列出完整名單，摘要上只剩數量時才說得出是哪幾個。</summary>
    public static string Detail(IReadOnlyList<string> names, string separator = "、")
    {
        if (names is null) throw new ArgumentNullException(nameof(names));
        return names.Count == 0 ? "" : string.Join(separator, names);
    }
}
