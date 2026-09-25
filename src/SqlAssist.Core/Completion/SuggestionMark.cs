using System;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 建議清單列上名稱右側的例外標記；宣告順序就是圖示由左到右的順序。
/// </summary>
/// <remarks>
/// 只標例外：大部分列一顆都沒有，有標記的那一列才讀得出「這一筆和旁邊的不一樣」。
/// 人人有份的標記等於沒有，所以種類、結構描述這些每一列都有答案的屬性不做成標記——
/// 那是圖示與說明欄的事。警示排在前面，說明排名的放最後。
/// </remarks>
[Flags]
public enum SuggestionMark
{
    None = 0,

    /// <summary>會修改或刪除資料的片段；沒有前綴時本來就不顯示，打出捷徑之後要看得出它危險。</summary>
    Destructive = 1 << 0,

    /// <summary>已淘汰但仍可使用的內建型別。</summary>
    Deprecated = 1 << 1,

    /// <summary>與使用者物件混在同一份清單裡的系統物件。</summary>
    SystemObject = 1 << 2,

    /// <summary>最近提交過；解釋它為什麼排在同類別的前面。</summary>
    RecentlyUsed = 1 << 3
}

/// <summary>一筆建議在這個位置該掛哪些標記。</summary>
public static class SuggestionMarks
{
    /// <summary>所有標記的聯集；呈現端依此配置每一種組合的快取。</summary>
    public const SuggestionMark All =
        SuggestionMark.Destructive | SuggestionMark.Deprecated | SuggestionMark.SystemObject | SuggestionMark.RecentlyUsed;

    /// <summary>
    /// 只看建議項本身、上下文與使用紀錄就能決定的標記。
    /// </summary>
    /// <remarks>
    /// 最近用過與排名讀同一份 <see cref="SqlSuggestionUsage"/>，標記因此不會與順序說出
    /// 兩種答案。清單建立時定一次：平台之後每一次按鍵只重新比對，不再問來源。
    /// </remarks>
    public static SuggestionMark Of(SqlSuggestion suggestion, SqlCompletionContext context)
    {
        if (suggestion is null)
        {
            throw new ArgumentNullException(nameof(suggestion));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var marks = SuggestionMark.None;

        if (suggestion.IsDestructive)
        {
            marks |= SuggestionMark.Destructive;
        }

        if (IsDeprecated(suggestion))
        {
            marks |= SuggestionMark.Deprecated;
        }

        if (IsMixedSystemObject(suggestion, context))
        {
            marks |= SuggestionMark.SystemObject;
        }

        if (SqlSuggestionUsage.IsRecent(suggestion))
        {
            marks |= SuggestionMark.RecentlyUsed;
        }

        return marks;
    }

    /// <remarks>
    /// 只認型別建議本身。資料行型別是 <c>ntext</c> 時不標：那一列選的是資料行，
    /// 掛上「已淘汰」讀起來像資料行本身淘汰了，而寫查詢的人在那個位置也改不了型別——
    /// 那是結構健檢的事。
    /// </remarks>
    private static bool IsDeprecated(SqlSuggestion suggestion) =>
        suggestion.Kind == SuggestionKind.DataType &&
        SqlTypeName.IsDeprecatedLargeObject(suggestion.DisplayText);

    /// <remarks>
    /// 限定字本身就是系統結構描述（<c>sys.|</c>）時整份都是系統物件，標了等於每一列都標。
    /// 會混在一起的是沒有限定字的 <c>EXEC |</c>：<c>sp_help</c> 與使用者的程序並列，
    /// 名稱又常常一樣以 <c>sp_</c> 開頭。
    ///
    /// 結構描述名稱本身（<c>sys</c> 那一筆）與系統檢視的資料行不算：兩者都帶著
    /// 結構描述，但前者就是那個名稱，後者的結構描述是它所屬的那張表。
    /// </remarks>
    private static bool IsMixedSystemObject(SqlSuggestion suggestion, SqlCompletionContext context) =>
        suggestion.Kind is not (SuggestionKind.Schema or SuggestionKind.Column) &&
        SqlSystemSchemas.IsSystem(suggestion.SchemaName) &&
        !SqlSystemSchemas.IsSystem(context.Qualifier);
}
