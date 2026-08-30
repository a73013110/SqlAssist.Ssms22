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
    /// 指令碼自己宣告的資料來源：CTE 與暫存資料表。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Table"/> 分開：它們沒有結構描述，因此提交時不能加
    /// <c>dbo.</c> 前置詞，也不該在 <c>FROM dbo.</c> 之後出現；
    /// 而且它們是使用者上一行才寫下的名稱，排名要在資料庫的資料表之前。
    /// </remarks>
    ScriptDataSource,

    /// <summary>資料庫；只出現在 <c>USE</c> 之後。</summary>
    Database
}

