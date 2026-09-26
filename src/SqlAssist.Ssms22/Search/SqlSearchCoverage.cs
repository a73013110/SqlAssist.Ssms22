using System;
using System.Collections.Generic;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 一輪搜尋的完整度寫成給人看的話：搜了哪些、哪幾個沒搜到、為什麼。
/// </summary>
/// <remarks>
/// 頁尾、空白畫面與通知共用這一份：三處各寫一次的話，通知說「已完整搜尋」而頁尾說
/// 「搜尋不完整」，使用者不知道該信哪一個。
///
/// 句子由這裡組，不由 provider 組：provider 只交出結構（目標、結局、種類、讀不到幾個），
/// 每多一個來源不必在呈現這一層多一個 <c>if</c>，也不會有哪一個來源的句子漏翻。
/// </remarks>
internal sealed class SqlSearchCoverage
{
    /// <summary>缺口最多列幾項；更多時只說還有幾項。頁尾只有一兩行。</summary>
    private const int MaxGaps = 3;

    /// <summary>還沒有答案、或整輪失敗時的那一份：什麼都不說。</summary>
    public static SqlSearchCoverage None { get; } = new(isComplete: false, "", "", anySearched: false, allDenied: false);

    private SqlSearchCoverage(bool isComplete, string summary, string gaps, bool anySearched, bool allDenied)
    {
        IsComplete = isComplete;
        Summary = summary;
        Gaps = gaps;
        AnySearched = anySearched;
        AllDenied = allDenied;
    }

    /// <summary>每一個目標都比完而且沒有漏。</summary>
    public bool IsComplete { get; }

    /// <summary>頁尾那一句：完整時說搜了哪些，不完整時說缺了哪些。沒有目標時是空字串。</summary>
    public string Summary { get; }

    /// <summary>只有缺口那一段；空白畫面的說明用。</summary>
    public string Gaps { get; }

    /// <summary>至少有一個目標比完了；一個都沒有時，空白畫面說「讀不到」而不是「沒有相符項目」。</summary>
    public bool AnySearched { get; }

    /// <summary>一個都沒搜到，而且每一個都說得出就是權限；抬頭換成「權限不足」的唯一門檻。</summary>
    public bool AllDenied { get; }

    public static SqlSearchCoverage Of(IReadOnlyList<SearchTargetStatus> targets)
    {
        if (targets is null) throw new ArgumentNullException(nameof(targets));
        if (targets.Count == 0) return new SqlSearchCoverage(isComplete: true, "", "", anySearched: true, allDenied: false);

        var gaps = new List<string>();
        var databases = 0;
        var sources = new List<string>();
        var anySearched = false;
        var allDenied = true;

        foreach (var target in targets)
        {
            if (target.Kind == SearchTargetKind.Database) databases++;
            else sources.Add(target.Name);

            if (target.State == SearchTargetState.Complete) anySearched = true;
            if (target.State != SearchTargetState.Unavailable || target.UnavailableKind != SearchUnavailableKind.Denied) allDenied = false;

            gaps.AddRange(GapsOf(target));
        }

        if (gaps.Count == 0)
        {
            return new SqlSearchCoverage(isComplete: true, SqlSearchText.CoverageComplete(Scope(databases, sources)), "",
                anySearched, allDenied: false);
        }

        var shown = string.Join(SqlSearchText.GapSeparator, gaps.GetRange(0, Math.Min(MaxGaps, gaps.Count)));
        var text = gaps.Count > MaxGaps ? SqlSearchText.MoreGaps(shown, gaps.Count - MaxGaps) : shown;

        return new SqlSearchCoverage(isComplete: false, SqlSearchText.CoverageIncomplete(text), text,
            anySearched, allDenied && !anySearched);
    }

    /// <summary>「40 個資料庫、SQL Agent 作業」；只勾了作業時不寫「0 個資料庫」。</summary>
    private static string Scope(int databases, List<string> sources)
    {
        var parts = new List<string>(sources.Count + 1);
        if (databases > 0) parts.Add(SqlSearchText.CoverageDatabases(databases));
        parts.AddRange(sources);
        return string.Join(CommonText.ListSeparator, parts);
    }

    /// <summary>一個目標缺了什麼；一個目標可以同時缺兩樣（讀不到的本文與沒比完的本文）。</summary>
    private static IEnumerable<string> GapsOf(SearchTargetStatus target)
    {
        switch (target.State)
        {
            case SearchTargetState.Unavailable:
                yield return target.UnavailableKind == SearchUnavailableKind.Denied ? SqlSearchText.GapDenied(target.Name)
                    : target.Detail is { } detail ? SqlSearchText.GapDetail(target.Name, detail)
                    : SqlSearchText.GapUnavailable(target.Name);
                yield break;
            case SearchTargetState.Running:
            case SearchTargetState.Canceled:
                yield return SqlSearchText.GapCanceled(target.Name);
                yield break;
        }

        if (target.UnreadableText > 0) yield return SqlSearchText.GapUnreadable(target.Name, target.UnreadableText);
        if (target.IsTextIncomplete) yield return SqlSearchText.GapTextIncomplete(target.Name);
    }
}
