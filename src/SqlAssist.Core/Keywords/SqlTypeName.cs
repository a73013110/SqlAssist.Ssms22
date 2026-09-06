using System;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 型別名稱的字串層面判斷：取基底名稱，以及分辨文字型別要不要 <c>N</c> 前綴。
/// </summary>
/// <remarks>
/// 拆出來的理由是它同時有兩個呼叫端，而兩邊各寫一份的症狀是靜悄悄的：
/// <see cref="SqlAssist.Core.Statements.SqlLiteralDefaults"/> 拿它挑骨架的預留值，
/// 結果格線的常值轉換拿它決定 <c>N''</c>。其中一份漏了新的型別別名，
/// 產出的字串仍然合法、仍然執行得動，只是值不對。
///
/// 這裡只看字面，不查目錄：使用者自訂型別與別名走不到這裡，那是刻意的——
/// 猜錯一個別名的代價比回答「不知道」大得多。
/// </remarks>
public static class SqlTypeName
{
    /// <summary>
    /// 型別的大類。
    /// </summary>
    /// <remarks>
    /// 只給結構健檢用：問「這兩個資料行是不是同一類的東西」時，
    /// <c>varchar</c> 與 <c>nvarchar</c> 算同一類（值得比對），
    /// 而 <c>int</c> 與 <c>uniqueidentifier</c> 不算——後者的差異多半是刻意的，
    /// 一個是流水號一個是對外的識別碼，報出來只是雜訊。
    /// </remarks>
    public enum Family
    {
        Other,

        Text,

        Integer,

        Numeric,

        DateAndTime,

        Binary
    }

    /// <summary>取左括號之前那一段並轉成小寫；<c>decimal(18,2)</c> 得到 <c>decimal</c>。</summary>
    public static string BaseOf(string? dataType)
    {
        if (string.IsNullOrEmpty(dataType))
        {
            return string.Empty;
        }

        var parenthesis = dataType!.IndexOf('(');
        var name = parenthesis < 0 ? dataType : dataType.Substring(0, parenthesis);
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>是不是 Unicode 文字型別（字面值要加 <c>N</c> 前綴）。</summary>
    public static bool IsUnicodeText(string? dataType)
    {
        switch (BaseOf(dataType))
        {
            case "nchar":
            case "nvarchar":
            case "ntext":
            case "sysname":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 是不是<b>確定</b>不需要 <c>N</c> 前綴的文字型別。
    /// </summary>
    /// <remarks>
    /// 問句是「確定不用」而不是「用不用」：型別名稱查不到時要回答 false，
    /// 讓呼叫端加上 <c>N</c>。多一個 <c>N</c> 插進 <c>varchar</c> 欄位只是一次
    /// 隱含轉換；少一個 <c>N</c> 插進 <c>nvarchar</c> 欄位是把非拉丁字元
    /// 換成問號，而那不會有任何錯誤訊息。
    /// </remarks>
    public static bool IsNonUnicodeText(string? dataType)
    {
        switch (BaseOf(dataType))
        {
            case "char":
            case "varchar":
            case "text":
                return true;
            default:
                return false;
        }
    }

    /// <summary>型別屬於哪一個大類；認不得的一律是 <see cref="Family.Other"/>。</summary>
    public static Family FamilyOf(string? dataType)
    {
        switch (BaseOf(dataType))
        {
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
            case "text":
            case "ntext":
            case "sysname":
                return Family.Text;

            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
                return Family.Integer;

            case "decimal":
            case "numeric":
            case "money":
            case "smallmoney":
            case "float":
            case "real":
                return Family.Numeric;

            case "date":
            case "time":
            case "datetime":
            case "datetime2":
            case "smalldatetime":
            case "datetimeoffset":
                return Family.DateAndTime;

            case "binary":
            case "varbinary":
            case "image":
                return Family.Binary;

            default:
                return Family.Other;
        }
    }

    /// <summary>
    /// 是不是大型物件：<c>max</c> 長度，或已經淘汰的 <c>text</c>／<c>ntext</c>／<c>image</c>。
    /// </summary>
    /// <remarks>
    /// 長度看的是格式化後的字串裡有沒有 <c>(max)</c>。這一層刻意不收
    /// <c>max_length</c>：問這個問題的地方拿到的是已經組好的型別字串，
    /// 而多帶一個位元組長度進來會讓兩種來源各自算一次「這算不算 max」。
    /// </remarks>
    public static bool IsLargeObject(string? dataType)
    {
        switch (BaseOf(dataType))
        {
            case "text":
            case "ntext":
            case "image":
            case "xml":
                return true;
        }

        return dataType is not null &&
               dataType.IndexOf("(max)", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>已經淘汰、官方建議改用 <c>max</c> 型別的那三個。</summary>
    public static bool IsDeprecatedLargeObject(string? dataType)
    {
        switch (BaseOf(dataType))
        {
            case "text":
            case "ntext":
            case "image":
                return true;
            default:
                return false;
        }
    }
}
