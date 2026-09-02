using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 把一塊查詢結果寫成可以直接接在 <c>WHERE</c> 後面的條件。
/// </summary>
/// <remarks>
/// 產出的東西是一段<b>述詞</b>，不是完整的查詢：使用者要的是把它貼進手上那一句
/// SQL，而不是換一句新的。因此沒有 <c>SELECT</c>、沒有資料表名稱，也刻意不加
/// 別名——多資料表查詢裡要限定哪一個別名，只有使用者知道。
///
/// 兩件事決定了輸出的形狀，兩件都是「不處理就會產生一段執行得動而答案是錯的 SQL」：
///
/// 一、<b>SQL Server 沒有列值建構函式的 <c>IN</c></b>。
/// <c>(BranchId, CopyNo) IN ((1, N'A01'))</c> 在 PostgreSQL 與 MySQL 上成立，
/// 在 SQL Server 上是語法錯誤。複合鍵因此展開成 OR 條件——它貼上去就能跑，
/// 不必先替外層查詢想一個別名，這一點勝過 <c>EXISTS (VALUES ...)</c> 的寫法。
///
/// 二、<b><c>NULL</c> 不能用等號比</b>。<c>x IN (NULL)</c> 與 <c>x = NULL</c>
/// 兩者都恆為 UNKNOWN，於是使用者明明選了那一列，條件卻永遠比不到它——
/// 沒有錯誤訊息，只是結果少一列。所以 <c>NULL</c> 一律改寫成 <c>IS NULL</c>。
/// </remarks>
public static class SqlInPredicateScript
{
    /// <summary>單欄 <c>IN</c> 清單換行前的寬度上限。</summary>
    private const int WrapWidth = 96;

    /// <summary>產不出來時的第一句。</summary>
    private const string UnavailableHeadline = "無法從查詢結果產生 IN 條件。";

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
                "先在結果格線裡選出要當條件的那一欄（或幾欄），再執行一次這個命令。");
        }

        if (!ResultGridLiterals.TryFormatAll(table, out var literals, out var failure))
        {
            return ResultGridLiterals.Unavailable(
                UnavailableHeadline,
                failure,
                "把那一欄從選取範圍拿掉之後再試一次；其餘欄位都轉得出來。");
        }

        var names = new string[table.Columns.Count];

        for (var index = 0; index < names.Length; index++)
        {
            names[index] = SqlIdentifier.QuoteIfNeeded(table.ScriptColumnNames[index]);
        }

        var builder = new StringBuilder(ResultGridLiterals.EstimateLength(literals) + 256);
        ResultGridLiterals.AppendSourceComment(builder, table);

        if (names.Length == 1)
        {
            AppendSingleColumn(builder, names[0], Distinct(literals));
        }
        else
        {
            AppendCompositeKey(builder, names, Distinct(literals));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 去掉重複的資料列，保留第一次出現的順序。
    /// </summary>
    /// <remarks>
    /// 重複是常態而不是例外：使用者常常整欄選下來，而那一欄可能只有幾個相異值。
    /// 保留順序而不是排序，是因為那個順序是他在格線上看到的順序，
    /// 排過之後就對不回去了。
    /// </remarks>
    private static List<string[]> Distinct(string[][] literals)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string[]>(literals.Length);

        foreach (var row in literals)
        {
            // 分隔字元用單位分隔符，避免值本身含有的逗號讓兩組不同的資料列撞在一起。
            if (seen.Add(string.Join("\u001F", row)))
            {
                result.Add(row);
            }
        }

        return result;
    }

    private static void AppendSingleColumn(StringBuilder builder, string name, List<string[]> rows)
    {
        var values = new List<string>(rows.Count);
        var hasNull = false;

        foreach (var row in rows)
        {
            if (string.Equals(row[0], SqlValueLiteral.Null, StringComparison.Ordinal))
            {
                hasNull = true;
            }
            else
            {
                values.Add(row[0]);
            }
        }

        // 整欄都是 NULL 的話沒有 IN 清單可寫，只剩下 IS NULL 那一半。
        if (values.Count == 0)
        {
            builder.Append(name).AppendLine(" IS NULL");
            return;
        }

        if (hasNull)
        {
            builder.AppendLine("-- 選取範圍裡有 NULL；IN 清單比不到 NULL，另外用 OR 補上。");
            builder.Append('(');
        }

        builder.Append(name).Append(" IN (");

        var column = name.Length + 5;

        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
                column += 2;
            }

            if (column > WrapWidth)
            {
                builder.AppendLine().Append("    ");
                column = 4;
            }

            builder.Append(values[index]);
            column += values[index].Length;
        }

        builder.Append(')');

        if (hasNull)
        {
            builder.Append(" OR ").Append(name).Append(" IS NULL)");
        }

        builder.AppendLine();
    }

    /// <remarks>
    /// 外面一定包一層括號。這段條件多半接在既有的 <c>WHERE</c> 後面，
    /// 而少了括號的話 <c>WHERE Branch = 1 AND A = 1 OR A = 2</c> 會因為
    /// <c>AND</c> 的優先權比 <c>OR</c> 高而換一個意思——查得出結果，只是不對。
    /// </remarks>
    private static void AppendCompositeKey(StringBuilder builder, string[] names, List<string[]> rows)
    {
        builder.AppendLine("-- SQL Server 不接受 (a, b) IN ((1, N'x')) 這種列值寫法，複合鍵改寫成 OR 條件。");
        builder.AppendLine("-- 欄名沒有加別名；多資料表的查詢裡請自行限定。");
        builder.AppendLine("(");

        for (var index = 0; index < rows.Count; index++)
        {
            builder.Append(index == 0 ? "       " : "    OR ").Append('(');

            for (var column = 0; column < names.Length; column++)
            {
                if (column > 0)
                {
                    builder.Append(" AND ");
                }

                var literal = rows[index][column];

                builder.Append(names[column]).Append(
                    string.Equals(literal, SqlValueLiteral.Null, StringComparison.Ordinal)
                        ? " IS NULL"
                        : " = " + literal);
            }

            builder.AppendLine(")");
        }

        builder.AppendLine(")");
    }
}
