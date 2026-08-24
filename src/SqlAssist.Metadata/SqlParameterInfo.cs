using System;

namespace SqlAssist.Metadata;

/// <summary>預存程序或函式的單一參數。</summary>
public sealed class SqlParameterInfo
{
    public SqlParameterInfo(int ordinal, string name, string dataType, bool isOutput)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("參數名稱不可為空。", nameof(name));
        }

        Ordinal = ordinal;
        Name = name;
        DataType = dataType ?? string.Empty;
        IsOutput = isOutput;
    }

    public int Ordinal { get; }

    /// <summary>含 <c>@</c> 前綴的參數名稱；純量函式的傳回值其名稱為空字串。</summary>
    public string Name { get; }

    public string DataType { get; }

    public bool IsOutput { get; }

    public string ToScriptLine()
    {
        return IsOutput
            ? $"{Name} {DataType} OUTPUT"
            : $"{Name} {DataType}";
    }

    public override string ToString() => ToScriptLine();
}
