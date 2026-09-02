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
}
