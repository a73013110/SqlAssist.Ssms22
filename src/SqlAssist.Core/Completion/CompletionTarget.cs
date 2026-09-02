namespace SqlAssist.Core.Completion;

public enum CompletionTarget
{
    Any,
    DataSource,
    Procedure,

    /// <summary><c>ALTER</c>、<c>DROP FUNCTION</c> 之後；純量函式與資料表值函式都算。</summary>
    /// <remarks>
    /// 這裡是<b>宣告</b>位置，不是呼叫位置：提交之後要的只有名稱，補上引數清單反而
    /// 讓那句 DDL 語法錯誤。分辨呼叫與宣告的就是這個目標，
    /// 見 <c>SqlCommitExpander.Resolve</c>。
    /// </remarks>
    Function,

    /// <summary><c>CROSS</c>／<c>OUTER APPLY</c> 之後。</summary>
    /// <remarks>
    /// 與 <see cref="Function"/> 分開：那裡是 DDL 的宣告位置，這裡是呼叫位置，
    /// 而且文法上只接得了資料表值函式——純量函式放在 <c>APPLY</c> 後面剖析不過。
    /// 併在一起的代價有兩個：清單裡混進一批選不中的純量函式，
    /// 而且提交時分不出「要補引數」還是「只要名稱」。
    ///
    /// 與 <see cref="DataSource"/> 也分開：<c>APPLY</c> 後面放資料表雖然剖析得過，
    /// 卻沒有任何意義——那正是 <c>CROSS JOIN</c> 該做的事。
    /// </remarks>
    TableFunction,

    /// <summary>限定字解析成敘述中的資料來源，因此建議該來源的欄位。</summary>
    Column,

    /// <summary>
    /// <c>USE</c> 之後，建議這台伺服器上的資料庫。
    /// </summary>
    /// <remarks>
    /// 與其他目標不同，這裡要的東西不在目前連線的資料庫裡，而在伺服器層級。
    /// </remarks>
    Database,

    /// <summary>
    /// 游標停在 <c>@@</c> 開頭的詞元上，因此只建議 T-SQL 全域變數。
    /// </summary>
    /// <remarks>
    /// 目標由<b>正在輸入的詞元</b>決定，而不是由前導關鍵字決定，這是唯一的一個
    /// ——<c>@@</c> 開頭的名稱在 T-SQL 裡只有這一種意思，前面是什麼子句都不改變
    /// 這件事。也因此它與 <see cref="Database"/> 一樣可以跳過「輸入幾個字元之後
    /// 才建議」：使用者打完那兩個小老鼠時，要什麼已經說完了。
    /// </remarks>
    GlobalVariable,

    /// <summary>
    /// 游標停在單一個 <c>@</c> 開頭的詞元上，因此只建議這份指令碼宣告過的變數。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="GlobalVariable"/> 一樣由正在輸入的詞元決定，差別在名稱的來源：
    /// 那一份是系統的，這一份是使用者自己在上面幾行寫下的。
    ///
    /// 使用者正在<b>宣告</b>名字的位置（<c>DECLARE @</c>、<c>CREATE PROCEDURE p @</c>）
    /// 不會走到這裡——那裡整份上下文都不算數，見
    /// <see cref="SqlScriptVariableSuggestions.IsDeclarationSlot"/>。
    /// </remarks>
    Variable,

    /// <summary>
    /// 游標停在文法上只接受資料型別的位置（<c>DECLARE @rows </c>、
    /// <c>CAST(x AS </c>…），因此只建議內建型別。
    /// </summary>
    /// <remarks>
    /// 哪些位置算數見 <see cref="SqlDataTypePosition"/>。判定成立時整份清單就只剩
    /// 型別，關鍵字一個都不列——那些位置本來就沒有別的東西是對的。
    /// 使用者自訂的資料表型別也在這個目標裡，它們與內建型別在同一個位置。
    /// </remarks>
    DataType,

    /// <summary><c>ALTER</c>、<c>DROP VIEW</c> 之後。</summary>
    /// <remarks>
    /// 檢視同時是資料來源，因此 <see cref="DataSource"/> 裡本來就有它；分出這一個
    /// 是為了 <c>ALTER VIEW</c>／<c>DROP VIEW</c> 那兩個位置——那裡列出資料表只會讓
    /// 使用者選到一個在該語句裡一定失敗的名稱。
    /// </remarks>
    View,

    /// <summary><c>ALTER</c>、<c>DROP</c>、<c>DISABLE</c>、<c>ENABLE TRIGGER</c> 之後。</summary>
    Trigger,

    /// <summary><c>NEXT VALUE FOR</c>、<c>ALTER</c>、<c>DROP SEQUENCE</c> 之後。</summary>
    Sequence,

    /// <summary><c>DATEADD(</c> 這一族的第一個引數。</summary>
    DatePart,

    /// <summary><c>WITH (</c> 的資料表提示。</summary>
    TableHint,

    /// <summary><c>OPTION (</c> 的查詢提示。</summary>
    QueryHint
}

