namespace SqlAssist.Core.Parsing;

/// <summary>T-SQL 詞法單元的種類。</summary>
/// <remarks>
/// 這是純詞法分類，不含「是不是關鍵字」——關鍵字在 T-SQL 裡並非保留字，
/// <c>FROM [User]</c> 與 <c>FROM User</c> 都合法，是不是關鍵字要由上下文決定，
/// 不能在詞法階段就定死。
/// </remarks>
public enum SqlTokenKind
{
    /// <summary>識別字，包含一般名稱、方括號與雙引號名稱、暫存表名稱。</summary>
    Identifier,

    /// <summary>區域變數、資料表變數或系統函式，例如 <c>@p</c>、<c>@@ROWCOUNT</c>。</summary>
    Variable,

    /// <summary>數值常值。</summary>
    Number,

    /// <summary>單引號字串常值，含 <c>N</c> 前置詞。</summary>
    String,

    /// <summary>標點符號，例如 <c>. , ( ) ;</c>。</summary>
    Punctuation,

    /// <summary>運算子，例如 <c>= &lt;&gt; +</c>。</summary>
    Operator,

    /// <summary>
    /// 單行或區塊註解。
    /// </summary>
    /// <remarks>
    /// 只有指定 <c>includeComments</c> 的呼叫端才會拿到——語意分析要的是程式碼，
    /// 註解對它們只是雜訊；語法著色則相反，少了註解整段就會被畫成一般文字。
    /// </remarks>
    Comment
}
