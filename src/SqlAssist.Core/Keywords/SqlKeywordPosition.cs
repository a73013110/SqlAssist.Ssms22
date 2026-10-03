using System;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 關鍵字可以出現的位置。
/// </summary>
/// <remarks>
/// 每個成員對應 <c>tools/SqlAssist.KeywordGenerator/Data/PositionTemplates.cs</c> 裡的一個樣板；成員名稱與樣板名稱
/// 必須一致，產生器直接用名稱組出旗標。
///
/// 存在的理由是雜訊：關鍵字目錄有 180 個字，全部無條件列出來的話，打第一個字元時
/// 清單會被文法上根本不可能出現的字塞滿。分層之後 <c>WHERE</c> 之後不會冒出
/// <c>PROCEDURE</c>，語句開頭也不會冒出 <c>ASC</c>。
///
/// 位置的切法刻意對齊「游標前一個詞元」——分析器認得的就是那個。
/// 每個位置都必須是分析器回得出來的：產生器的樣板交給分析器必須回報含該位置的值，
/// 這一條由測試逐條回驗。只有產生器分得出、分析器判不出的位置放進去只是自欺。
/// 分析器判不出來時回 <see cref="Any"/>，所有字都放行：寧可多列幾個字，
/// 也不要因為分析器看不懂上下文就把使用者要的關鍵字藏起來。
/// 反過來，產生器判不出來的字（<see cref="None"/>）只在那個時候出現，
/// 規則見 <see cref="SqlKeywordPositionExtensions.Allows(SqlKeywordPosition, SqlKeywordPosition)"/>。
/// </remarks>
[Flags]
public enum SqlKeywordPosition : long
{
    /// <summary>
    /// 產生器判定它進不了任何樣板；只在分析器也判不出位置（<see cref="Any"/>）時出現。
    /// </summary>
    /// <remarks>
    /// 只有這一個意思。「這一格是使用者自己取的名字」是另一個軸，見
    /// <see cref="SqlCaretPosition.Slot"/>。
    /// </remarks>
    None = 0,

    /// <summary>語句開頭。</summary>
    StatementStart = 1 << 0,

    /// <summary>SELECT 之後的選取清單起點。</summary>
    SelectList = 1 << 1,

    /// <summary>選取清單已經有一項之後——FROM、INTO、UNION、ORDER。</summary>
    SelectListTail = 1 << 2,

    /// <summary>FROM、JOIN、INTO、UPDATE 之後的資料來源位置。</summary>
    DataSource = 1 << 3,

    /// <summary>資料來源之後——WHERE、JOIN、GROUP、ORDER 這些子句的起點。</summary>
    TableSourceTail = 1 << 4,

    /// <summary>WHERE、ON、HAVING 之後的述詞起點。</summary>
    Predicate = 1 << 5,

    /// <summary>述詞完整之後——AND、OR，以及後續子句。</summary>
    ExpressionTail = 1 << 6,

    /// <summary>ORDER BY 的欄位之後——ASC、DESC、OFFSET。</summary>
    OrderByTail = 1 << 7,

    /// <summary>GROUP BY 的欄位之後——HAVING、ORDER、WITH（ROLLUP）。</summary>
    /// <remarks>
    /// 不併進 <see cref="OrderByTail"/>：GROUP BY a 之後不接 ASC、DESC，ORDER BY a 之後不接 HAVING。
    /// </remarks>
    GroupByTail = 1 << 23,

    /// <summary>
    /// ORDER BY 或 GROUP BY 要的那個欄位本身，含逗號之後的下一項。
    /// </summary>
    /// <remarks>
    /// 分析器一直知道這裡要的是欄位，卻只回得出 <see cref="Any"/>——列舉裡沒有
    /// 對應的成員，<see cref="OrderByTail"/> 是欄位<b>之後</b>的 ASC／DESC。
    /// 回 <see cref="Any"/> 的代價量得出來：同一組候選、同一個前綴 <c>C</c>，
    /// <c>SELECT C</c> 只有 62 筆而 <c>ORDER BY C</c> 有 118 筆，而且前 13 名
    /// 全被捷徑以 <c>C</c> 開頭的片段占滿，欄位掉到第 14 名之後。
    /// </remarks>
    OrderByColumn = 1 << 16,

    /// <summary>ORDER、GROUP 之後——BY。</summary>
    ByAnchor = 1 << 8,

    /// <summary>CREATE、ALTER、DROP 之後的物件類別。</summary>
    DdlObject = 1 << 9,

    /// <summary>CASE 的 WHEN 條件之後——IN、LIKE、BETWEEN、THEN、AND、OR。</summary>
    CaseArm = 1 << 10,

    /// <summary>CASE 的 THEN 結果之後——WHEN、ELSE、END。</summary>
    CaseBody = 1 << 11,

    /// <summary>
    /// 資料行定義清單的每一項開頭——CONSTRAINT、PRIMARY、UNIQUE、INDEX、CHECK、FOREIGN。
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE t (</c>、逗號之後，以及 <c>DECLARE @t TABLE (</c>、
    /// <c>RETURNS @t TABLE (</c>、<c>CREATE TYPE … AS TABLE (</c>。這一格也可能是新資料行
    /// 的名稱，所以分析器同時回報 <c>MaybeName</c>。
    ///
    /// 不借用 <see cref="AlterTableAdd"/>：<c>ALTER TABLE t ADD DEFAULT 0 FOR a</c> 合法，
    /// 資料表層級的 <c>DEFAULT</c> 在 CREATE TABLE 裡卻不合法，兩者的字不一樣。
    /// </remarks>
    ColumnDefinition = 1 << 20,

    /// <summary>
    /// 資料行定義寫完型別（或計算資料行的運算式）之後，以及其後每一個寫完的選項之後——
    /// NOT NULL、COLLATE、IDENTITY、條件約束，非關鍵字的 HIDDEN、SPARSE、MASKED、PERSISTED 由子句片語給。
    /// </summary>
    /// <remarks>
    /// 定義的開頭在哪裡由 <see cref="ColumnDefinition"/> 與 <see cref="AlterTableAdd"/> 說（含 <c>DECLARE @t TABLE (</c>、
    /// <c>OPENJSON(@j) WITH (</c>），<c>ALTER TABLE t ALTER COLUMN c</c> 也是一項；這幾種共用這一格，不各寫一份。
    /// 結果集的資料行只寫得出定序與 NULL，另見 <see cref="ResultSetColumnTail"/>。
    /// </remarks>
    ColumnDefinitionTail = 1L << 30,

    /// <summary>BEGIN 之後——TRANSACTION、TRY、CATCH。</summary>
    BlockStart = 1 << 13,

    /// <summary>BEGIN … END 的 END 之後——下一句，以及 ELSE、TRY、CATCH。</summary>
    /// <remarks>
    /// 分析器同時回報 <see cref="StatementStart"/>：區塊寫完就是一句的結尾。
    /// 不只回報語句開頭的理由是 <c>IF … BEGIN … END ELSE</c> 與 <c>END TRY</c>：
    /// 那三個字不能出現在一般的語句開頭。
    /// </remarks>
    BlockEnd = 1 << 24,

    /// <summary>IF 只有一句的主體寫完之後——下一句，以及 ELSE。</summary>
    /// <remarks>
    /// 分析器把它加在那一句自己的位置上：<c>IF @a = 1 SELECT 1 </c> 仍是選取清單尾端，
    /// 多接一個 ELSE。主體是 <c>BEGIN … END</c> 時 ELSE 由 <see cref="BlockEnd"/> 給。
    /// </remarks>
    IfBodyEnd = 1 << 26,

    /// <summary>DECLARE c CURSOR 與它的選項之後——FOR；選項本身由子句片語給。</summary>
    CursorOption = 1 << 25,

    /// <summary>CREATE|ALTER SEQUENCE s 與它的每一個選項之後——AS、NO；START WITH 這些選項由子句片語給。</summary>
    /// <remarks>選項不以逗號分隔、順序不限，與 <see cref="CursorOption"/> 同一種格子。</remarks>
    SequenceOption = 1 << 29,

    /// <summary>觸發程序標頭的目標與 WITH 選項之後——FOR、WITH；AFTER、INSTEAD 由子句片語給。</summary>
    /// <remarks>
    /// 不借用資料來源尾端：<c>ON t WITH ENCRYPTION FOR</c> 的 FOR 接 INSERT，不接 XML。
    /// </remarks>
    TriggerHeader = 1 << 27,

    /// <summary>MERGE 的 WHEN 之後——NOT；MATCHED 由子句片語給。</summary>
    /// <remarks>
    /// 不借用述詞起點：<c>CASE WHEN</c> 之後才是述詞，那裡寫得出欄位，這裡寫不出。
    /// </remarks>
    MergeWhen = 1 << 28,

    /// <summary>MERGE 的 THEN 之後——UPDATE、DELETE、INSERT。</summary>
    MergeAction = 1L << 36,

    /// <summary>MERGE 的 ON 條件或一個動作寫完之後——WHEN、OUTPUT、OPTION。</summary>
    /// <remarks>
    /// ON 條件寫完時分析器同時回報 <see cref="ExpressionTail"/>：條件還能接 AND、OR。
    /// 不借用資料來源尾端：MERGE 的 ON 之後不接 WHERE、JOIN。
    /// </remarks>
    MergeClause = 1L << 37,

    /// <summary>GRANT／DENY／REVOKE 的權限寫完之後——ON、TO、FROM。</summary>
    PermissionList = 1L << 38,

    /// <summary>GRANT／DENY／REVOKE 的 ON 目標之後——TO、FROM。</summary>
    /// <remarks>不借用資料來源尾端：那裡探得到的是查詢的子句字，探不到 REVOKE 的 FROM。</remarks>
    PermissionTarget = 1L << 39,

    /// <summary>GRANT／DENY／REVOKE 的 ON 之後——安全性實體的類別（SCHEMA::、OBJECT::）或目標名稱。</summary>
    /// <remarks>
    /// 不借用述詞起點：那裡列的是 EXISTS、CASE，類別多半不是關鍵字，由子句片語給；名稱照常。
    /// </remarks>
    PermissionOn = 1L << 56,

    /// <summary>
    /// GRANT／DENY／REVOKE 的 TO、FROM，ALTER AUTHORIZATION 的 TO 與稽核動作的 BY 之後（含 <c>TO a, </c>）——主體；
    /// 關鍵字只有 PUBLIC、SCHEMA OWNER 這幾個，主體的名稱由目錄物件給。
    /// </summary>
    PermissionGrantee = 1L << 53,

    /// <summary>SELECT … INTO 的新資料表之後——FROM、WHERE、UNION。</summary>
    SelectIntoTail = 1L << 40,

    /// <summary>FETCH … FROM 的游標之後——INTO。</summary>
    FetchTail = 1L << 41,

    /// <summary>CREATE INDEX 的 <c>WITH (</c> 與選項清單的逗號之後；選項由子句片語給。</summary>
    /// <remarks>
    /// 認的是這一句，不是緊鄰的形狀：INCLUDE、篩選索引的 WHERE 夾在中間也一樣。
    /// 不借用資料表提示：<c>WITH (</c> 前面是 CREATE INDEX 時接的是 ONLINE、FILLFACTOR。
    /// </remarks>
    IndexOption = 1L << 57,

    /// <summary>索引鍵清單裡一個資料行之後——ASC、DESC。</summary>
    /// <remarks>
    /// CREATE INDEX 的 <c>ON t (a </c>、條件約束的 <c>PRIMARY KEY (a </c>、<c>UNIQUE (a </c> 與內嵌的
    /// <c>INDEX ix (a </c>。不借用述詞：<c>ON t (</c> 的 ON 不是聯結條件。
    /// </remarks>
    IndexKeyTail = 1L << 42,

    /// <summary>UPDATE 的 SET 指派寫完之後——FROM、WHERE、OUTPUT、OPTION。</summary>
    /// <remarks>分析器同時回報 <see cref="ExpressionTail"/>：指派的值還能接運算子與 COLLATE。</remarks>
    UpdateSetTail = 1L << 43,

    /// <summary>CREATE／ALTER PROCEDURE 的 WITH 與選項清單的逗號之後；選項由子句片語給。</summary>
    /// <remarks>
    /// 模組的四種選項各自一格：<c>RECOMPILE</c> 只屬於程序，<c>VIEW_METADATA</c> 只屬於檢視。
    /// 不併進 <see cref="OptionItem"/>：模組選項寫完之後還有 <see cref="ModuleHeader"/> 接 AS，
    /// 清單片語的清單沒有這一格。
    /// </remarks>
    ProcedureOption = 1 << 12,

    /// <summary>CREATE／ALTER FUNCTION 的 WITH 與選項清單的逗號之後。</summary>
    FunctionOption = 1L << 31,

    /// <summary>CREATE／ALTER VIEW 的 WITH 與選項清單的逗號之後。</summary>
    ViewOption = 1L << 32,

    /// <summary>觸發程序目標之後的 WITH 與選項清單的逗號之後。</summary>
    TriggerOption = 1L << 33,

    /// <summary>觸發程序的 AFTER、FOR、INSTEAD OF 與事件清單的逗號之後——INSERT、UPDATE、DELETE。</summary>
    /// <remarks>
    /// 不借用語句開頭：<c>AFTER DELETE</c> 的 DELETE 不開始一句，當成開頭的話 <c>AS</c> 走不回
    /// CREATE，本體判不成語句開頭。
    /// </remarks>
    TriggerEvent = 1L << 34,

    /// <summary>觸發程序的事件寫完之後——AS、WITH（APPEND）、NOT（FOR REPLICATION）。</summary>
    TriggerEventEnd = 1L << 35,

    /// <summary>外部索引鍵 <c>REFERENCES t (a)</c> 與它的 ON DELETE／ON UPDATE 動作之後——ON、NOT、其他條件約束。</summary>
    /// <remarks>
    /// 不借用資料行定義：那裡的 ON 之後是 DELETE、UPDATE，查詢的 ON 之後才是述詞。
    /// </remarks>
    ReferencesTail = 1L << 44,

    /// <summary>函式呼叫的括號之後——OVER、COLLATE；分析器把它加在選取清單與 ORDER BY 的尾端上。</summary>
    /// <remarks>
    /// 不併進 <see cref="SelectListTail"/>：<c>SELECT a |</c> 不接 OVER，<c>SELECT SUM(a) |</c> 才接。
    /// </remarks>
    FunctionCallTail = 1L << 45,

    /// <summary>視窗 <c>OVER (… ORDER BY a</c> 的排序項之後——ASC、DESC、ROWS、RANGE。</summary>
    /// <remarks>
    /// 不併進 <see cref="OrderByTail"/>：查詢的 ORDER BY 接 OFFSET、UNION，視窗的接視窗框架。
    /// </remarks>
    WindowOrderTail = 1L << 46,

    /// <summary>
    /// 視窗規格的括號裡、排序之前——<c>OVER (</c>、<c>WINDOW w AS (</c> 與其後的基底視窗名稱、
    /// <c>PARTITION BY a</c> 寫完：PARTITION、ORDER、ROWS、RANGE。
    /// </summary>
    /// <remarks>
    /// 括號一開頭還寫得出具名視窗（<c>OVER (w ORDER BY …)</c>），那是指令碼取的名字，由視窗目標列；
    /// 資料庫物件在這裡寫不出來。
    /// </remarks>
    WindowSpecification = 1L << 50,

    /// <summary>WINDOW 子句一項的名稱之後（<c>WINDOW w </c>、<c>WINDOW w AS (…), w2 </c>）——AS。</summary>
    WindowName = 1L << 52,

    /// <summary>WINDOW 子句的一項寫完（<c>WINDOW w AS (…) </c>）——ORDER、OPTION、UNION 這些查詢之後的子句。</summary>
    /// <remarks>
    /// 下一項由逗號開頭。不借用 WINDOW 前面那個子句的尾端：<c>WHERE a = 1 WINDOW w AS (…) </c> 之後不接 AND。
    /// </remarks>
    WindowClauseTail = 1L << 51,

    /// <summary><c>ORDER BY … OFFSET 10</c> 的值之後——ROW、ROWS；兩個字都由子句片語給。</summary>
    OffsetTail = 1L << 47,

    /// <summary><c>TABLESAMPLE [SYSTEM] (10</c> 的樣本大小之後——PERCENT、ROWS。</summary>
    /// <remarks>不借用 <see cref="TopClauseTail"/>：TOP 之後接 WITH TIES 與選取清單，這裡不接。</remarks>
    TableSampleTail = 1L << 54,

    /// <summary>
    /// PIVOT、UNPIVOT 括號裡的一段寫完之後——彙總或值之後是 FOR，FOR 的資料行之後是 IN。
    /// </summary>
    /// <remarks>
    /// 不借用資料來源尾端：那裡的 FOR 接 XML、JSON，這裡的 FOR 接資料行。
    /// 兩處共用一個位置：各只有一個字，分開只多一個旗標。
    /// </remarks>
    PivotClause = 1L << 55,

    /// <summary>模組的 WITH 選項清單寫完之後——AS；程序另外接 FOR REPLICATION。</summary>
    /// <remarks>
    /// 清單裡是 <see cref="ProcedureOption"/>、<see cref="FunctionOption"/>、<see cref="ViewOption"/>；
    /// 寫完一個選項之後接的是下一個選項的逗號或本體，選項名稱不再出現。
    /// </remarks>
    ModuleHeader = 1L << 48,

    /// <summary><c>CREATE FUNCTION f (…)</c> 的參數清單之後——RETURNS；RETURNS 不是關鍵字，由子句片語給。</summary>
    FunctionReturns = 1L << 49,

    /// <summary><c>WITH RESULT SETS (</c> 與結果集之間的逗號之後——AS；OBJECT、TYPE、FOR XML 由子句片語給。</summary>
    /// <remarks>另一種寫法是一組資料行定義的左括號，那不是字。</remarks>
    ResultSetList = 1L << 58,

    /// <summary>結果集資料行定義的左括號與逗號之後：新資料行的名稱，分析器同時回報 <c>Name</c>。</summary>
    /// <remarks>
    /// 不借用 <see cref="ColumnDefinition"/>：這裡只有「名稱 型別 [COLLATE] [NULL | NOT NULL]」，
    /// 寫不出 CONSTRAINT、PRIMARY KEY，名稱之外沒有任何東西是對的。
    /// </remarks>
    ResultSetColumn = 1L << 59,

    /// <summary>結果集資料行的型別與定序寫完之後——COLLATE、NULL、NOT。</summary>
    ResultSetColumnTail = 1L << 60,

    /// <summary>
    /// 清單片語宣告的選項清單：標頭（<c>ALTER LOGIN l WITH</c>、<c>BACKUP DATABASE d TO … WITH</c>、<c>FOR XML</c>）
    /// 與逗號之後；選項由那個片語給。
    /// </summary>
    /// <remarks>
    /// 所有這種清單共用一個位元。位置只說「這裡是某一句的選項清單」，是哪一句、接哪些字由片語的尾巴
    /// 說：LOGIN、USER、BACKUP、RESTORE、EXEC、RAISERROR、DBCC 的選項各不相同，每種各佔一個位元的話，
    /// 敘述數一多位元就不夠。哪些敘述有這種清單也只由片語說一次，見產生器 <c>Data</c> 資料夾
    /// 裡片語的 <c>,*</c>；標頭中段長度不定的（<c>EXEC p @a = 1 WITH</c>）用 <c>...</c> 代表動詞到 WITH 之間。
    /// 仍各佔一格的清單：中間夾著的不是這一句的其餘標頭（<see cref="IndexOption"/> 前面是索引鍵與篩選），
    /// 選項寫完之後還有位置要回報（<see cref="ProcedureOption"/>、<see cref="TriggerOption"/>），
    /// 或不以逗號分隔（<see cref="CursorOption"/>）。
    /// </remarks>
    OptionItem = 1L << 62,

    /// <summary>SET 之後——ROWCOUNT、TEXTSIZE、IDENTITY_INSERT、TRANSACTION。</summary>
    SetTarget = 1 << 14,

    /// <summary>INSERT 之後——INTO、TOP。</summary>
    InsertTarget = 1 << 15,

    /// <summary>ALTER TABLE 的目標之後——ADD、ALTER、DROP、CHECK、ENABLE、SWITCH。</summary>
    AlterTableAction = 1 << 17,

    /// <summary>
    /// ALTER TABLE t ADD 與新增清單的逗號之後——CONSTRAINT、DEFAULT、PRIMARY、FOREIGN、UNIQUE、INDEX。
    /// </summary>
    /// <remarks>
    /// 這一格也接得了使用者自己取的新資料行名稱，所以分析器同時回報 <c>MaybeName</c>；
    /// 名稱寫完之後是型別，見 <c>SqlDataTypePosition</c>。
    /// </remarks>
    AlterTableAdd = 1 << 18,

    /// <summary>ALTER TABLE t ALTER／DROP COLUMN 之後要的那個既有資料行。</summary>
    AlterTableColumn = 1 << 19,

    /// <summary>SELECT 的 TOP 子句之後——PERCENT、WITH（TIES）。</summary>
    /// <remarks>
    /// 分析器同時回報 <see cref="SelectList"/>：TOP 寫完之後仍是選取清單的起點。
    /// 不併進 <see cref="SelectList"/> 的理由是 <c>SELECT |</c> 不接 PERCENT。
    /// </remarks>
    TopClauseTail = 1 << 21,

    /// <summary>SET 的選項名稱之後——ON、OFF；各選項自己的值由子句片語給，這裡是比對不上時的退路。</summary>
    SetOptionValue = 1 << 22,

    /// <summary>全部位置；分析器判不出上下文時使用。</summary>
    Any = StatementStart | SelectList | SelectListTail | DataSource
        | TableSourceTail | Predicate | ExpressionTail | OrderByTail | GroupByTail
        | OrderByColumn | ByAnchor | DdlObject | CaseArm | CaseBody
        | ColumnDefinition | ColumnDefinitionTail | BlockStart | BlockEnd | IfBodyEnd | CursorOption | SequenceOption | TriggerHeader
        | MergeWhen | MergeAction | MergeClause
        | PermissionList | PermissionTarget | PermissionOn | PermissionGrantee | SelectIntoTail | FetchTail | IndexKeyTail | UpdateSetTail
        | IndexOption | ProcedureOption | FunctionOption | ViewOption | TriggerOption
        | TriggerEvent | TriggerEventEnd | SetTarget | InsertTarget
        | ReferencesTail | FunctionCallTail | WindowOrderTail | WindowSpecification | WindowName | WindowClauseTail
        | OffsetTail | ModuleHeader | FunctionReturns
        | ResultSetList | ResultSetColumn | ResultSetColumnTail | OptionItem | TableSampleTail | PivotClause
        | AlterTableAction | AlterTableAdd | AlterTableColumn
        | TopClauseTail | SetOptionValue
}
