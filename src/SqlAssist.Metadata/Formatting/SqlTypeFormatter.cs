using System;
using System.Globalization;
using System.Text;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 把 sys.columns／sys.parameters 的原始型別欄位格式化成 T-SQL 寫法。
/// </summary>
/// <remarks>
/// 排版由三個布林參數帶進來，而不是收一份 <c>SqlScriptOptions</c>：
/// 這一層是給滑鼠停留提示、參數清單與指令碼共用的，其中只有指令碼那一條
/// 有選項可問。收整份選項等於讓另外兩條路也得先組一份出來。
/// </remarks>
public static class SqlTypeFormatter
{
    /// <summary>float 的預設精確度；等於預設值時 SQL Server 不會顯示括號。</summary>
    private const byte DefaultFloatPrecision = 53;

    /// <summary>
    /// 格式化型別，不加方括號也不留空格——建議清單與提示共用的緊湊寫法。
    /// </summary>
    /// <param name="typeName">sys.types.name。</param>
    /// <param name="maxLength">sys.columns.max_length，以位元組計；-1 代表 max。</param>
    /// <param name="precision">sys.columns.precision。</param>
    /// <param name="scale">sys.columns.scale。</param>
    public static string Format(string typeName, short maxLength, byte precision, byte scale) =>
        Format(typeName, maxLength, precision, scale, false, false, false);

    /// <param name="quoteTypeName">型別名稱加方括號（<c>[nvarchar]</c>）。</param>
    /// <param name="spaceBeforeArguments">型別名稱與括號之間留空格（<c>[nvarchar] (200)</c>）。</param>
    /// <param name="spaceAfterComma">括號內的逗號後面留空格（<c>[decimal] (18, 2)</c>）。</param>
    public static string Format(
        string typeName,
        short maxLength,
        byte precision,
        byte scale,
        bool quoteTypeName,
        bool spaceBeforeArguments,
        bool spaceAfterComma)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            throw new ArgumentException("型別名稱不可為空。", nameof(typeName));
        }

        var arguments = BuildArguments(typeName, maxLength, precision, scale, spaceAfterComma);
        var builder = new StringBuilder(typeName.Length + 16);
        builder.Append(quoteTypeName ? SqlIdentifier.Quote(typeName) : typeName);

        if (arguments.Length == 0)
        {
            return builder.ToString();
        }

        if (spaceBeforeArguments)
        {
            builder.Append(' ');
        }

        return builder.Append('(').Append(arguments).Append(')').ToString();
    }

    /// <summary>括號裡的內容；這個型別不帶引數時回傳空字串。</summary>
    private static string BuildArguments(
        string typeName,
        short maxLength,
        byte precision,
        byte scale,
        bool spaceAfterComma)
    {
        var separator = spaceAfterComma ? ", " : ",";

        switch (typeName.ToLowerInvariant())
        {
            case "nchar":
            case "nvarchar":
                // Unicode 型別的 max_length 以位元組計，字元數是它的一半。
                return FormatLength(maxLength, halveLength: true);

            case "char":
            case "varchar":
            case "binary":
            case "varbinary":
                return FormatLength(maxLength, halveLength: false);

            case "decimal":
            case "numeric":
                return Number(precision) + separator + Number(scale);

            case "datetime2":
            case "time":
            case "datetimeoffset":
                return Number(scale);

            case "float":
                return precision == DefaultFloatPrecision ? string.Empty : Number(precision);

            default:
                return string.Empty;
        }
    }

    private static string FormatLength(short maxLength, bool halveLength)
    {
        if (maxLength < 0)
        {
            return "max";
        }

        return Number(halveLength ? maxLength / 2 : maxLength);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
