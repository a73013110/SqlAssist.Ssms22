using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 把一塊查詢結果寫成 Markdown 表格。
/// </summary>
/// <remarks>
/// SSMS 22 自己就有 Markdown 匯出（「另存結果為…」的 <c>GridSaveFormats.Markdown</c>），
/// <b>所以這裡刻意只做它沒有做的那一半</b>：內建那條路一律寫成檔案、一律整份結果，
/// 看不到選取範圍。而真正要貼進工單、PR 或聊天室的時候，要的是剪貼簿裡的幾列，
/// 不是桌面上一個 .md 檔。XLSX 就沒有這個落差，內建的匯出已經夠用，這裡不重做。
///
/// 表格是給人讀的，所以值走 <see cref="ResultGridCellText.Display"/> 而不是
/// <see cref="SqlValueLiteral"/>：字串不帶引號、不跳脫，日期不帶引號。
/// 但兩者共用同一份型別判斷，日期的精確度才不會在兩個地方長得不一樣。
/// </remarks>
public static class SqlMarkdownTableScript
{
    /// <summary>
    /// 真正的 <c>NULL</c> 在表格裡寫成這個。
    /// </summary>
    /// <remarks>
    /// 用斜體而不是 <c>NULL</c> 四個字：一個內容剛好是 <c>NULL</c> 的字串在表格裡
    /// 就是那四個字，兩者混在一起之後讀的人分不出來——正是整組結果格線功能一開始
    /// 要解決的那個問題，不該在最後一步又混回去。渲染出來一個是斜體一個不是。
    /// </remarks>
    public const string NullMarker = "*NULL*";

    /// <summary>產不出來時的第一句。</summary>
    private const string UnavailableHeadline = "無法從查詢結果產生 Markdown 表格。";

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

        var names = table.ScriptColumnNames;
        var cells = new string[table.Rows.Count][];
        var widths = new int[names.Count];

        for (var column = 0; column < names.Count; column++)
        {
            widths[column] = Escape(names[column]).Length;
        }

        for (var row = 0; row < table.Rows.Count; row++)
        {
            var values = table.Rows[row];
            cells[row] = new string[names.Count];

            for (var column = 0; column < names.Count; column++)
            {
                var value = column < values.Length ? values[column] : null;

                var text = SqlValueLiteral.IsNullValue(value)
                    ? NullMarker
                    : Escape(ResultGridCellText.Display(
                        table.Columns[column],
                        value,
                        ResultGridTextStyle.Plain));

                cells[row][column] = text;
                widths[column] = Math.Max(widths[column], text.Length);
            }
        }

        return Render(table, names, cells, widths);
    }

    /// <remarks>
    /// 欄寬對齊是為了讓原始碼本身讀得下去。Markdown 渲染出來不看空白，但這段東西
    /// 有一半的時間是被貼進 PR 的說明欄，而那裡看到的就是原始碼。
    ///
    /// 對齊有上限：一欄裡有一段 4000 字的備註時，把每一列都補到 4000 欄會讓整份
    /// 完全讀不了，比不對齊還糟。超過上限的欄就照原樣寫出來。
    /// </remarks>
    private const int MaxAlignedWidth = 60;

    private static string Render(
        ResultGridTable table,
        IReadOnlyList<string> names,
        string[][] cells,
        int[] widths)
    {
        for (var column = 0; column < widths.Length; column++)
        {
            widths[column] = Math.Min(widths[column], MaxAlignedWidth);
        }

        // 不加「由 SqlAssist 產生」那一行。另外兩個命令加是因為它們的產出是 SQL，
        // 那一行是註解；這一份的去處是工單或 PR 的說明欄，多一行就是使用者要刪的一行。
        // 形狀由狀態列那句話交代。
        var builder = new StringBuilder(ResultGridLiterals.EstimateLength(cells) + 256);

        AppendRow(builder, Escape(names), widths);
        AppendSeparator(builder, table, widths);

        foreach (var row in cells)
        {
            AppendRow(builder, row, widths);
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells, int[] widths)
    {
        builder.Append('|');

        for (var column = 0; column < cells.Count; column++)
        {
            builder.Append(' ').Append(cells[column])
                .Append(' ', Math.Max(widths[column] - cells[column].Length, 0))
                .Append(" |");
        }

        builder.AppendLine();
    }

    /// <remarks>
    /// 數值與日期靠右對齊。這不是裝飾：一整欄數字靠右才對得起小數點，
    /// 而那是這張表被貼出去之後第一個要看的東西。
    /// </remarks>
    private static void AppendSeparator(StringBuilder builder, ResultGridTable table, int[] widths)
    {
        builder.Append('|');

        for (var column = 0; column < widths.Length; column++)
        {
            var right = IsRightAligned(table.Columns[column].BaseTypeName);
            var width = Math.Max(widths[column], 3);

            builder.Append(' ').Append('-', right ? width - 1 : width);

            if (right)
            {
                builder.Append(':');
            }

            builder.Append(" |");
        }

        builder.AppendLine();
    }

    private static bool IsRightAligned(string baseTypeName)
    {
        switch (baseTypeName)
        {
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
            case "date":
            case "time":
            case "datetime":
            case "datetime2":
            case "smalldatetime":
            case "datetimeoffset":
                return true;
            default:
                return false;
        }
    }

    private static string[] Escape(IReadOnlyList<string> values)
    {
        var escaped = new string[values.Count];

        for (var index = 0; index < values.Count; index++)
        {
            escaped[index] = Escape(values[index]);
        }

        return escaped;
    }

    /// <remarks>
    /// 兩個字元會拆掉表格：豎線切出一欄不存在的欄，換行把一列切成兩列。
    /// 豎線跳脫成 <c>\|</c>，換行換成 <c>&lt;br&gt;</c>——後者是 Markdown
    /// 表格裡唯一到處都認得的換行寫法。
    /// </remarks>
    private static string Escape(string value)
    {
        if (value.IndexOf('|') < 0 && value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '|':
                    builder.Append("\\|");
                    break;

                case '\r':
                    // CRLF 只換一次。
                    if (index + 1 < value.Length && value[index + 1] == '\n')
                    {
                        index++;
                    }

                    builder.Append("<br>");
                    break;

                case '\n':
                    builder.Append("<br>");
                    break;

                default:
                    builder.Append(value[index]);
                    break;
            }
        }

        return builder.ToString();
    }
}
