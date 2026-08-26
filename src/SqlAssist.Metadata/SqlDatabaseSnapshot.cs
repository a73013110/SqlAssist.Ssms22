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
        Array.Empty<string>(),
        DateTimeOffset.MinValue);

    public SqlDatabaseSnapshot(
        string databaseName,
        IReadOnlyList<SqlObjectInfo> objects,
        IReadOnlyList<string> schemas,
        IReadOnlyList<string> databases,
        DateTimeOffset loadedAt)
    {
        DatabaseName = databaseName ?? string.Empty;
        Objects = objects ?? Array.Empty<SqlObjectInfo>();
        Schemas = schemas ?? Array.Empty<string>();
        Databases = databases ?? Array.Empty<string>();
        LoadedAt = loadedAt;
    }

    public string DatabaseName { get; }

    public IReadOnlyList<SqlObjectInfo> Objects { get; }

    public IReadOnlyList<string> Schemas { get; }

    /// <summary>
    /// 這一台伺服器上的資料庫，供 <c>USE</c> 之後的建議使用。
    /// </summary>
    /// <remarks>
    /// 內容是伺服器層級的，卻放在資料庫層級的快照裡：同一台伺服器的不同資料庫
    /// 各自快取一份相同的清單。換來的是不必為了一份幾十列的名稱清單多養一層
    /// 伺服器快取與它的失效規則。
    /// </remarks>
    public IReadOnlyList<string> Databases { get; }

    public DateTimeOffset LoadedAt { get; }

    public bool IsEmpty => Objects.Count == 0 && Schemas.Count == 0;

    /// <summary>
    /// 依名稱尋找物件。未指定 <paramref name="schemaName"/> 時會跨結構描述比對，
    /// 並把 dbo 的結果排在前面——沒有明確限定時那通常才是使用者想看的那一個。
    /// </summary>
    public IReadOnlyList<SqlObjectInfo> Find(string name, string? schemaName = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Array.Empty<SqlObjectInfo>();
        }

        var matches = new List<SqlObjectInfo>();

        foreach (var info in Objects)
        {
            if (!string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(schemaName) &&
                !string.Equals(info.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(info);
        }

        if (matches.Count > 1)
        {
            matches.Sort((left, right) => Rank(left).CompareTo(Rank(right)));
        }

        return matches;
    }

    private static int Rank(SqlObjectInfo info)
    {
        return string.Equals(info.SchemaName, "dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
