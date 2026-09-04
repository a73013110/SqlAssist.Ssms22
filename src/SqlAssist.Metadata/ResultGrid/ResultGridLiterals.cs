using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 把一整塊結果先轉成字面值，全部成功才交出去；任何一格轉不出來就是失敗。
/// </summary>
/// <remarks>
/// 每個產指令碼的命令都從這裡開始，理由有兩個。
///
/// 第一個是正確性：CLAUDE.md 那條「禁止在資料不齊時輸出半份可以執行的東西」在這裡
/// 的形狀是「一欄是空間型別，其他 177 欄都好」。少那一欄的 <c>INSERT</c> 執行得動，
/// 而使用者拿它 debug 的時候不會發現資料少了一塊。先全部轉完再決定，
/// 就不會出現寫到一半才發現不行的輸出。
///
/// 第二個是效能：轉換的結果同時餵給長度估算與字串組裝，只轉一次。實測的結果
/// 有 178 欄，選滿 100 列就是 17800 格；轉兩次的代價是看得出來的頓一下。
/// </remarks>
internal static class ResultGridLiterals
{
    /// <summary>估算字面值總長度時，每一格額外算進去的分隔符與縮排。</summary>
    private const int PerCellOverhead = 4;

    /// <summary>
    /// 把整塊資料轉成字面值。
    /// </summary>
    /// <param name="literals">成功時是與 <see cref="ResultGridTable.Rows"/> 同形狀的字面值。</param>
    /// <param name="failure">失敗時是要寫進註解的原因；成功時是空字串。</param>
    public static bool TryFormatAll(
        ResultGridTable table,
        out string[][] literals,
        out string failure)
    {
        literals = Array.Empty<string[]>();
        failure = string.Empty;

        var columns = table.Columns;
        var rows = table.Rows;
        var result = new string[rows.Count][];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var values = rows[rowIndex];
            var line = new string[columns.Count];

            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                var value = columnIndex < values.Length ? values[columnIndex] : null;

                if (!SqlValueLiteral.TryFormat(value, column.ServerDataType, out var literal, out var reason))
                {
                    // 位置要講清楚：178 欄的結果裡只說「有一欄轉不出來」等於沒說。
                    // 列號用格線上看得到的 1 起算，不是內部索引。
                    failure = string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 列的「{1}」欄轉不成 T-SQL 字面值：{2}。",
                        rowIndex + 1,
                        table.ScriptColumnNames[columnIndex],
                        reason);
                    return false;
                }

                line[columnIndex] = literal;
            }

            result[rowIndex] = line;
        }

        literals = result;
        return true;
    }

    /// <summary>字面值加起來的字元數，用來預先配置 <see cref="StringBuilder"/>。</summary>
    public static int EstimateLength(string[][] literals)
    {
        var total = 0;

        foreach (var row in literals)
        {
            foreach (var literal in row)
            {
                total += literal.Length + PerCellOverhead;
            }
        }

        return total;
    }

    /// <summary>
    /// 產不出來時的輸出：整段都是註解，寫明缺什麼與該怎麼辦。
    /// </summary>
    /// <remarks>
    /// 整段註解而不是空字串，也不是一個對話框：使用者按了命令，結果會出現在他
    /// 預期的地方，只是內容說明了為什麼沒有東西可執行。這與
    /// <c>SqlObjectStructure</c> 缺定義時的做法是同一份判斷，格式也刻意寫成同一種。
    /// </remarks>
    /// <param name="headline">完整的第一句，含句號。</param>
    public static string Unavailable(string headline, params string[] reasons)
    {
        var builder = new StringBuilder();
        builder.Append("-- ").AppendLine(headline);

        foreach (var reason in reasons)
        {
            if (!string.IsNullOrEmpty(reason))
            {
                builder.Append("-- ").AppendLine(reason);
            }
        }

        return builder.ToString();
    }

    /// <summary>指令碼開頭那一行：這塊資料是從哪裡來的、多大。</summary>
    public static void AppendSourceComment(StringBuilder builder, ResultGridTable table)
    {
        builder.Append("-- 由 SqlAssist 從查詢結果產生：")
            .Append(table.Columns.Count.ToString(CultureInfo.InvariantCulture)).Append(" 欄 × ")
            .Append(table.Rows.Count.ToString(CultureInfo.InvariantCulture)).Append(" 列")
            .Append(table.IsWholeResult ? "（整份結果）" : "（選取範圍）")
            .AppendLine("。");
    }

    /// <summary>把識別字清單寫成 <c>[a], [b], [c]</c>。</summary>
    public static void AppendQuotedNames(StringBuilder builder, IReadOnlyList<string> names)
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(SqlIdentifier.QuoteIfNeeded(names[index]));
        }
    }
}
