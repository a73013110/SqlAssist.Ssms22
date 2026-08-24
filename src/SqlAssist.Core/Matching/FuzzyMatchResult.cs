using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 單次模糊比對的結果。分數越高代表越貼近使用者輸入。
/// </summary>
public sealed class FuzzyMatchResult
{
    private static readonly MatchSpan[] EmptySpans = Array.Empty<MatchSpan>();

    /// <summary>沒有命中時共用的結果實例，避免在熱路徑配置物件。</summary>
    public static readonly FuzzyMatchResult NoMatch = new(false, 0, EmptySpans);

    private FuzzyMatchResult(bool isMatch, int score, IReadOnlyList<MatchSpan> spans)
    {
        IsMatch = isMatch;
        Score = score;
        Spans = spans;
    }

    public bool IsMatch { get; }

    public int Score { get; }

    /// <summary>命中的字元區段，依 <see cref="MatchSpan.Start"/> 由小到大排序且互不重疊。</summary>
    public IReadOnlyList<MatchSpan> Spans { get; }

    public static FuzzyMatchResult Matched(int score, IReadOnlyList<MatchSpan> spans)
    {
        return new FuzzyMatchResult(true, score, spans ?? EmptySpans);
    }
}
