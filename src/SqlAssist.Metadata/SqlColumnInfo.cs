using System;
using System.Text;

namespace SqlAssist.Metadata;

/// <summary>資料表或檢視的單一欄位。</summary>
public sealed class SqlColumnInfo
{
    public SqlColumnInfo(
        int ordinal,
        string name,
        string dataType,
        bool isNullable,
        bool isIdentity = false,
        bool isComputed = false,
        bool isPrimaryKey = false,
        string? defaultDefinition = null,
        string? computedDefinition = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("欄位名稱不可為空。", nameof(name));
        }

        Ordinal = ordinal;
        Name = name;
        DataType = dataType ?? string.Empty;
        IsNullable = isNullable;
        IsIdentity = isIdentity;
        IsComputed = isComputed;
        IsPrimaryKey = isPrimaryKey;
        DefaultDefinition = defaultDefinition;
        ComputedDefinition = computedDefinition;
    }

    public int Ordinal { get; }

    public string Name { get; }

    public string DataType { get; }

    public bool IsNullable { get; }

    public bool IsIdentity { get; }

    public bool IsComputed { get; }

    public bool IsPrimaryKey { get; }

    public string? DefaultDefinition { get; }

    /// <summary>計算欄位的運算式；一般欄位為 null。</summary>
    public string? ComputedDefinition { get; }

    /// <summary>組出接近 CREATE TABLE 寫法的單行描述。</summary>
    public string ToScriptLine()
    {
        var builder = new StringBuilder();
        builder.Append(SqlIdentifier.Quote(Name));
        builder.Append(' ');
        builder.Append(DataType);

        if (IsIdentity)
        {
            builder.Append(" IDENTITY");
        }

        builder.Append(IsNullable ? " NULL" : " NOT NULL");

        if (!string.IsNullOrEmpty(DefaultDefinition))
        {
            builder.Append(" DEFAULT ").Append(DefaultDefinition);
        }

        if (IsPrimaryKey)
        {
            builder.Append(" -- PK");
        }

        return builder.ToString();
    }

    public override string ToString() => ToScriptLine();
}
