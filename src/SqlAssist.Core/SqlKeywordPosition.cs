using System;

namespace SqlAssist.Core;

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
/// 位置的切法刻意對齊「游標前一個詞元」——分析器認得的就是那個，
/// 產生器分不出來、分析器也判不出來的位置放進去只是自欺。
/// 兩邊都判不出來時一律當成 <see cref="Any"/> 放行：寧可多列幾個字，
/// 也不要因為分析器看不懂上下文就把使用者要的關鍵字藏起來。
/// </remarks>
[Flags]
public enum SqlKeywordPosition
{
    /// <summary>產生器判定它進不了任何樣板；執行期視同 <see cref="Any"/>。</summary>
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

    /// <summary>ORDER BY 或 GROUP BY 的欄位之後——ASC、DESC。</summary>
    OrderByTail = 1 << 7,

    /// <summary>ORDER、GROUP 之後——BY。</summary>
    ByAnchor = 1 << 8,

    /// <summary>CREATE、ALTER、DROP 之後的物件類別。</summary>
    DdlObject = 1 << 9,

    /// <summary>CASE 的 WHEN 條件之後——THEN。</summary>
    CaseArm = 1 << 10,

    /// <summary>CASE 的 THEN 結果之後——WHEN、ELSE、END。</summary>
    CaseBody = 1 << 11,

    /// <summary>CREATE TABLE 的資料行型別之後——NOT、NULL、PRIMARY、IDENTITY、CONSTRAINT。</summary>
    ColumnDefinition = 1 << 12,

    /// <summary>BEGIN 之後——TRANSACTION、TRY、CATCH。</summary>
    BlockStart = 1 << 13,

    /// <summary>SET 之後——ROWCOUNT、TEXTSIZE、IDENTITY_INSERT、TRANSACTION。</summary>
    SetTarget = 1 << 14,

    /// <summary>INSERT 之後——INTO、TOP。</summary>
    InsertTarget = 1 << 15,

    /// <summary>全部位置；分析器判不出上下文，或關鍵字沒有分到任何位置時使用。</summary>
    Any = StatementStart | SelectList | SelectListTail | DataSource
        | TableSourceTail | Predicate | ExpressionTail | OrderByTail
        | ByAnchor | DdlObject | CaseArm | CaseBody | ColumnDefinition
        | BlockStart | SetTarget | InsertTarget
}
