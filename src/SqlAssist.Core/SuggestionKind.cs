namespace SqlAssist.Core;

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

    /// <summary>資料庫；只出現在 <c>USE</c> 之後。</summary>
    Database
}

