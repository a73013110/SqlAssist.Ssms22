using System;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 一格的完整內容，以及一句描述它有多大。
/// </summary>
/// <remarks>
/// 結果格線一列只有一行高，而 <c>NumberOfCharsToShow</c> 是 65535——一段
/// <c>nvarchar(max)</c> 的 XML 在格線上只看得到開頭那幾十個字。這個型別回答的就是
/// 「那一格裡面到底是什麼」。
///
/// 值的呈現分兩種，而不是一律套字面值：文字與 XML 直接給原文，因為看的人要讀的是
/// 內容本身，多一層引號跳脫只會擋路；其他型別給字面值，因為那時候看的人多半是要
/// 把它貼進一句 <c>WHERE</c>。二進位一律十六進位，那是唯一看得懂又貼得回去的寫法。
///
/// 長度單位跟著型別走：文字算字元、二進位算位元組。混成同一個數字的話，
/// 「這一欄會不會被截斷」就答不出來了——而那正是最常來問這個視窗的問題。
/// </remarks>
public sealed class ResultGridCellText
{
    private ResultGridCellText(string headline, string text, bool isNull)
    {
        Headline = headline;
        Text = text;
        IsNull = isNull;
    }

    /// <summary>一句話說明這是哪一欄、什麼型別、多大。</summary>
    public string Headline { get; }

    /// <summary>完整內容；<c>NULL</c> 時是空字串。</summary>
    public string Text { get; }

    public bool IsNull { get; }

    public static ResultGridCellText Create(ResultGridColumn column, object? value)
    {
        if (column is null)
        {
            throw new ArgumentNullException(nameof(column));
        }

        var name = column.Name.Length == 0 ? "（沒有資料行名稱）" : column.Name;
        var type = column.ServerDataType.Length == 0 ? "?" : column.ServerDataType;
        var prefix = name + "（" + type + "）· ";

        if (SqlValueLiteral.IsNullValue(value))
        {
            return new ResultGridCellText(prefix + "NULL", string.Empty, isNull: true);
        }

        if (TryText(value, out var text))
        {
            return new ResultGridCellText(
                prefix + Count(text!.Length, "個字元"),
                text,
                isNull: false);
        }

        if (TryBinary(value, out var bytes))
        {
            return new ResultGridCellText(
                prefix + Count(bytes!.Length, "個位元組"),
                Hex(bytes),
                isNull: false);
        }

        var literal = SqlValueLiteral.TryFormat(value, column.ServerDataType, out var formatted, out _)
            ? formatted
            : value!.ToString() ?? string.Empty;

        return new ResultGridCellText(prefix + value!.GetType().Name, literal, isNull: false);
    }

    private static string Count(int value, string unit) =>
        value.ToString("N0", CultureInfo.InvariantCulture) + " " + unit;

    private static bool TryText(object? value, out string? text)
    {
        switch (value)
        {
            case string plain:
                text = plain;
                return true;
            case SqlString sql:
                text = sql.Value;
                return true;
            case SqlXml xml:
                text = xml.Value;
                return true;
            case SqlChars chars:
                text = new string(chars.Value);
                return true;
            case char[] chars:
                text = new string(chars);
                return true;
            default:
                text = null;
                return false;
        }
    }

    private static bool TryBinary(object? value, out byte[]? bytes)
    {
        switch (value)
        {
            case byte[] plain:
                bytes = plain;
                return true;
            case SqlBinary binary:
                bytes = binary.Value;
                return true;
            case SqlBytes sql:
                bytes = sql.Value;
                return true;
            default:
                bytes = null;
                return false;
        }
    }

    /// <remarks>
    /// 每 32 個位元組換一行。不換行的話，一段 8 KB 的 <c>varbinary</c> 會變成一條
    /// 一萬六千字元的單行——捲得到頭，但看不出任何結構。
    /// </remarks>
    private static string Hex(byte[] bytes)
    {
        const int PerLine = 32;
        var builder = new StringBuilder(2 + (bytes.Length * 2) + (bytes.Length / PerLine));
        builder.Append("0x");

        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0 && index % PerLine == 0)
            {
                builder.AppendLine();
            }

            builder.Append(HexDigits[bytes[index] >> 4]).Append(HexDigits[bytes[index] & 0xF]);
        }

        return builder.ToString();
    }

    private const string HexDigits = "0123456789ABCDEF";
}
