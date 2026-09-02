using System;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 把結果格線裡的一個值寫成 T-SQL 字面值。<b>所有結果格線命令的唯一出處。</b>
/// </summary>
/// <remarks>
/// <c>#temp</c> 的 <c>INSERT</c>、<c>IN</c> 條件、之後的 <c>UPDATE</c>／<c>DELETE</c>
/// 都經過這裡。各寫一份的症狀是其中一份改了另一份沒改，而分歧的形狀最惡劣：
/// 產出的 SQL 兩邊都執行得動，只是有一邊的值不對。
///
/// 三條規則，每一條背後都是一種「跑得動但錯了」：
///
/// 一、<b>日期一律寫成 ISO 8601</b>。<c>'2024-03-04'</c> 這種寫法會隨連線的
/// <c>DATEFORMAT</c> 與語言改變解讀，同一段指令碼在別人的連線上會安靜地變成
/// 另一天。分隔的 <c>yyyy-MM-ddTHH:mm:ss</c> 與純日期的 <c>yyyy-MM-dd</c>
/// 不受那兩個設定影響。
///
/// 二、<b>型別不確定時文字加 <c>N</c> 前綴</b>。多一個 <c>N</c> 插進
/// <c>varchar</c> 只是一次隱含轉換；少一個 <c>N</c> 插進 <c>nvarchar</c>
/// 是把非拉丁字元換成問號，沒有錯誤訊息。
///
/// 三、<b>轉不出來就說轉不出來</b>，不回退成字串。空間型別、<c>hierarchyid</c>、
/// <c>sql_variant</c> 的 <c>ToString()</c> 都給得出東西，包成 <c>N'...'</c>
/// 也插得進去，但那已經不是原本的值了。回報失敗讓呼叫端整段拒絕輸出——
/// 與 <c>SqlObjectStructure.CanBuildExecutableScript</c> 同一條理由。
/// </remarks>
public static class SqlValueLiteral
{
    /// <summary>SQL 的 <c>NULL</c>。</summary>
    public const string Null = "NULL";

    /// <summary>
    /// 轉成字面值。
    /// </summary>
    /// <param name="value">
    /// 格線給的值。<c>null</c> 與 <see cref="DBNull"/>，以及 <c>IsNull</c> 的
    /// <c>SqlTypes</c> 值，都轉成 <c>NULL</c>。
    /// </param>
    /// <param name="serverDataType">
    /// 伺服器回報的型別，例如 <c>nvarchar(50)</c>。只用來決定 <c>N</c> 前綴與
    /// 日期時間的精確度；取不到時全部往「不失真」的那一邊倒。
    /// </param>
    /// <param name="literal">成功時是字面值；失敗時是空字串。</param>
    /// <param name="reason">失敗時說明為什麼；成功時是空字串。</param>
    public static bool TryFormat(
        object? value,
        string? serverDataType,
        out string literal,
        out string reason)
    {
        reason = string.Empty;

        if (IsNullValue(value))
        {
            literal = Null;
            return true;
        }

        switch (value)
        {
            // SqlTypes 先攤成原生值，後面兩段就只需要處理一種形狀。
            case SqlString text:
                return TryText(text.Value, serverDataType, out literal, out reason);
            case SqlChars chars:
                return TryText(new string(chars.Value), serverDataType, out literal, out reason);
            case SqlXml xml:
                return TryText(xml.Value, serverDataType, out literal, out reason);
            case SqlBinary binary:
                return TryBinary(binary.Value, out literal, out reason);
            case SqlBytes bytes:
                return TryBinary(bytes.Value, out literal, out reason);
            case SqlGuid guid:
                return TryGuid(guid.Value, out literal, out reason);
            case SqlBoolean flag:
                literal = flag.Value ? "1" : "0";
                return true;
            case SqlByte number:
                return TryNumber(number.Value.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case SqlInt16 number:
                return TryNumber(number.Value.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case SqlInt32 number:
                return TryNumber(number.Value.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case SqlInt64 number:
                return TryNumber(number.Value.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case SqlDecimal number:
                // SqlDecimal 的精確度可以到 38 位，超過 decimal 裝得下的範圍，
                // 所以走它自己的 ToString() 而不是先轉成 decimal。
                return TryNumber(number.ToString(), out literal, out reason);
            case SqlMoney number:
                return TryNumber(number.Value.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case SqlSingle number:
                return TryNumber(number.Value.ToString("R", CultureInfo.InvariantCulture), out literal, out reason);
            case SqlDouble number:
                return TryNumber(number.Value.ToString("R", CultureInfo.InvariantCulture), out literal, out reason);
            case SqlDateTime moment:
                literal = DateTimeLiteral(moment.Value, serverDataType);
                return true;

            case string text:
                return TryText(text, serverDataType, out literal, out reason);
            case char[] chars:
                return TryText(new string(chars), serverDataType, out literal, out reason);
            case char character:
                return TryText(character.ToString(), serverDataType, out literal, out reason);
            case byte[] bytes:
                return TryBinary(bytes, out literal, out reason);
            case Guid guid:
                return TryGuid(guid, out literal, out reason);
            case bool flag:
                literal = flag ? "1" : "0";
                return true;
            case byte number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case sbyte number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case short number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case ushort number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case int number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case uint number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case long number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case ulong number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case decimal number:
                return TryNumber(number.ToString(CultureInfo.InvariantCulture), out literal, out reason);
            case float number:
                return TryNumber(number.ToString("R", CultureInfo.InvariantCulture), out literal, out reason);
            case double number:
                return TryNumber(number.ToString("R", CultureInfo.InvariantCulture), out literal, out reason);
            case DateTime moment:
                literal = DateTimeLiteral(moment, serverDataType);
                return true;
            case DateTimeOffset moment:
                literal = Quote(moment.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
                return true;
            case TimeSpan duration:
                return TryTime(duration, out literal, out reason);

            default:
                literal = string.Empty;
                reason = "CLR 型別 " + value!.GetType().Name + " 沒有對得上的 T-SQL 字面值寫法";
                return false;
        }
    }

    /// <summary>值是不是 SQL 的 <c>NULL</c>。</summary>
    /// <remarks>
    /// 三種形狀都是 <c>NULL</c>，而且都會出現：格線對真正的 <c>NULL</c> 回傳
    /// 純 <c>null</c>，<c>DBNull</c> 來自別的資料路徑，<c>SqlTypes</c> 則各自
    /// 有一個 <c>Null</c> 實例——後者最容易漏掉，因為 <c>SqlString.Null</c>
    /// 與有值的 <c>SqlString</c> 是同一個型別，不是 <c>null</c> 參考。
    /// </remarks>
    public static bool IsNullValue(object? value) =>
        value is null || value is DBNull || (value is INullable nullable && nullable.IsNull);

    /// <summary>把文字包成字面值，單引號跳脫成兩個。</summary>
    private static bool TryText(string? text, string? serverDataType, out string literal, out string reason)
    {
        reason = string.Empty;

        if (text is null)
        {
            literal = Null;
            return true;
        }

        var prefix = SqlTypeName.IsNonUnicodeText(serverDataType) ? string.Empty : "N";
        literal = prefix + Quote(text);
        return true;
    }

    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('\'');

        foreach (var character in text)
        {
            if (character == '\'')
            {
                builder.Append('\'');
            }

            builder.Append(character);
        }

        builder.Append('\'');
        return builder.ToString();
    }

    private static bool TryBinary(byte[]? bytes, out string literal, out string reason)
    {
        reason = string.Empty;

        if (bytes is null)
        {
            literal = Null;
            return true;
        }

        var builder = new StringBuilder(2 + (bytes.Length * 2));
        builder.Append("0x");

        foreach (var value in bytes)
        {
            builder.Append(HexDigits[value >> 4]).Append(HexDigits[value & 0xF]);
        }

        // 長度為零的 varbinary 是合法的值，但 0x 後面什麼都沒有不是合法的字面值。
        literal = bytes.Length == 0 ? "0x00" : builder.ToString();
        return true;
    }

    private const string HexDigits = "0123456789ABCDEF";

    private static bool TryGuid(Guid guid, out string literal, out string reason)
    {
        reason = string.Empty;
        literal = Quote(guid.ToString("D", CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>
    /// 數字直接寫，但先確認它真的只有數字。
    /// </summary>
    /// <remarks>
    /// 這一道守門擋的是格式化跟著地區設定跑：小數點在某些地區是逗號，
    /// 而 <c>1,5</c> 插進 <c>VALUES</c> 會被當成兩個值——多出來的那一欄讓整段
    /// 失敗，還算看得見；但 <c>1 234</c> 這種千分位就會變成語法錯誤在幾百列之後
    /// 才爆出來。每一個呼叫端都已經指定了 <see cref="CultureInfo.InvariantCulture"/>，
    /// 這裡是那件事的第二道保險，因為漏掉一個的症狀太難查。
    /// </remarks>
    private static bool TryNumber(string text, out string literal, out string reason)
    {
        foreach (var character in text)
        {
            if ((character < '0' || character > '9')
                && character != '.' && character != '-' && character != '+'
                && character != 'e' && character != 'E')
            {
                literal = string.Empty;
                reason = "數值格式化的結果含有非預期的字元，無法安全寫成字面值";
                return false;
            }
        }

        reason = string.Empty;
        literal = text.Length == 0 ? "0" : text;
        return true;
    }

    private static bool TryTime(TimeSpan duration, out string literal, out string reason)
    {
        // time 型別只到 24 小時，負值與跨日的 TimeSpan 沒有對應的字面值。
        if (duration < TimeSpan.Zero || duration >= TimeSpan.FromDays(1))
        {
            literal = string.Empty;
            reason = "時間值超出 time 型別的 0:00:00 至 23:59:59.9999999 範圍";
            return false;
        }

        reason = string.Empty;
        literal = Quote(duration.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>
    /// 依伺服器型別挑日期時間的精確度。
    /// </summary>
    /// <remarks>
    /// 精確度不能一律取最長：<c>'2024-03-04T00:00:00.0000000'</c> 插得進
    /// <c>date</c> 欄，但那已經不是使用者看到的東西了，而且 <c>IN</c> 條件貼出去
    /// 之後比對不到任何一列。型別取不到時才用最長的形式——寧可多幾位零，
    /// 也不要把 <c>datetime2(7)</c> 的尾數截掉。
    /// </remarks>
    private static string DateTimeLiteral(DateTime moment, string? serverDataType)
    {
        switch (SqlTypeName.BaseOf(serverDataType))
        {
            case "date":
                return Quote(moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            case "time":
                return Quote(moment.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));

            // smalldatetime 與 datetime 共用一種寫法。伺服器給回來的 smalldatetime
            // 秒數本來就是零，多寫出來的 :00.000 不會改變任何值，而少一份分支
            // 就少一次「其中一份改了另一份沒改」。
            case "smalldatetime":
            case "datetime":
                return Quote(moment.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));

            default:
                return Quote(moment.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        }
    }
}
