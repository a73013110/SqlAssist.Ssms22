namespace SqlAssist.Core.Parsing;

/// <summary>敘述中出現的一個資料來源，例如 <c>FROM dbo.Lib_Reader AS u</c>。</summary>
public sealed class SqlTableReference
{
    public SqlTableReference(
        string? schemaName,
        string objectName,
        string? alias,
        bool isDerived,
        int start,
        int end)
    {
        SchemaName = schemaName;
        ObjectName = objectName;
        Alias = alias;
        IsDerived = isDerived;
        Start = start;
        End = end;
    }

    /// <summary>結構描述限定字，沒寫時為 null。</summary>
    public string? SchemaName { get; }

    /// <summary>物件名稱；衍生資料表沒有名稱時為空字串。</summary>
    public string ObjectName { get; }

    /// <summary>別名，沒寫時為 null。</summary>
    public string? Alias { get; }

    /// <summary>是否為衍生資料表或資料表值建構式，這種來源查不到中繼資料。</summary>
    public bool IsDerived { get; }

    public int Start { get; }

    public int End { get; }

    /// <summary>在敘述中可用來限定欄位的名稱：有別名就是別名，否則是物件名稱。</summary>
    public string EffectiveName => string.IsNullOrEmpty(Alias) ? ObjectName : Alias!;

    public override string ToString()
    {
        var name = string.IsNullOrEmpty(SchemaName) ? ObjectName : $"{SchemaName}.{ObjectName}";
        return string.IsNullOrEmpty(Alias) ? name : $"{name} AS {Alias}";
    }
}
