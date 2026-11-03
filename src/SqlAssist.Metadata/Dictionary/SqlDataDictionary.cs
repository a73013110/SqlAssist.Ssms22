using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>資料字典收的物件種類：說明掛得上去、也看得到資料行的那兩種。</summary>
public enum SqlDictionaryObjectKind
{
    Table,

    View
}

/// <summary>資料字典裡的一個資料行：名稱、排好的型別與說明。</summary>
/// <remarks>
/// 與 <c>Model/SqlColumnInfo</c> 分開：那一份是第二層「選了一個物件之後」的完整明細（預設值、
/// 計算式、識別值、指令碼要的每一個欄位），這裡一次載入整個資料庫，每多一欄就是每一個資料行
/// 多付一份。字典只要看得出「是什麼、叫什麼、寫了什麼說明」。
/// </remarks>
public sealed class SqlDictionaryColumn
{
    [Localizable(false)]
    public SqlDictionaryColumn(
        int columnId,
        string name,
        string dataType,
        bool isNullable,
        bool isPrimaryKey = false,
        bool isIdentity = false,
        bool isComputed = false,
        string? description = null)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("資料行名稱不可為空。", nameof(name));
        if (string.IsNullOrEmpty(dataType)) throw new ArgumentException("型別不可為空。", nameof(dataType));

        ColumnId = columnId;
        Name = name;
        DataType = dataType;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        IsIdentity = isIdentity;
        IsComputed = isComputed;
        Description = SqlDescriptionValue.Normalize(description);
    }

    public int ColumnId { get; }

    public string Name { get; }

    /// <summary><see cref="Formatting.SqlTypeFormatter"/> 的緊湊寫法（<c>nvarchar(50)</c>）。</summary>
    public string DataType { get; }

    public bool IsNullable { get; }

    public bool IsPrimaryKey { get; }

    public bool IsIdentity { get; }

    public bool IsComputed { get; }

    /// <summary>載入當時的 <c>MS_Description</c>；沒有時是空字串，見 <see cref="SqlDescriptionValue"/>。</summary>
    public string Description { get; }

    public override string ToString() => Name + " " + DataType;
}

/// <summary>資料字典裡的一張資料表或一個檢視，連同它的資料行。</summary>
public sealed class SqlDictionaryObject
{
    [Localizable(false)]
    public SqlDictionaryObject(
        int objectId,
        string schemaName,
        string name,
        SqlDictionaryObjectKind kind,
        string? description,
        IReadOnlyList<SqlDictionaryColumn> columns)
    {
        if (string.IsNullOrEmpty(schemaName)) throw new ArgumentException("結構描述不可為空。", nameof(schemaName));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("物件名稱不可為空。", nameof(name));

        ObjectId = objectId;
        SchemaName = schemaName;
        Name = name;
        Kind = kind;
        Description = SqlDescriptionValue.Normalize(description);
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Target = new SqlDescriptionTarget(kind, schemaName, name);
    }

    /// <summary>只在這個資料庫裡唯一；寫回之後讓中繼資料目錄清掉這一個物件時用。</summary>
    public int ObjectId { get; }

    public string SchemaName { get; }

    public string Name { get; }

    public SqlDictionaryObjectKind Kind { get; }

    /// <summary>物件本身的 <c>MS_Description</c>（<c>minor_id = 0</c>）；沒有時是空字串。</summary>
    public string Description { get; }

    /// <summary>照 <c>column_id</c> 排好的資料行。</summary>
    public IReadOnlyList<SqlDictionaryColumn> Columns { get; }

    /// <summary>物件本身那一筆說明的寫入目標。</summary>
    public SqlDescriptionTarget Target { get; }

    /// <summary><c>schema.name</c>，不加方括號；清單與複製用的顯示名稱。</summary>
    public string DisplayName => SchemaName + "." + Name;

    /// <summary>某一個資料行那一筆說明的寫入目標。</summary>
    public SqlDescriptionTarget TargetOf(SqlDictionaryColumn column) =>
        new(Kind, SchemaName, Name, (column ?? throw new ArgumentNullException(nameof(column))).Name);

    public override string ToString() => DisplayName;
}

/// <summary>一個資料庫的資料字典：每一張使用者資料表與檢視，連同資料行與說明。</summary>
/// <remarks>
/// 一份快照，不跟著資料庫變：載入之後別人改了說明，這裡要等使用者重新整理才看得到；
/// 寫回前的併發檢查（<see cref="SqlDescriptionWriter"/>）就是為了這個落差。
/// </remarks>
public sealed class SqlDataDictionary
{
    public SqlDataDictionary(string databaseName, IReadOnlyList<SqlDictionaryObject> objects)
    {
        DatabaseName = databaseName ?? string.Empty;
        Objects = objects ?? throw new ArgumentNullException(nameof(objects));

        var columns = 0;
        foreach (var item in objects) columns += item.Columns.Count;
        ColumnCount = columns;
    }

    public string DatabaseName { get; }

    /// <summary>照結構描述、名稱排好的物件。</summary>
    public IReadOnlyList<SqlDictionaryObject> Objects { get; }

    public int ColumnCount { get; }
}
