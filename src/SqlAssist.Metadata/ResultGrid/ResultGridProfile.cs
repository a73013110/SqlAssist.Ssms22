using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Globalization;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>一欄的統計摘要。</summary>
public sealed class ResultGridColumnProfile
{
    internal ResultGridColumnProfile(
        string name,
        string dataType,
        int rowCount,
        int nullCount,
        int emptyTextCount,
        int distinctCount,
        string minimum,
        string maximum,
        string textLength)
    {
        Name = name;
        DataType = dataType;
        RowCount = rowCount;
        NullCount = nullCount;
        EmptyTextCount = emptyTextCount;
        DistinctCount = distinctCount;
        Minimum = minimum;
        Maximum = maximum;
        TextLength = textLength;
    }

    public string Name { get; }

    /// <summary>伺服器回報的型別；問不出來時是 <c>?</c>。</summary>
    public string DataType { get; }

    public int RowCount { get; }

    public int NullCount { get; }

    /// <summary>長度為零的字串有幾個；<b>不含</b> <c>NULL</c>。</summary>
    public int EmptyTextCount { get; }

    /// <summary>相異值有幾個，<c>NULL</c> 算一個。</summary>
    public int DistinctCount { get; }

    /// <summary>最小值，寫成 T-SQL 字面值；比不出大小時是空字串。</summary>
    public string Minimum { get; }

    /// <summary>最大值，寫成 T-SQL 字面值；比不出大小時是空字串。</summary>
    public string Maximum { get; }

    /// <summary>文字欄位的字元數範圍，例如 <c>3–20</c>；非文字欄位是空字串。</summary>
    public string TextLength { get; }

    /// <summary>整欄都是 <c>NULL</c>。</summary>
    public bool IsAllNull => RowCount > 0 && NullCount == RowCount;

    /// <summary>整欄只有一種值（<c>NULL</c> 也算一種）。</summary>
    public bool IsConstant => RowCount > 1 && DistinctCount == 1;
}

/// <summary>
/// 把一塊查詢結果整理成每一欄的統計摘要。
/// </summary>
/// <remarks>
/// 這個功能在寬表上的價值遠高於窄表。實測的查詢有 178 欄，捲到最右邊要拖十幾次，
/// 而真正想知道的往往只是「哪幾欄整欄是 NULL、哪幾欄其實從頭到尾只有一個值」
/// ——那兩件事看資料看不出來，看摘要一眼就有。
///
/// 不重新查詢資料庫：格線的資料本來就在記憶體裡（<c>StoredAllData</c>），
/// 而且統計的對象刻意就是「使用者眼前這一份」，不是資料表的全貌。
/// 對後者下一句 <c>GROUP BY</c> 比較快，也比較準。
///
/// 最小與最大寫成 T-SQL 字面值而不是顯示字串，理由是它們的下一步幾乎一定是被貼進
/// 一句 <c>WHERE</c>。格式化走 <see cref="SqlValueLiteral"/>，與其他命令同一份。
/// </remarks>
public static class ResultGridProfile
{
    /// <summary>最小值與最大值顯示到幾個字元。</summary>
    private const int MaxLiteralLength = 60;

    public static IReadOnlyList<ResultGridColumnProfile> Build(ResultGridTable table)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        var names = table.ScriptColumnNames;
        var profiles = new ResultGridColumnProfile[table.Columns.Count];

        for (var column = 0; column < table.Columns.Count; column++)
        {
            profiles[column] = BuildColumn(table, column, names[column]);
        }

        return profiles;
    }

    private static ResultGridColumnProfile BuildColumn(ResultGridTable table, int column, string name)
    {
        var descriptor = table.Columns[column];
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var nulls = 0;
        var empties = 0;
        var minimumLength = int.MaxValue;
        var maximumLength = 0;
        var hasText = false;

        object? minimum = null;
        object? maximum = null;
        var comparable = true;

        foreach (var row in table.Rows)
        {
            var value = column < row.Length ? row[column] : null;

            if (SqlValueLiteral.IsNullValue(value))
            {
                nulls++;
                distinct.Add(SqlValueLiteral.Null);
                continue;
            }

            distinct.Add(DistinctKey(value, descriptor.ServerDataType));

            if (TryTextLength(value, out var length))
            {
                hasText = true;
                empties += length == 0 ? 1 : 0;
                minimumLength = Math.Min(minimumLength, length);
                maximumLength = Math.Max(maximumLength, length);
            }

            if (!comparable)
            {
                continue;
            }

            if (minimum is null)
            {
                minimum = value;
                maximum = value;
            }
            else if (TryCompare(value!, minimum, out var toMinimum) && TryCompare(value!, maximum!, out var toMaximum))
            {
                minimum = toMinimum < 0 ? value : minimum;
                maximum = toMaximum > 0 ? value : maximum;
            }
            else
            {
                // 同一欄裡出現比不出大小的值。整欄的最小最大一起放棄而不是
                // 只算得出來的那些——後者會給出一個看起來正常、實際上少算了
                // 一部分資料的範圍。
                comparable = false;
                minimum = null;
                maximum = null;
            }
        }

        return new ResultGridColumnProfile(
            name,
            descriptor.ServerDataType.Length == 0 ? "?" : descriptor.ServerDataType,
            table.Rows.Count,
            nulls,
            empties,
            distinct.Count,
            Literal(minimum, descriptor.ServerDataType),
            Literal(maximum, descriptor.ServerDataType),
            hasText && minimumLength <= maximumLength
                ? minimumLength == maximumLength
                    ? minimumLength.ToString(CultureInfo.InvariantCulture)
                    : minimumLength.ToString(CultureInfo.InvariantCulture)
                        + "–" + maximumLength.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
    }

    /// <remarks>
    /// 用字面值當相異值的鍵，而不是把值丟進 <c>HashSet&lt;object&gt;</c>：
    /// <c>byte[]</c> 的預設相等性是參考比較，兩個內容相同的位元組陣列會被算成
    /// 兩個相異值，而那個錯誤沒有任何徵兆。
    /// </remarks>
    private static string DistinctKey(object? value, string serverDataType) =>
        SqlValueLiteral.TryFormat(value, serverDataType, out var literal, out _)
            ? literal
            : value?.ToString() ?? string.Empty;

    private static string Literal(object? value, string serverDataType)
    {
        if (value is null || !SqlValueLiteral.TryFormat(value, serverDataType, out var literal, out _))
        {
            return string.Empty;
        }

        return literal.Length <= MaxLiteralLength
            ? literal
            : literal.Substring(0, MaxLiteralLength) + "…";
    }

    private static bool TryTextLength(object? value, out int length)
    {
        switch (value)
        {
            case string text:
                length = text.Length;
                return true;
            case SqlString text:
                length = text.Value.Length;
                return true;
            case SqlChars chars:
                length = (int)chars.Length;
                return true;
            case char[] chars:
                length = chars.Length;
                return true;
            default:
                length = 0;
                return false;
        }
    }

    private static bool TryCompare(object left, object? right, out int result)
    {
        try
        {
            if (left is IComparable comparable)
            {
                result = comparable.CompareTo(right);
                return true;
            }
        }
        catch (Exception)
        {
            // 型別對不上時 CompareTo 會擲例外。這在同一欄裡不該發生，
            // 但認不得的值本來就是這個功能要容忍的情形。
        }

        result = 0;
        return false;
    }
}
