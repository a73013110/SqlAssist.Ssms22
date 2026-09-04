using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Model;

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
        DateTimeOffset loadedAt,
        IReadOnlyList<string>? linkedServers = null)
    {
        DatabaseName = databaseName ?? string.Empty;
        Objects = SortByName(objects);
        Schemas = schemas ?? Array.Empty<string>();
        Databases = databases ?? Array.Empty<string>();
        LinkedServers = linkedServers ?? Array.Empty<string>();
        LoadedAt = loadedAt;
    }

    public string DatabaseName { get; }

    /// <summary>物件清單，已依名稱排序。</summary>
    /// <remarks>
    /// 查詢本身沒有 <c>ORDER BY</c>——伺服器回傳的大致是建立順序，
    /// 那個順序對使用者沒有任何意義。建議清單同分時保留候選項的原始順序，
    /// 所以「原始順序」必須自己先弄成有意義的：這裡排一次，
    /// 之後每一次按鍵都不必再排。
    /// </remarks>
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

    /// <summary>
    /// 這一台伺服器上掛的連結伺服器，四段式名稱的第一段。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Databases"/> 一樣是伺服器層級的內容放在資料庫層級的快照裡，
    /// 理由也一樣：幾列名稱不值得多養一層快取與它的失效規則。
    ///
    /// 這份清單本身<b>不需要</b>對任何一台連結伺服器送出查詢——<c>sys.servers</c>
    /// 就在目前這條連線上。沒有它的話，只看文字分不出 <c>SQL209.</c> 是結構描述、
    /// 資料庫還是伺服器，而右對齊會一律猜成結構描述，於是清單一筆都比不中。
    /// </remarks>
    public IReadOnlyList<string> LinkedServers { get; }

    public DateTimeOffset LoadedAt { get; }

    /// <summary>這份快照什麼都沒有，等於還沒載入成功。</summary>
    /// <remarks>
    /// 資料庫清單也要算進來。連結伺服器本身那一格（<c>LibMirror.</c>）的快照
    /// <b>只有</b>資料庫清單——不算的話它永遠不「新鮮」，於是每按一次鍵就重查一次
    /// 那台伺服器，而那一輪的延遲由對方決定。
    /// </remarks>
    public bool IsEmpty => Objects.Count == 0 && Schemas.Count == 0 && Databases.Count == 0;

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

    private static IReadOnlyList<SqlObjectInfo> SortByName(IReadOnlyList<SqlObjectInfo>? objects)
    {
        if (objects is null)
        {
            return Array.Empty<SqlObjectInfo>();
        }

        if (objects.Count < 2)
        {
            return objects;
        }

        var sorted = new List<SqlObjectInfo>(objects);

        // 同名不同結構描述時再比結構描述，順序才是穩定的。
        sorted.Sort((left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

            return byName != 0
                ? byName
                : string.Compare(left.SchemaName, right.SchemaName, StringComparison.OrdinalIgnoreCase);
        });

        return sorted;
    }

    private static int Rank(SqlObjectInfo info)
    {
        return string.Equals(info.SchemaName, "dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
