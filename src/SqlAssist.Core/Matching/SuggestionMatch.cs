using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 一筆通過篩選的建議項，附帶排名分數與供高亮使用的命中區段。
/// </summary>
public sealed class SuggestionMatch
{
    public SuggestionMatch(SqlSuggestion suggestion, int score, IReadOnlyList<MatchSpan> spans)
    {
        Suggestion = suggestion ?? throw new ArgumentNullException(nameof(suggestion));
        Score = score;
        Spans = spans ?? Array.Empty<MatchSpan>();
    }

    public SqlSuggestion Suggestion { get; }

    /// <summary>排名分數，越大越前面。跨查詢之間不具可比性。</summary>
    public int Score { get; }

    /// <summary><see cref="SqlSuggestion.DisplayText"/> 中被命中的字元區段。</summary>
    public IReadOnlyList<MatchSpan> Spans { get; }
}
