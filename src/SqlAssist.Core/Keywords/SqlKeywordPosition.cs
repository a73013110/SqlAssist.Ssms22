using System;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 關鍵字可以出現的位置。
/// </summary>
/// <remarks>
/// 每個成員對應 <c>tools/Generate-Keywords.ps1</c> 裡的一個樣板；成員名稱與樣板名稱
/// 必須一致，產生器直接用名稱組出旗標。
///
/// 存在的理由是雜訊：關鍵字目錄有 180 個字，全部無條件列出來的話，打第一個字元時
/// 清單會被文法上根本不可能出現的字塞滿。分層之後 <c>WHERE</c> 之後不會冒出
/// <c>PROCEDURE</c>，語句開頭也不會冒出 <c>ASC</c>。
///
/// 位置的切法刻意對齊「游標前一個詞元」——分析器認得的就是那個。
/// 每個位置都必須是分析器回得出來的：產生器的樣板交給分析器必須回報含該位置的值，
/// 這一條由測試逐條回驗。只有產生器分得出、分析器判不出的位置放進去只是自欺——
/// 資料行型別之後（<c>CREATE TABLE t (a int |</c>）因此沒有自己的位置，那裡是 <see cref="Any"/>。
/// 分析器判不出來時回 <see cref="Any"/>，所有字都放行：寧可多列幾個字，
/// 也不要因為分析器看不懂上下文就把使用者要的關鍵字藏起來。
/// 反過來，產生器判不出來的字（<see cref="None"/>）只在那個時候出現，
/// 規則見 <see cref="SqlKeywordPositionExtensions.Allows(SqlKeywordPosition, SqlKeywordPosition)"/>。
/// </remarks>
[Flags]
public enum SqlKeywordPosition
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

    /// <summary>BEGIN 之後——TRANSACTION、TRY、CATCH。</summary>
    BlockStart = 1 << 13,

    /// <summary>SET 之後——ROWCOUNT、TEXTSIZE、IDENTITY_INSERT、TRANSACTION。</summary>
    SetTarget = 1 << 14,

    /// <summary>INSERT 之後——INTO、TOP。</summary>
    InsertTarget = 1 << 15,

    /// <summary>ALTER TABLE 的目標之後——ADD、ALTER、DROP、CHECK、ENABLE、SWITCH。</summary>
    AlterTableAction = 1 << 17,

    /// <summary>
    /// ALTER TABLE t ADD 之後——CONSTRAINT、DEFAULT、PRIMARY、FOREIGN、UNIQUE、INDEX。
    /// </summary>
    /// <remarks>
    /// 這一格也接得了使用者自己取的新資料行名稱，所以分析器同時回報 <c>MaybeName</c>。
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

    /// <summary>SET 的選項名稱之後——ON、OFF，隔離等級的 READ。</summary>
    SetOptionValue = 1 << 22,

    /// <summary>全部位置；分析器判不出上下文時使用。</summary>
    Any = StatementStart | SelectList | SelectListTail | DataSource
        | TableSourceTail | Predicate | ExpressionTail | OrderByTail | GroupByTail
        | OrderByColumn | ByAnchor | DdlObject | CaseArm | CaseBody
        | ColumnDefinition | BlockStart | SetTarget | InsertTarget
        | AlterTableAction | AlterTableAdd | AlterTableColumn
        | TopClauseTail | SetOptionValue
}
