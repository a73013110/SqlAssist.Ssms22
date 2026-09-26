namespace SqlAssist.Core.Keywords;

/// <summary>位置旗標的比對規則。</summary>
public static class SqlKeywordPositionExtensions
{
    /// <summary>
    /// 帶著 <paramref name="positions"/> 的建議項，能不能出現在分析器回報的
    /// <paramref name="caret"/> 位置。
    /// </summary>
    /// <remarks>
    /// 一般情形是位元交集。<see cref="SqlKeywordPosition.None"/> 是唯一的例外：
    /// 產生器判不出位置的字只在分析器也判不出位置（<see cref="SqlKeywordPosition.Any"/>）
    /// 時出現。
    ///
    /// 以前 <c>None</c> 在讀進來時就換成 <c>Any</c>，於是 <c>FILLFACTOR</c>、
    /// <c>STOPLIST</c> 這些深層子句字在每一個判得出的位置都出現——<c>SELECT F</c>
    /// 的清單裡有 <c>FILLFACTOR</c>。反過來整個藏起來也不行：分析器判不出位置的地方
    /// 正是這些字真正的用處（<c>WITH (</c>、<c>= ANY</c>）。字的用法若落在判得出的位置，
    /// 該補的是產生器的樣板，不是放寬這一條。
    ///
    /// 關鍵字、內建函式與片段共用這一條；片段沒寫 <c>positions</c> 的意思是「哪裡都能用」，
    /// 讀進來就是 <see cref="SqlKeywordPosition.Any"/>，不會落到這個例外。
    /// </remarks>
    public static bool Allows(this SqlKeywordPosition positions, SqlKeywordPosition caret)
    {
        return positions == SqlKeywordPosition.None
            ? caret == SqlKeywordPosition.Any
            : (positions & caret) != SqlKeywordPosition.None;
    }

    /// <summary>
    /// 文法在這裡寫不出既有物件的名稱；每一項都要說得出「那裡沒有任何名稱是合法的」。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>子句尾端（<c>GROUP BY a |</c>、<c>WHERE a = 1 |</c>、<c>FROM t a |</c>）：
    /// 一項剛寫完，同一行只接得了運算子或關鍵字。唯一會接名字的是別名，那是使用者
    /// 新取的名字，不是既有物件。換行後補上的語句開頭也不例外，見下一項。</item>
    /// <item>語句開頭、<c>BEGIN |</c> 與區塊的 <c>END |</c>：接下一句的關鍵字。省略 EXEC 的程序呼叫只在
    /// 批次第一句合法，由 <c>SqlCompletionContext.StartsBatch</c> 另外放行。</item>
    /// <item><c>ORDER |</c>／<c>GROUP |</c> 之後只有 <c>BY</c>；<c>CREATE |</c>／<c>ALTER |</c>／
    /// <c>DROP |</c> 之後是物件<b>種類</b>。</item>
    /// <item><c>ALTER TABLE t |</c>、<c>ALTER TABLE t ADD |</c>、<c>CREATE TABLE t (|</c>：
    /// 動作、條件約束關鍵字，或新資料行名稱。</item>
    /// <item><c>SET NOCOUNT |</c> 之後是選項值；要資料表的 <c>SET IDENTITY_INSERT |</c>
    /// 不是這個位置。</item>
    /// <item><c>DECLARE c CURSOR LOCAL |</c> 之後是選項或 <c>FOR</c>。</item>
    /// </list>
    ///
    /// 不在裡面的都有理由：<c>INSERT |</c> 的 <c>INTO</c> 可以省略；<c>SET |</c> 與
    /// <c>UPDATE t SET |</c> 是同一個位置，後者要資料行；CASE 的各段寫的是運算式，
    /// <c>CaseArm</c> 同時是 <c>WHEN |</c> 的起點。
    /// </remarks>
    private const SqlKeywordPosition NoNamePositions =
        SqlKeywordPosition.SelectListTail |
        SqlKeywordPosition.TableSourceTail |
        SqlKeywordPosition.ExpressionTail |
        SqlKeywordPosition.OrderByTail |
        SqlKeywordPosition.GroupByTail |
        SqlKeywordPosition.StatementStart |
        SqlKeywordPosition.BlockStart |
        SqlKeywordPosition.BlockEnd |
        SqlKeywordPosition.CursorOption |
        SqlKeywordPosition.ByAnchor |
        SqlKeywordPosition.DdlObject |
        SqlKeywordPosition.AlterTableAction |
        SqlKeywordPosition.AlterTableAdd |
        SqlKeywordPosition.ColumnDefinition |
        SqlKeywordPosition.SetOptionValue;

    /// <summary>
    /// 沒有位置旗標的建議項（資料表、程序、欄位、CTE…）能不能出現在 <paramref name="caret"/>。
    /// </summary>
    /// <remarks>
    /// 名稱是執行期從中繼資料來的，帶不了旗標，所以反過來列寫不出名稱的位置。
    /// 比的是「位置裡還有沒有別的位元」而不是交集：判不出位置時的
    /// <see cref="SqlKeywordPosition.Any"/> 含著每一個旗標，用交集的話 fail-open 會變成
    /// fail-closed，每一個位置的資料庫物件都會消失。
    /// </remarks>
    public static bool AcceptsNames(this SqlKeywordPosition caret)
    {
        return (caret & ~NoNamePositions) != SqlKeywordPosition.None;
    }
}
