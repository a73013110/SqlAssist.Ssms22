using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 一份文字上要標出來的命中：併段、上限，以及少標時要說的那一句。
/// </summary>
/// <remarks>
/// 每一個顯示命中的表面都經過這裡，各功能只決定「拿什麼去找」——SQL Search 是把每一筆命中的片段
/// 投影回定義，SQL Memory 是清單那一輪的 <see cref="TextMatcher"/>。併段與截斷各寫一份的症狀是
/// 同一段文字在兩個工具窗標出來的處數不一樣，而上限只改到其中一邊。
///
/// 上限落在<b>標記</b>上不落在比對上：重疊或緊貼的出現併成一段，算一處（在 <c>aaa</c> 裡找
/// <c>aa</c>），所以不交給 <see cref="TextMatcher.FindAll"/> 的 <c>limit</c>——那一個數的是出現次數，
/// 併完之後可能還不到上限卻已經停了。
/// </remarks>
public static class MatchHighlights
{
    /// <summary>
    /// 一份文字上最多標幾處。
    /// </summary>
    /// <remarks>
    /// 沒有上限的症狀不是慢，是整份文件被切成上萬段：一張寬表上有五十個資料行都叫得出使用者打的
    /// 那幾個字，而每一個又在擴充屬性裡再出現一次。
    /// </remarks>
    public const int Maximum = 500;

    /// <summary>超過 <see cref="Maximum"/> 而少標了幾處時，狀態列上的那一句。</summary>
    /// <remarks>少標了卻不說的症狀是使用者按到最後一處就以為看完了。</remarks>
    public static string TruncatedNotice { get; } = "命中太多，只標出前 " + Maximum + " 處。";

    /// <summary><paramref name="matcher"/> 在 <paramref name="text"/> 上的每一處；沒有比對器（沒有搜尋字）時是空的。</summary>
    /// <remarks>邊找邊併，湊滿上限就不再往下掃：單一字元的搜尋字在一份大 SQL 上可能出現幾十萬次。</remarks>
    public static MatchHighlightSet Locate(TextMatcher? matcher, string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (matcher is null) return MatchHighlightSet.Empty;

        var merger = new Merger();
        var length = matcher.Pattern.Length;
        for (var at = matcher.IndexOf(text); at >= 0; at = matcher.IndexOf(text, at + 1))
        {
            if (!merger.Add(new MatchSpan(at, length))) break;
        }

        return merger.Build();
    }

    /// <summary>任意順序、可能重疊的區段排序後併成不重疊的標記，並套上上限。</summary>
    public static MatchHighlightSet Merge(IEnumerable<MatchSpan> spans)
    {
        if (spans is null) throw new ArgumentNullException(nameof(spans));

        var ordered = new List<MatchSpan>(spans);
        ordered.Sort(static (left, right) =>
            left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Length.CompareTo(right.Length));

        var merger = new Merger();
        foreach (var span in ordered)
        {
            if (!merger.Add(span)) break;
        }

        return merger.Build();
    }

    /// <summary>
    /// 由前到後吃進區段：與上一段重疊或緊貼就併進去，否則另起一段；滿了就記下「少標了」。
    /// </summary>
    /// <remarks>
    /// 文件那一層要不重疊的區段——重疊的話同一段文字會被切兩次，第二次的起點落在前一段裡面，
    /// 畫出來是一段錯位的底色。緊貼的兩段底色看起來本來就是一段，算兩處只會讓「3 / 7」對不上眼睛。
    /// </remarks>
    private sealed class Merger
    {
        private List<MatchSpan>? _spans;
        private bool _truncated;

        /// <returns>false 表示已經滿了，呼叫端不必再往下找。</returns>
        public bool Add(MatchSpan span)
        {
            if (_spans is { Count: > 0 } spans && span.Start <= spans[spans.Count - 1].End)
            {
                var last = spans[spans.Count - 1];
                if (span.End > last.End) spans[spans.Count - 1] = new MatchSpan(last.Start, span.End - last.Start);
                return true;
            }

            if (_spans?.Count == Maximum)
            {
                _truncated = true;
                return false;
            }

            (_spans ??= new List<MatchSpan>()).Add(span);
            return true;
        }

        public MatchHighlightSet Build() =>
            _spans is null ? MatchHighlightSet.Empty : new MatchHighlightSet(_spans, _truncated);
    }
}

/// <summary>標好的區段，外加「有沒有少標」。</summary>
public sealed class MatchHighlightSet
{
    public static MatchHighlightSet Empty { get; } = new(Array.Empty<MatchSpan>(), false);

    internal MatchHighlightSet(IReadOnlyList<MatchSpan> spans, bool truncated)
    {
        Spans = spans;
        IsTruncated = truncated;
    }

    /// <summary>由前到後、不重疊。</summary>
    public IReadOnlyList<MatchSpan> Spans { get; }

    public int Count => Spans.Count;

    /// <summary>超過 <see cref="MatchHighlights.Maximum"/> 而少標了幾處。</summary>
    public bool IsTruncated { get; }

    /// <summary>
    /// 狀態列要說的那一句；沒有話要說時是空字串。
    /// </summary>
    /// <param name="unmatched">
    /// 一處都沒標時的說明，由功能自己給（SQL Search 是位置對不上定義，SQL Memory 是收藏只靠名稱或說明命中）；
    /// null 代表一處都沒有也不必說。
    /// </param>
    /// <remarks>
    /// 優先順序只有這一份：少標了永遠先說，因為那是使用者唯一會被誤導的情形。
    /// </remarks>
    public string Notice(string? unmatched) =>
        IsTruncated ? MatchHighlights.TruncatedNotice
        : Count == 0 && unmatched is not null ? unmatched
        : "";
}
