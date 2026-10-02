namespace SqlAssist.KeywordGenerator.Data;

/// <summary>視窗框架：ROWS、RANGE 與框架的每一段。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class WindowPhrases
{
    internal static readonly PhraseDeclaration[] Frames =
    [
        // 視窗框架：ORDER BY 的排序項之後是 ROWS、RANGE，框架的每一段由片語往下補。
        // 框架中段（AND 之後）的 UNBOUNDED、CURRENT 前一格判不出位置，仍由 Lead 片語給。
        // CURRENT 之後剖析器收任何識別字（留到語意檢查才擋），ROW 只能手寫。
        new("") { After = ["WindowOrderTail"] },
        new("ROWS BETWEEN UNBOUNDED PRECEDING") { After = ["WindowOrderTail"] },
        new("RANGE BETWEEN UNBOUNDED PRECEDING") { After = ["WindowOrderTail"] },
        new("ROWS UNBOUNDED") { After = ["WindowOrderTail"] },
        new("RANGE UNBOUNDED") { After = ["WindowOrderTail"] },
        new("UNBOUNDED") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND " },
        new("PRECEDING AND") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED " },
        new("ROWS CURRENT") { After = ["WindowOrderTail"], Values = ["ROW"], Closed = true },
        new("RANGE CURRENT") { After = ["WindowOrderTail"], Values = ["ROW"], Closed = true },
        new("BETWEEN CURRENT") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS ", Values = ["ROW"], Closed = true },
        new("AND CURRENT") { Lead = "SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING ", Values = ["ROW"], Closed = true },
    ];
}
