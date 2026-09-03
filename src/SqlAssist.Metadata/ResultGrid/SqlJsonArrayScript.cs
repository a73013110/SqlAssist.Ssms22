using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Text;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 把一塊查詢結果寫成 JSON 陣列，一列一個物件。
/// </summary>
/// <remarks>
/// SSMS 22 自己的「另存結果為…」也有 JSON，但它與 Markdown 那條路一樣：一律寫成
/// 檔案、一律整份結果。這裡補的是同一個落差——要餵給 API 的測試資料、要貼進
/// 設定檔或工單的幾列，去處是剪貼簿，不是桌面上一個 .json 檔。
///
/// <b>JSON 是這一組命令裡唯一分得出 <c>NULL</c> 的文字格式。</b>剪貼簿那份 TSV
/// 裡，資料庫的 <c>NULL</c> 與一個內容剛好是 <c>NULL</c> 這四個字的字串長得一模一樣；
/// 這裡一個是 <c>null</c>、一個是 <c>"NULL"</c>，連 Markdown 要靠斜體區分的
/// 那一招都不必用。
///
/// 型別對應到 JSON 只有三種去處，而挑錯的代價各不相同：數值寫成字串之後，
/// 收下這份 JSON 的那一端要嘛比對失敗、要嘛自己再轉一次；字串寫成數值則會掉前導零
/// ——正是 <see cref="SqlTempTableScript"/> 那段「別從值反推型別」講的同一件事，
/// 所以這裡也只看伺服器給的型別，不看值長什麼樣。
///
/// 值走 <see cref="ResultGridCellText.Display"/>，與 Markdown 表格同一份：
/// 日期的精確度、二進位的十六進位寫法在兩個命令裡才不會長得不一樣。
/// 這也代表轉不成 T-SQL 字面值的型別（空間型別之類）在這裡<b>不會</b>整段拒絕——
/// 這一份的用途是讀與貼，不是拿去執行，退回 <c>ToString()</c> 仍然看得懂。
/// </remarks>
public static class SqlJsonArrayScript
{
    /// <summary>產不出來時的第一句。</summary>
    private const string UnavailableHeadline = "無法從查詢結果產生 JSON。";

    public static string Build(ResultGridTable table)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        if (table.IsEmpty)
        {
            return ResultGridLiterals.Unavailable(
                UnavailableHeadline,
                "選取範圍裡沒有資料列，或這份結果沒有欄位。",
                "先在結果格線裡選幾格，再執行一次這個命令。");
        }

        // 欄名走 ScriptColumnNames 而不是原始欄名：JSON 物件的鍵重複時，
        // 各家剖析器有的取第一個、有的取最後一個，兩種都會安靜地少掉一欄。
        var names = table.ScriptColumnNames;
        var keys = new string[names.Count];

        for (var column = 0; column < names.Count; column++)
        {
            keys[column] = Quote(names[column]);
        }

        var builder = new StringBuilder(EstimateLength(table, keys));
        builder.AppendLine("[");

        for (var row = 0; row < table.Rows.Count; row++)
        {
            AppendRow(builder, table, keys, row);
            builder.AppendLine(row == table.Rows.Count - 1 ? string.Empty : ",");
        }

        builder.Append(']');
        return builder.ToString();
    }

    /// <remarks>
    /// 一列一個物件、每一個鍵各自一行。全部擠成一行的話，這份東西貼進工單或
    /// 設定檔之後沒有人讀得下去，而它的去處正是那些地方。
    /// </remarks>
    private static void AppendRow(
        StringBuilder builder,
        ResultGridTable table,
        string[] keys,
        int row)
    {
        var values = table.Rows[row];
        builder.AppendLine("  {");

        for (var column = 0; column < keys.Length; column++)
        {
            var value = column < values.Length ? values[column] : null;

            builder.Append("    ").Append(keys[column]).Append(": ")
                .Append(Value(table.Columns[column], value))
                .AppendLine(column == keys.Length - 1 ? string.Empty : ",");
        }

        builder.Append("  }");
    }

    /// <summary>一格的 JSON 值。</summary>
    private static string Value(ResultGridColumn column, object? value)
    {
        if (SqlValueLiteral.IsNullValue(value))
        {
            return "null";
        }

        switch (value)
        {
            case bool flag:
                return flag ? "true" : "false";
            case SqlBoolean flag:
                return flag.Value ? "true" : "false";
        }

        var text = ResultGridCellText.Display(column, value, ResultGridTextStyle.Plain);

        // 數值型別才寫成 JSON 數值，而且還要真的長得像數值。.NET 的 "R" 格式對
        // float 會給 1E-06 這種寫法，JSON 收得下；但只要有一個字元不合，
        // 整份 JSON 就剖析失敗，所以檢查不過的一律退回字串。
        return IsNumeric(value) && IsJsonNumber(text) ? text : Quote(text);
    }

    /// <remarks>
    /// 看 CLR 型別而不是看文字：一整欄看起來都是整數，實際上是 <c>varchar</c>
    /// 而其中一列有前導零時，寫成 JSON 數值就把那一列的值改掉了。
    /// </remarks>
    private static bool IsNumeric(object? value)
    {
        switch (value)
        {
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
            case SqlByte:
            case SqlInt16:
            case SqlInt32:
            case SqlInt64:
            case SqlDecimal:
            case SqlMoney:
            case SqlSingle:
            case SqlDouble:
                return true;
            default:
                return false;
        }
    }

    /// <summary>這串字是不是一個 JSON 數值字面值。</summary>
    private static bool IsJsonNumber(string text)
    {
        var index = 0;

        if (index < text.Length && text[index] == '-')
        {
            index++;
        }

        if (!SkipDigits(text, ref index))
        {
            return false;
        }

        if (index < text.Length && text[index] == '.')
        {
            index++;

            if (!SkipDigits(text, ref index))
            {
                return false;
            }
        }

        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            index++;

            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                index++;
            }

            if (!SkipDigits(text, ref index))
            {
                return false;
            }
        }

        return index == text.Length;
    }

    /// <summary>吃掉一段十進位數字；一個都沒有時回傳 <c>false</c>。</summary>
    private static bool SkipDigits(string text, ref int index)
    {
        var start = index;

        while (index < text.Length && text[index] >= '0' && text[index] <= '9')
        {
            index++;
        }

        return index > start;
    }

    /// <remarks>
    /// 跳脫的是 JSON 硬性要求的那幾個：引號、反斜線，以及所有 U+0020 以下的
    /// 控制字元。非 ASCII 一律照原樣寫出去而不是轉成 <c>\uXXXX</c>——這份東西
    /// 是給人讀的，而中文欄位值轉成六個字元一組之後就沒得讀了。
    /// </remarks>
    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <remarks>
    /// 鍵在每一列都重寫一次，所以預估要把它算進去——178 欄的結果選滿 1000 列時，
    /// 光是鍵就佔掉整份輸出的一半以上。少算的代價是 <see cref="StringBuilder"/>
    /// 一路重新配置，而那正是這條路徑上最貴的一段。
    /// </remarks>
    private static int EstimateLength(ResultGridTable table, IReadOnlyList<string> keys)
    {
        const int PerCellOverhead = 12;
        var perRow = 8;

        foreach (var key in keys)
        {
            perRow += key.Length + PerCellOverhead;
        }

        return (perRow * table.Rows.Count) + 16;
    }
}
