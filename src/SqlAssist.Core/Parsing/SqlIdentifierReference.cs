namespace SqlAssist.Core.Parsing;

/// <summary>
/// 文字中某個位置所指的識別字，例如 <c>Lib_Reader</c> 或 <c>[dbo].[Lib_Reader]</c>。
/// </summary>
public sealed class SqlIdentifierReference
{
    public SqlIdentifierReference(string name, SqlObjectPath? path, int start, int length)
    {
        Name = name;
        Path = path;
        Start = start;
        Length = length;
    }

    /// <summary>去掉括號後的識別字本體。</summary>
    /// <remarks>
    /// 刻意不從 <see cref="Path"/> 推導：游標底下是哪一個識別字，就算整串名稱
    /// 段數過多而解析不出路徑，也仍然是確定的。推導的話那個情形會連名稱都拿不到，
    /// 而滑鼠停留提示至少還說得出「你停在這個字上」。
    /// </remarks>
    public string Name { get; }

    /// <summary>
    /// 連同前方限定字算出的完整位置；段數超過 T-SQL 上限時為 null。
    /// </summary>
    public SqlObjectPath? Path { get; }

    /// <summary>點號前方的限定詞，通常是結構描述或別名；沒有限定詞時為 null。</summary>
    public string? Qualifier => Path?.SchemaName;

    /// <summary>這個名稱在目前這條連線上查得到嗎。</summary>
    /// <remarks>
    /// 解析不出路徑時算「不是」：那種名稱下游一樣查不到，而猜一個出來
    /// 會讓 F12 跳到一個使用者沒有指名的物件。
    /// </remarks>
    public bool IsLocal => Path is not null && Path.IsLocal;

    /// <summary>整個參考在原文中的起點，含限定詞與括號。</summary>
    public int Start { get; }

    /// <summary>整個參考在原文中的長度，含限定詞與括號。</summary>
    public int Length { get; }

    public int End => Start + Length;

    public override string ToString()
    {
        return Path?.ToString() ?? Name;
    }
}
