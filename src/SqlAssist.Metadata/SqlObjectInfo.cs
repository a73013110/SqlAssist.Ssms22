using System;

namespace SqlAssist.Metadata;

/// <summary>
/// 資料庫物件的輕量描述。第一層載入只取這些欄位，不包含欄位清單與定義本文，
/// 因此即使資料庫有數千個物件也能快速取回並常駐快取。
/// </summary>
public sealed class SqlObjectInfo
{
    public SqlObjectInfo(int objectId, string schemaName, string name, SqlObjectKind kind)
    {
        if (string.IsNullOrEmpty(schemaName))
        {
            throw new ArgumentException("結構描述名稱不可為空。", nameof(schemaName));
        }

        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("物件名稱不可為空。", nameof(name));
        }

        ObjectId = objectId;
        SchemaName = schemaName;
        Name = name;
        Kind = kind;
    }

    public int ObjectId { get; }

    public string SchemaName { get; }

    public string Name { get; }

    public SqlObjectKind Kind { get; }

    /// <summary>加上方括號的完整名稱，例如 <c>[dbo].[Lib_Reader]</c>。</summary>
    public string QualifiedName =>
        $"{SqlIdentifier.Quote(SchemaName)}.{SqlIdentifier.Quote(Name)}";

    public override string ToString() => QualifiedName;
}
