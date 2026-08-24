using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata;

/// <summary>第一層中繼資料：物件清單與結構描述清單。</summary>
public sealed class SqlDatabaseSnapshot
{
    public static readonly SqlDatabaseSnapshot Empty = new(
        string.Empty,
        Array.Empty<SqlObjectInfo>(),
        Array.Empty<string>(),
        DateTimeOffset.MinValue);

    public SqlDatabaseSnapshot(
        string databaseName,
        IReadOnlyList<SqlObjectInfo> objects,
        IReadOnlyList<string> schemas,
        DateTimeOffset loadedAt)
    {
        DatabaseName = databaseName ?? string.Empty;
        Objects = objects ?? Array.Empty<SqlObjectInfo>();
        Schemas = schemas ?? Array.Empty<string>();
        LoadedAt = loadedAt;
    }

    public string DatabaseName { get; }

    public IReadOnlyList<SqlObjectInfo> Objects { get; }

    public IReadOnlyList<string> Schemas { get; }

    public DateTimeOffset LoadedAt { get; }

    public bool IsEmpty => Objects.Count == 0 && Schemas.Count == 0;
}
