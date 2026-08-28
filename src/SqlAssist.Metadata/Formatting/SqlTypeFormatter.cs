using System;
using System.Globalization;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 把 sys.columns／sys.parameters 的原始型別欄位格式化成 T-SQL 寫法。
/// </summary>
public static class SqlTypeFormatter
{
    /// <summary>float 的預設精確度；等於預設值時 SQL Server 不會顯示括號。</summary>
    private const byte DefaultFloatPrecision = 53;

    /// <summary>
    /// 格式化型別。
    /// </summary>
    /// <param name="typeName">sys.types.name。</param>
    /// <param name="maxLength">sys.columns.max_length，以位元組計；-1 代表 max。</param>
    /// <param name="precision">sys.columns.precision。</param>
    /// <param name="scale">sys.columns.scale。</param>
    public static string Format(string typeName, short maxLength, byte precision, byte scale)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            throw new ArgumentException("型別名稱不可為空。", nameof(typeName));
        }

        switch (typeName.ToLowerInvariant())
        {
            case "nchar":
            case "nvarchar":
                // Unicode 型別的 max_length 以位元組計，字元數是它的一半。
                return $"{typeName}({FormatLength(maxLength, halveLength: true)})";

            case "char":
            case "varchar":
            case "binary":
            case "varbinary":
                return $"{typeName}({FormatLength(maxLength, halveLength: false)})";

            case "decimal":
            case "numeric":
                return $"{typeName}({precision},{scale})";

            case "datetime2":
            case "time":
            case "datetimeoffset":
                return $"{typeName}({scale})";

            case "float":
                return precision == DefaultFloatPrecision
                    ? typeName
                    : $"{typeName}({precision})";

            default:
                return typeName;
        }
    }

    private static string FormatLength(short maxLength, bool halveLength)
    {
        if (maxLength < 0)
        {
            return "max";
        }

        var length = halveLength ? maxLength / 2 : maxLength;
        return length.ToString(CultureInfo.InvariantCulture);
    }
}
