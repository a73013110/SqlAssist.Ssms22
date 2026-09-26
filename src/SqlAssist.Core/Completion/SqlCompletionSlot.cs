namespace SqlAssist.Core.Completion;

/// <summary>
/// 游標所在的這一格，文法上要的是不是一個使用者自己取的名字。
/// </summary>
/// <remarks>
/// 與 <see cref="CompletionTarget"/>、關鍵字位置是不同的軸：那兩個回答「該列什麼」，
/// 這個回答「該不該列、列了之後預設選不選」。以前這件事借用關鍵字位置的
/// <c>None</c> 表示，而 <c>None</c> 同時又是「產生器判不出位置的字」，
/// 同一個值兩個意思，執行期只能靠呼叫端記得是哪一個。
///
/// 要不要因此開清單不在這裡決定，見 <see cref="SqlCompletionPolicy"/>。
/// </remarks>
public enum SqlCompletionSlot
{
    /// <summary>
    /// 文法上的一般位置：關鍵字、既有物件、運算式。
    /// </summary>
    /// <remarks>
    /// 放在第一個是刻意的：<c>default</c> 必須是「照常」，漏設的上下文才不會
    /// 安靜地變成不可補。
    /// </remarks>
    Grammar,

    /// <summary>不可補：字串、註解、數值常值裡面。</summary>
    Inert,

    /// <summary>
    /// 一定是新名字：<c>AS </c> 之後的別名、衍生資料表的別名、<c>DECLARE @</c>、
    /// <c>CREATE PROCEDURE </c>、CTE 名稱、<c>SELECT … INTO </c>。
    /// </summary>
    /// <remarks>清單裡沒有一項會是對的。</remarks>
    Name,

    /// <summary>
    /// 可能是新名字，也可能是關鍵字或既有物件：選取清單與資料來源的一項寫完之後
    /// 同一行的那一格、資料行定義的開頭、<c>CREATE OR ALTER PROCEDURE </c>。
    /// </summary>
    /// <remarks>
    /// <c>FROM dbo.T W</c> 的 <c>W</c> 可能是別名，也可能是打到一半的 <c>WHERE</c>，
    /// 只看文字分不出來。清單照開，但預設不選中任何一項。
    /// </remarks>
    MaybeName
}
