using System;
using System.Collections.Generic;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 比對位置在畫面上叫什麼；分段開關與結果列的徽章共用這一份。
/// </summary>
/// <remarks>
/// 兩邊各寫一次的症狀是開關上寫「內容」、列上寫「定義本文」，而使用者會以為它們是兩件事。
/// 用「內容」而不是「定義本文」：之後的 SQL Memory provider 比對的是儲存的 SQL 全文，
/// 片段 provider 比對的是片段本體，三者都不是「定義」。
/// </remarks>
internal static class SqlSearchTargets
{
    /// <summary>分段開關上的先後；與結果列徽章共用同一組字。</summary>
    public static IReadOnlyList<SearchMatchTarget> Order { get; } = new[]
    {
        SearchMatchTarget.Name,
        SearchMatchTarget.Text,
        SearchMatchTarget.Column
    };

    public static string LabelFor(SearchMatchTarget target) => target switch
    {
        SearchMatchTarget.Name => "名稱",
        SearchMatchTarget.Text => "內容",
        SearchMatchTarget.Column => "欄位",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "沒有這個比對位置的顯示字。")
    };

    public static string DescriptionFor(SearchMatchTarget target) => target switch
    {
        SearchMatchTarget.Name => "比對物件名稱；模糊比對，不分大小寫。",
        SearchMatchTarget.Text => "比對定義本文；關掉就真的不去撈定義，第一次搜尋最貴的一段因此省下來。",
        SearchMatchTarget.Column => "比對資料行名稱；命中仍指向擁有它的那一個物件。",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "沒有這個比對位置的說明。")
    };
}
