using System;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>值要寫成給人看的文字時，用哪一種寫法。</summary>
public enum ResultGridTextStyle
{
    /// <summary>
    /// 保留 T-SQL 的寫法：字串帶引號與 <c>N</c> 前綴，二進位每 32 位元組換一行。
    /// </summary>
    /// <remarks>
    /// 給「看完一格之後多半要把它貼進一句 <c>WHERE</c>」的場合。
    /// </remarks>
    Literal,

    /// <summary>沒有引號、沒有前綴、不換行。給表格的一格用。</summary>
    Plain
}

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

        var text = Display(column, value, ResultGridTextStyle.Literal);

        if (TryText(value, out var raw))
        {
            return new ResultGridCellText(prefix + Count(raw!.Length, "個字元"), text, isNull: false);
        }

        if (TryBinary(value, out var bytes))
        {
            return new ResultGridCellText(prefix + Count(bytes!.Length, "個位元組"), text, isNull: false);
        }

        return new ResultGridCellText(prefix + value!.GetType().Name, text, isNull: false);
    }

    /// <summary>
    /// 值寫成給人看的文字。<b>所有「不是要拿去執行」的呈現都走這裡。</b>
    /// </summary>
    /// <remarks>
    /// 與 <see cref="SqlValueLiteral"/> 是兩個不同的問題，不是同一件事寫兩份：
    /// 那邊回答「貼進查詢裡要長什麼樣」，這邊回答「給人讀要長什麼樣」。
    /// 文字在這裡不帶引號也不跳脫，因為讀的人要看的是內容本身。
    ///
    /// 但兩者共用同一份型別判斷：日期的精確度、數值的格式化都從
    /// <see cref="SqlValueLiteral"/> 借過來，再視需要脫掉外層引號。各算一次的話，
    /// 儲存格視窗與 Markdown 表格會顯示不同的日期精確度，而那看起來像資料有問題。
    /// </remarks>
    public static string Display(ResultGridColumn column, object? value, ResultGridTextStyle style)
    {
        if (column is null)
        {
            throw new ArgumentNullException(nameof(column));
        }

        if (SqlValueLiteral.IsNullValue(value))
        {
            return string.Empty;
        }

        if (TryText(value, out var raw))
        {
            return raw!;
        }

        if (TryBinary(value, out var bytes))
        {
            return style == ResultGridTextStyle.Plain ? Hex(bytes!, wrap: false) : Hex(bytes!, wrap: true);
        }

        if (!SqlValueLiteral.TryFormat(value, column.ServerDataType, out var literal, out _))
        {
            return value!.ToString() ?? string.Empty;
        }

        return style == ResultGridTextStyle.Plain ? Unquote(literal) : literal;
    }

    /// <summary>脫掉字面值外層的引號與 <c>N</c> 前綴，並把 <c>''</c> 還原成一個引號。</summary>
    private static string Unquote(string literal)
    {
        var start = literal.Length > 1 && literal[0] == 'N' && literal[1] == '\'' ? 1 : 0;

        return literal.Length - start >= 2 && literal[start] == '\'' && literal[literal.Length - 1] == '\''
            ? literal.Substring(start + 1, literal.Length - start - 2).Replace("''", "'")
            : literal;
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
    private static string Hex(byte[] bytes, bool wrap)
    {
        const int PerLine = 32;
        var builder = new StringBuilder(2 + (bytes.Length * 2) + (bytes.Length / PerLine));
        builder.Append("0x");

        for (var index = 0; index < bytes.Length; index++)
        {
            if (wrap && index > 0 && index % PerLine == 0)
            {
                builder.AppendLine();
            }

            builder.Append(HexDigits[bytes[index] >> 4]).Append(HexDigits[bytes[index] & 0xF]);
        }

        return builder.ToString();
    }

    private const string HexDigits = "0123456789ABCDEF";
}
