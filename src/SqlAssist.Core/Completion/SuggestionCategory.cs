using System;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 建議清單篩選列上的分類；宣告順序就是按鈕由左到右的順序。
/// </summary>
/// <remarks>
/// 順序是「使用者寫 SQL 時想到的順序」：先是敘述裡的欄位，再來是資料表，然後才是建立在
/// 資料表之上的檢視、預存程序、函式與序列；打字時隨手可得的關鍵字與片段放後面，限定名稱
/// 最左邊那一段（結構描述、資料庫）收在最後。順序只寫在這裡：平台照來源交出的順序畫按鈕，
/// 按鈕的數字快捷鍵又照畫面順序編，兩者都從這份宣告推出來。
/// </remarks>
public enum SuggestionCategory
{
    Column,
    Table,
    View,
    Procedure,
    ScalarFunction,
    TableFunction,
    Sequence,
    BuiltInFunction,
    Keyword,
    Snippet,
    SchemaOrDatabase
}

/// <summary>建議種類與篩選分類的對應。</summary>
public static class SuggestionCategories
{
    /// <summary>
    /// 建議項所屬的分類；不參與分類篩選的種類回傳 null。
    /// </summary>
    /// <remarks>
    /// 函式按使用方式分成內建、純量與資料表值，避免大量內建函式淹沒資料庫函式；內嵌與
    /// 多敘述同屬資料表值函式，呼叫位置相同，不按實作方式拆按鈕。
    ///
    /// CTE、暫存資料表與資料表變數歸在「資料表」：那顆按鈕問的是「這個位置我要一張表」，
    /// 而它們在那個位置就是表。獨立一顆的話，按下「資料表」之後使用者上一行才寫下的
    /// <c>#Loan</c> 反而消失，而那正是他最可能要選的那一個。
    ///
    /// 結構描述、資料庫與連結伺服器是限定名稱最左邊那一段，在 <c>FROM</c>、<c>EXEC</c>、
    /// <c>NEXT VALUE FOR</c> 之後與物件並列，所以有自己的一顆。
    ///
    /// 其餘種類（變數、型別、提示、定序…）只出現在文法只接受它們的那個位置，清單裡只有
    /// 它們自己那一類，篩選列根本不會出現，所以沒有分類。曾經收成一顆「其他」：它唯一
    /// 出現得了的地方是 <c>NEXT VALUE FOR</c> 之後，而那裡的「其他」其實全是序列——
    /// 一顆說不出內容的按鈕，不如讓序列有自己的名字。
    ///
    /// 每一種都要寫明，沒有預設分支：新增種類時漏了這裡，測試逐一列舉整個列舉就會失敗。
    /// </remarks>
    public static SuggestionCategory? Of(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Column => SuggestionCategory.Column,
        SuggestionKind.Table or SuggestionKind.ScriptDataSource => SuggestionCategory.Table,
        SuggestionKind.View => SuggestionCategory.View,
        SuggestionKind.Procedure => SuggestionCategory.Procedure,
        SuggestionKind.Function => SuggestionCategory.ScalarFunction,
        SuggestionKind.TableFunction => SuggestionCategory.TableFunction,
        SuggestionKind.Sequence => SuggestionCategory.Sequence,
        SuggestionKind.BuiltInFunction => SuggestionCategory.BuiltInFunction,
        SuggestionKind.Keyword => SuggestionCategory.Keyword,
        SuggestionKind.Snippet => SuggestionCategory.Snippet,
        SuggestionKind.Schema
            or SuggestionKind.Database
            or SuggestionKind.LinkedServer => SuggestionCategory.SchemaOrDatabase,
        SuggestionKind.GlobalVariable
            or SuggestionKind.Variable
            or SuggestionKind.DataType
            or SuggestionKind.Parameter
            or SuggestionKind.Trigger
            or SuggestionKind.UserDefinedType
            or SuggestionKind.DatePart
            or SuggestionKind.TableHint
            or SuggestionKind.QueryHint
            or SuggestionKind.Collation
            or SuggestionKind.CollationInUse => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
