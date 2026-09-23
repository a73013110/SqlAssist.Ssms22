using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// SQL Memory 預覽上的命中：標在哪幾處，以及狀態列要說的那一句。
/// </summary>
/// <remarks>
/// 位置用清單同一個 <see cref="TextMatcher"/>（<see cref="SqlMemoryQuery.Matcher"/>）算：History 的 SQL
/// 與 Favorites 的名稱、說明、SQL 都是它判定命中的，另寫一份比對的話，大小寫或整個字只要差一點，
/// 清單上的列在預覽裡就一處都標不出來。上限與 SQL Search 同一個，見 <see cref="MatchHighlights"/>。
/// </remarks>
public sealed class SqlMemoryPreviewMatches
{
    /// <summary>收藏只靠名稱或說明命中時的那一句。</summary>
    public const string OutsideSqlNotice = "SQL 內文沒有命中；這一筆是名稱或說明符合。";

    public static SqlMemoryPreviewMatches None { get; } = new(Array.Empty<MatchSpan>(), "");

    private SqlMemoryPreviewMatches(IReadOnlyList<MatchSpan> spans, string notice)
    {
        Spans = spans;
        Notice = notice;
    }

    /// <summary>要標出來的區段，由前到後、不重疊；沒有搜尋字或一處都沒有時是空的。</summary>
    public IReadOnlyList<MatchSpan> Spans { get; }

    /// <summary>狀態列上的那一句；沒有話要說時是空字串。</summary>
    public string Notice { get; }

    /// <param name="matcher">清單這一輪的比對器；null 代表沒有搜尋字，什麼都不標。</param>
    /// <param name="favorite">這一筆是收藏：收藏比對的是名稱、說明與 SQL 的聯集。</param>
    /// <remarks>
    /// 三種情形的下一步不同，所以不併成一句：太多是「還有沒標出來的」；收藏只靠名稱或說明命中時
    /// SQL 上一處都沒有，不說的話一份沒有任何標記的全文看起來像是高亮壞了；其餘不必說話。
    /// History 只比對 SQL，不會走到第二句。
    /// </remarks>
    public static SqlMemoryPreviewMatches Locate(TextMatcher? matcher, string sql, bool favorite)
    {
        if (sql is null) throw new ArgumentNullException(nameof(sql));
        if (matcher is null) return None;

        var spans = MatchHighlights.Locate(matcher, sql, out var truncated);
        var notice = truncated ? MatchHighlights.TruncatedNotice
            : spans.Count == 0 && favorite ? OutsideSqlNotice
            : "";
        return new SqlMemoryPreviewMatches(spans, notice);
    }
}
