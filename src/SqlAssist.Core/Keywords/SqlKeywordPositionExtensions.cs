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
    /// <item>子句尾端（<c>GROUP BY a |</c>、<c>WHERE a = 1 |</c>、<c>FROM t a |</c>）與疊在上面的運算元之後：
    /// 一項剛寫完，同一行只接得了運算子或關鍵字。唯一會接名字的是別名，那是使用者
    /// 新取的名字，不是既有物件。換行後補上的語句開頭也不例外，見下一項。</item>
    /// <item>語句開頭、<c>BEGIN |</c>、區塊的 <c>END |</c> 與 IF 主體寫完：接下一句的關鍵字或 ELSE。省略 EXEC 的程序呼叫只在
    /// 批次第一句合法，由 <c>SqlCompletionContext.StartsBatch</c> 另外放行。</item>
    /// <item><c>ORDER |</c>／<c>GROUP |</c> 之後只有 <c>BY</c>；<c>CREATE |</c>／<c>ALTER |</c>／
    /// <c>DROP |</c> 之後是物件<b>種類</b>。</item>
    /// <item><c>ALTER TABLE t |</c>、<c>ALTER TABLE t ADD |</c>、<c>CREATE TABLE t (|</c>：
    /// 動作、條件約束關鍵字，或新資料行名稱。</item>
    /// <item><c>SET NOCOUNT |</c> 之後是選項值；要資料表的 <c>SET IDENTITY_INSERT |</c>
    /// 不是這個位置。</item>
    /// <item><c>DECLARE c CURSOR LOCAL |</c> 之後是選項或 <c>FOR</c>，<c>CREATE SEQUENCE s START WITH 1 |</c> 之後是下一個選項；觸發程序的 <c>ON t |</c> 之後是
    /// <c>FOR</c>、<c>AFTER</c> 這些字，事件清單裡是 <c>INSERT</c> 這些事件與 <c>AS</c>；BACKUP／RESTORE 的 <c>WITH |</c> 之後是選項；MERGE 的
    /// <c>WHEN |</c> 之後是 <c>MATCHED</c>、<c>NOT</c>，<c>THEN |</c> 之後是動作；<c>GRANT SELECT |</c> 之後是
    /// <c>ON</c>、<c>TO</c>；索引鍵清單的 <c>(a |</c> 之後是 <c>ASC</c>、<c>DESC</c>；外部索引鍵的
    /// <c>REFERENCES u (a) |</c> 之後是 <c>ON</c>、<c>NOT</c>；視窗的 <c>ORDER BY a |</c> 之後是框架，<c>OVER (|</c> 之後是
    /// <c>PARTITION</c>、<c>ORDER</c> 或指令碼取的視窗名稱，<c>WINDOW w |</c> 之後是 <c>AS</c>，
    /// <c>WINDOW w AS (…) |</c> 之後是逗號或查詢之後的子句；
    /// <c>OFFSET 10 |</c> 之後是 <c>ROWS</c>；函式呼叫的 <c>SUM(a) |</c> 之後是 <c>OVER</c>，它總是加在會接別名的子句尾端上；模組的 <c>WITH SCHEMABINDING |</c> 之後是 <c>AS</c>，
    /// 函式參數清單的 <c>f (@a int) |</c> 之後是 <c>RETURNS</c>；<c>EXEC p WITH |</c>、CREATE INDEX 的 <c>WITH (|</c>
    /// 與清單片語的標頭（<c>RAISERROR (…) WITH |</c>、<c>FOR XML |</c>、<c>ALTER USER u WITH |</c>）之後是選項；
    /// <c>WITH RESULT SETS (|</c> 之後是 <c>AS</c> 或一組資料行定義，那一組的 <c>(|</c> 之後是新資料行名稱，
    /// 型別寫完之後是 <c>COLLATE</c>、<c>NULL</c>、<c>NOT NULL</c>；資料行定義的型別與每個選項寫完之後是下一個選項；
    /// <c>TABLESAMPLE (10 |</c> 之後是 <c>PERCENT</c>；PIVOT 的 <c>(SUM(x) |</c> 之後是 <c>FOR</c>，<c>FOR y |</c> 之後是 <c>IN</c>。</item>
    /// </list>
    ///
    /// 不在裡面的都有理由：<c>INSERT |</c> 的 <c>INTO</c> 可以省略；<c>SET |</c> 與
    /// <c>UPDATE t SET |</c> 是同一個位置，後者要資料行；CASE 的各段寫的是運算式，
    /// <c>CaseArm</c> 同時是 <c>WHEN |</c> 的起點；<c>GRANT SELECT ON |</c> 可以直接寫物件名稱。
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
        SqlKeywordPosition.IfBodyEnd |
        SqlKeywordPosition.CursorOption |
        SqlKeywordPosition.SequenceOption |
        SqlKeywordPosition.TriggerHeader |
        SqlKeywordPosition.MergeWhen |
        SqlKeywordPosition.ProcedureOption |
        SqlKeywordPosition.FunctionOption |
        SqlKeywordPosition.ViewOption |
        SqlKeywordPosition.TriggerOption |
        SqlKeywordPosition.TriggerEvent |
        SqlKeywordPosition.TriggerEventEnd |
        SqlKeywordPosition.MergeAction |
        SqlKeywordPosition.MergeClause |
        SqlKeywordPosition.PermissionList |
        SqlKeywordPosition.PermissionTarget |
        SqlKeywordPosition.SelectIntoTail |
        SqlKeywordPosition.FetchTail |
        SqlKeywordPosition.UpdateSetTail |
        SqlKeywordPosition.IndexKeyTail |
        SqlKeywordPosition.ReferencesTail |
        SqlKeywordPosition.FunctionCallTail |
        SqlKeywordPosition.OperandTail |
        SqlKeywordPosition.WindowOrderTail |
        SqlKeywordPosition.WindowSpecification |
        SqlKeywordPosition.WindowName |
        SqlKeywordPosition.WindowClauseTail |
        SqlKeywordPosition.OffsetTail |
        SqlKeywordPosition.ModuleHeader |
        SqlKeywordPosition.FunctionReturns |
        SqlKeywordPosition.ResultSetList |
        SqlKeywordPosition.ResultSetColumn |
        SqlKeywordPosition.ResultSetColumnTail |
        SqlKeywordPosition.OptionItem |
        SqlKeywordPosition.IndexOption |
        SqlKeywordPosition.TableSampleTail |
        SqlKeywordPosition.PivotClause |
        SqlKeywordPosition.ByAnchor |
        SqlKeywordPosition.DdlObject |
        SqlKeywordPosition.AlterTableAction |
        SqlKeywordPosition.AlterTableAdd |
        SqlKeywordPosition.ColumnDefinition |
        SqlKeywordPosition.ColumnDefinitionTail |
        SqlKeywordPosition.SetOptionValue;

    /// <summary>
    /// 下一個詞元只能是這個位置的字；每一項都要說得出「那裡寫不出標點、常值、新名字或下一句」。
    /// </summary>
    /// <remarks>
    /// 是 <see cref="NoNamePositions"/> 的子集：寫得出名稱的位置由目標或資料行的所屬資料表收斂，
    /// 不在這裡。
    /// <list type="bullet">
    /// <item><c>ORDER |</c>／<c>GROUP |</c> 之後只有 <c>BY</c>；<c>CREATE |</c>／<c>ALTER |</c>／<c>DROP |</c>
    /// 之後是物件種類；<c>ALTER TABLE t |</c> 之後是動作。</item>
    /// <item>MERGE 的 <c>WHEN |</c>、<c>THEN |</c>；PIVOT 的 <c>(SUM(x) |</c> 與 <c>FOR y |</c>；
    /// <c>OFFSET 10 |</c> 之後的 <c>ROWS</c>；函式參數清單之後的 <c>RETURNS</c>；WINDOW 子句一項的名稱之後的 <c>AS</c>。</item>
    /// <item>觸發程序標頭與事件、游標選項，以及各種 <c>WITH</c> 選項清單的起點與逗號之後。</item>
    /// </list>
    ///
    /// 不在裡面的都有理由：子句尾端（<c>WHERE a |</c>、<c>ORDER BY a |</c>、<c>REFERENCES u (a) |</c>）
    /// 接得了逗號、運算子或下一句；<c>BEGIN |</c> 之後可以直接寫一句；<c>GRANT SELECT |</c>
    /// 接得了逗號；<c>SET NOCOUNT |</c> 的值可以是數字或識別字，由子句片語逐一判斷；
    /// 資料行定義與 <c>ALTER TABLE t ADD |</c> 多半是新名字。
    /// </remarks>
    private const SqlKeywordPosition ClosedPositions =
        SqlKeywordPosition.ByAnchor |
        SqlKeywordPosition.DdlObject |
        SqlKeywordPosition.AlterTableAction |
        SqlKeywordPosition.MergeWhen |
        SqlKeywordPosition.MergeAction |
        SqlKeywordPosition.PivotClause |
        SqlKeywordPosition.OffsetTail |
        SqlKeywordPosition.FunctionReturns |
        SqlKeywordPosition.WindowName |
        SqlKeywordPosition.TriggerHeader |
        SqlKeywordPosition.TriggerEvent |
        SqlKeywordPosition.CursorOption |
        SqlKeywordPosition.SequenceOption |
        SqlKeywordPosition.IndexOption |
        SqlKeywordPosition.ProcedureOption |
        SqlKeywordPosition.FunctionOption |
        SqlKeywordPosition.ViewOption |
        SqlKeywordPosition.TriggerOption |
        SqlKeywordPosition.OptionItem;

    /// <summary>
    /// 分析器回報的 <paramref name="caret"/> 裡每一個位置都封閉，見 <see cref="ClosedPositions"/>。
    /// </summary>
    /// <remarks>
    /// 聯集裡只要有一個位置不封閉就不算：<c>WHERE a NOT |</c> 同時是述詞起點，那裡寫得出運算式。
    /// 判不出位置的 <see cref="SqlKeywordPosition.Any"/> 含著每一個旗標，因此一定不算。
    /// </remarks>
    public static bool IsClosed(this SqlKeywordPosition caret)
    {
        return caret != SqlKeywordPosition.None && (caret & ~ClosedPositions) == SqlKeywordPosition.None;
    }

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
