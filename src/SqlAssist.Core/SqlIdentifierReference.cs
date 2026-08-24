namespace SqlAssist.Core;

/// <summary>
/// 文字中某個位置所指的識別字，例如 <c>Lib_Reader</c> 或 <c>[dbo].[Lib_Reader]</c>。
/// </summary>
public sealed class SqlIdentifierReference
{
    public SqlIdentifierReference(string name, string? qualifier, int start, int length)
    {
        Name = name;
        Qualifier = qualifier;
        Start = start;
        Length = length;
    }

    /// <summary>去掉括號後的識別字本體。</summary>
    public string Name { get; }

    /// <summary>點號前方的限定詞，通常是結構描述或別名；沒有限定詞時為 null。</summary>
    public string? Qualifier { get; }

    /// <summary>整個參考在原文中的起點，含限定詞與括號。</summary>
    public int Start { get; }

    /// <summary>整個參考在原文中的長度，含限定詞與括號。</summary>
    public int Length { get; }

    public int End => Start + Length;

    public override string ToString()
    {
        return Qualifier is null ? Name : $"{Qualifier}.{Name}";
    }
}
