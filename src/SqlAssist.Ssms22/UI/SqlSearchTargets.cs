using System;
using System.Collections.Generic;
using System.ComponentModel;
using SqlAssist.Core.Localization;
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

    [Localizable(false)]
    public static string LabelFor(SearchMatchTarget target) => target switch
    {
        SearchMatchTarget.Name => CommonText.Name,
        SearchMatchTarget.Text => SearchControlText.TargetText,
        SearchMatchTarget.Column => CommonText.Column,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "沒有這個比對位置的顯示字。")
    };

    [Localizable(false)]
    public static string DescriptionFor(SearchMatchTarget target) => target switch
    {
        SearchMatchTarget.Name => SearchControlText.TargetNameDescription,
        SearchMatchTarget.Text => SearchControlText.TargetTextDescription,
        SearchMatchTarget.Column => SearchControlText.TargetColumnDescription,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "沒有這個比對位置的說明。")
    };
}
