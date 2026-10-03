using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>視窗框架的每一段。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class WindowPhrases
{
    // 框架的端點：寫完的樣子與開頭的字。ROWS 收數值，RANGE 只收 UNBOUNDED 與 CURRENT ROW（剖析器擋下 RANGE 2 PRECEDING）。
    private static readonly string[] RowBounds = ["UNBOUNDED PRECEDING", "{value} PRECEDING", "{value} FOLLOWING", "CURRENT ROW"];
    private static readonly string[] RowHeads = ["UNBOUNDED", "{value}", "CURRENT"];
    private static readonly string[] RangeBounds = ["UNBOUNDED PRECEDING", "CURRENT ROW"];
    private static readonly string[] RangeHeads = ["UNBOUNDED", "CURRENT"];

    /// <remarks>
    /// ORDER BY 的排序項之後是 ROWS、RANGE。端點寫在三個地方：ROWS 之後、BETWEEN 之後與 AND 之後，每一處宣告到端點的第一個字，
    /// 之後的字由探測列；BETWEEN 的起點寫成整段，AND 由整段證據補（見 PhraseExplorer.AddEvidence）。框架從 ROWS 寫起
    /// 才由位置釘住：中段（AND 之後）的前一格判不出位置，以前另寫的 Lead 片語（UNBOUNDED、PRECEDING AND）漏了
    /// CURRENT ROW AND 與數值的端點。CURRENT 之後剖析器收任何識別字（留到語意檢查才擋），ROW 只能手寫。
    /// </remarks>
    internal static readonly PhraseDeclaration[] Frames =
    [
        .. Frame(new("")),
        .. Bounds("ROWS", RowBounds, RowHeads),
        .. Bounds("RANGE", RangeBounds, RangeHeads),
    ];

    private static IEnumerable<PhraseDeclaration> Bounds(string unit, string[] bounds, string[] heads) =>
        new[] { unit, $"{unit} BETWEEN" }
            .Concat(bounds.Select(bound => $"{unit} BETWEEN {bound} AND"))
            .SelectMany(anchor => heads.Select(head => new PhraseDeclaration($"{anchor} {head}")
            {
                Values = head == "CURRENT" ? ["ROW"] : null,
                Closed = head == "CURRENT" ? true : null,
            }))
            .SelectMany(Frame);

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
