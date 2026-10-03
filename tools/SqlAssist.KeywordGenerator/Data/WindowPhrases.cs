namespace SqlAssist.KeywordGenerator.Data;

/// <summary>視窗框架的每一段。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class WindowPhrases
{
    internal static readonly PhraseDeclaration[] Frames =
    [
        // 視窗框架：ORDER BY 的排序項之後是 ROWS、RANGE，框架的每一段由片語往下補。基底視窗名稱之後也寫得出框架
        // （OVER (w ROWS …)），見 Frame。
        // 框架中段（AND 之後）的 UNBOUNDED、CURRENT 前一格判不出位置，仍由 Lead 片語給。
        // CURRENT 之後剖析器收任何識別字（留到語意檢查才擋），ROW 只能手寫。
        .. Frame(new("")),
        .. Frame(new("ROWS BETWEEN UNBOUNDED PRECEDING")),
        .. Frame(new("RANGE BETWEEN UNBOUNDED PRECEDING")),
        .. Frame(new("ROWS UNBOUNDED")),
        .. Frame(new("RANGE UNBOUNDED")),
        new("UNBOUNDED") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND " },
        new("PRECEDING AND") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED " },
        .. Frame(new("ROWS CURRENT") { Values = ["ROW"], Closed = true }),
        .. Frame(new("RANGE CURRENT") { Values = ["ROW"], Closed = true }),
        new("BETWEEN CURRENT") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS ", Values = ["ROW"], Closed = true },
        new("AND CURRENT") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING ", Values = ["ROW"], Closed = true },
    ];

    /// <summary>排序項之後與視窗規格裡各一條。</summary>
    /// <remarks>
    /// 視窗規格的括號一開頭剖析器還不收框架（<c>OVER (ROWS</c>），要先有 ORDER BY 或基底視窗名稱，
    /// 所以那一格以第二個樣板（<c>OVER (w </c>）探測。
    /// </remarks>
    private static PhraseDeclaration[] Frame(PhraseDeclaration phrase) =>
    [
        phrase with { After = ["WindowOrderTail"] },
        phrase with { After = ["WindowSpecification"], Template = 1 },
    ];
}
