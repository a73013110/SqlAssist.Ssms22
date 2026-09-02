using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Statements;

/// <summary>
/// 骨架裡要先填進去的字面值。
/// </summary>
/// <remarks>
/// 這些值一律是<b>預留位置</b>，不是猜出來的內容：展開出來的 INSERT 與 EXEC 本來就要
/// 使用者填完才執行。挑選的標準因此只有兩條——看得出來是要改的（空字串、零），
/// 而且插得進去（不會在轉型那一步就失敗）。
///
/// 沒有這個標準的話很容易掉進反方向：給日期一個 <c>GETDATE()</c> 看起來體貼，
/// 但那是替使用者決定內容，而且它執行得動，於是錯的值會安靜地寫進資料表。
/// </remarks>
public static class SqlLiteralDefaults
{
    /// <summary>
    /// 依型別給一個預留字面值。
    /// </summary>
    /// <param name="dataType">
    /// 格式化過的型別，例如 <c>nvarchar(100)</c>、<c>decimal(18,2)</c>；
    /// 只看左括號之前那一段。
    /// </param>
    public static string ForType(string? dataType)
    {
        switch (SqlTypeName.BaseOf(dataType))
        {
            // Unicode 字串少了 N 前綴會先降成非 Unicode 再轉回來，那是使用者沒要求的失真。
            case "nchar":
            case "nvarchar":
            case "ntext":
            case "sysname":
                return "N''";

            case "char":
            case "varchar":
            case "text":
            case "xml":
                return "''";

            case "bit":
            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
            case "decimal":
            case "numeric":
            case "float":
            case "real":
            case "money":
            case "smallmoney":
                return "0";

            case "binary":
            case "varbinary":
            case "image":
            case "timestamp":
            case "rowversion":
                return "0x";

            // 空字串轉成日期是 1900-01-01——那是一個執行得動的錯值，正是要避免的那種。
            // NULL 在 NOT NULL 的欄位上會失敗，而失敗看得見。
            case "date":
            case "time":
            case "datetime":
            case "datetime2":
            case "smalldatetime":
            case "datetimeoffset":
                return "NULL";

            case "uniqueidentifier":
                return "NEWID()";

            // 空間型別、hierarchyid、sql_variant 與使用者自訂型別沒有共通的字面值寫法。
            default:
                return "NULL";
        }
    }
}
