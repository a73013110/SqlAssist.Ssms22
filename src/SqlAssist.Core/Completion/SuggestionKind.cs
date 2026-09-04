namespace SqlAssist.Core.Completion;

public enum SuggestionKind
{
    Keyword,
    Snippet,
    Schema,
    Table,
    View,
    Procedure,
    Function,
    Column,

    /// <summary>
    /// T-SQL 內建函式（COUNT、GETDATE…）。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Function"/> 分開：那是資料庫裡的使用者自訂函式，
    /// 改得動也刪得掉，因此會出現在 <c>ALTER FUNCTION</c> 之後。
    /// 內建函式沒有定義可以改，混在一起會讓 <c>ALTER FUNCTION COUNT</c>
    /// 出現在清單裡。
    /// </remarks>
    BuiltInFunction,

    /// <summary>
    /// 資料表值函式：內嵌的（<c>IF</c>）與多敘述的（<c>TF</c>）。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Function"/> 分開：那一類是純量函式，只出現在運算式位置；
    /// 這一類接得上 <c>FROM</c>、<c>JOIN</c> 與 <c>APPLY</c>，因為它回傳的是一份資料列集。
    /// 中繼資料層的 <c>SqlObjectKinds.IsDataSource</c> 早就這樣分了，只有建議項這一層
    /// 把三種函式壓成同一類——症狀是 <c>FROM dbo.fn_</c> 之後整份清單一個函式都沒有，
    /// 而使用者看不出它和資料表有什麼不同。
    ///
    /// 反過來也不能併進 <see cref="Table"/>：那樣 <c>ALTER FUNCTION</c>／
    /// <c>DROP FUNCTION</c> 之後就列不出它們了。
    /// </remarks>
    TableFunction,

    /// <summary>
    /// 指令碼自己宣告的資料來源：CTE 與暫存資料表。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Table"/> 分開：它們沒有結構描述，因此提交時不能加
    /// <c>dbo.</c> 前置詞，也不該在 <c>FROM dbo.</c> 之後出現；
    /// 而且它們是使用者上一行才寫下的名稱，排名要在資料庫的資料表之前。
    /// </remarks>
    ScriptDataSource,

    /// <summary>資料庫；只出現在 <c>USE</c> 之後。</summary>
    Database,

    /// <summary>
    /// T-SQL 全域變數（<c>@@ROWCOUNT</c>、<c>@@VERSION</c>…）。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="BuiltInFunction"/> 分開：那些名稱會混在一般清單裡參與比對，
    /// 而這一類只在使用者打出 <c>@@</c> 之後出現。混在一起的話，每一次按鍵的
    /// 候選清單都要多背 32 個一定比不中的名稱。
    /// </remarks>
    GlobalVariable,

    /// <summary>
    /// 指令碼自己宣告的變數與參數（<c>@rows</c>、<c>@readerId</c>…）。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="ScriptDataSource"/> 同一種東西——只存在於這份指令碼裡、
    /// 中繼資料看不到的名稱——但不能歸在一起：那一類接在 <c>FROM</c> 後面，
    /// 這一類接在 <c>@</c> 後面，兩者從來不會出現在同一個位置。
    /// </remarks>
    Variable,

    /// <summary>
    /// T-SQL 內建資料型別（<c>INT</c>、<c>NVARCHAR</c>…）。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Keyword"/> 分開：型別在文法上不是關鍵字，關鍵字目錄裡
    /// 一個都沒有；而且它們只出現在文法只接受型別的那幾個位置。
    /// </remarks>
    DataType,

    /// <summary>
    /// <c>EXEC</c> 正在呼叫的那個模組的參數名稱。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Variable"/> 分開：兩者在 <c>EXEC p @|</c> 同時出現，
    /// 但來源與插入文字都不同——這一類來自中繼資料，而且提交時連
    /// <c> = </c> 一起寫進去，因為打出參數名稱就是要做具名傳值。
    /// </remarks>
    Parameter,

    /// <summary>觸發程序；只出現在 <c>ALTER</c>、<c>DROP</c>、<c>DISABLE</c>、
    /// <c>ENABLE TRIGGER</c> 之後。</summary>
    /// <remarks>
    /// 與 <see cref="Procedure"/> 分開：觸發程序不能 <c>EXEC</c>，
    /// 混在一起會讓它出現在 <c>EXEC </c> 之後——那裡選到它一定執行失敗。
    /// </remarks>
    Trigger,

    /// <summary>序列；只出現在 <c>NEXT VALUE FOR</c> 與 <c>ALTER</c>、
    /// <c>DROP SEQUENCE</c> 之後。</summary>
    Sequence,

    /// <summary>
    /// 使用者自訂資料表型別（<c>DECLARE @t dbo.XType</c>）。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="DataType"/> 分開：內建型別沒有結構描述，這一類有，
    /// 插入時要照物件的規則補上 <c>dbo.</c> 與方括號。
    /// </remarks>
    UserDefinedType,

    /// <summary><c>DATEADD</c> 這一族第一個引數的日期部分（<c>DAY</c>、<c>MONTH</c>…）。</summary>
    DatePart,

    /// <summary><c>WITH (…)</c> 的資料表提示（<c>NOLOCK</c>、<c>UPDLOCK</c>…）。</summary>
    TableHint,

    /// <summary><c>OPTION (…)</c> 的查詢提示（<c>RECOMPILE</c>、<c>MAXDOP</c>…）。</summary>
    QueryHint,

    /// <summary>連結伺服器；四段式名稱的第一段。</summary>
    /// <remarks>
    /// 與 <see cref="Database"/> 分開：資料庫只在 <c>USE</c> 之後與連結伺服器之後
    /// 才對，而這一類接在 <c>FROM</c>、<c>JOIN</c> 之後——兩者從來不出現在同一格。
    ///
    /// 加在列舉<b>最後面</b>而不是排在 <see cref="Database"/> 旁邊：
    /// <see cref="SqlSuggestionUsage"/> 把 <c>(int)Kind</c> 寫進使用紀錄當鍵，
    /// 插在中間會讓既有紀錄整批對到別的類別上。
    /// </remarks>
    LinkedServer
}

