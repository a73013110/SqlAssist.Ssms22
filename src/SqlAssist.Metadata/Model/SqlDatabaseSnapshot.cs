using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;

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

    /// <summary>依名稱查物件的索引；<see cref="Find"/> 在每一次按鍵上，不逐一掃過清單。</summary>
    private readonly Dictionary<string, IReadOnlyList<SqlObjectInfo>> _index;

    /// <summary>系統物件的索引；那一份還沒載入時是空的。</summary>
    private readonly Dictionary<string, IReadOnlyList<SqlObjectInfo>> _systemIndex;

    /// <summary><see cref="SchemasWithObjects"/> 的結果；第一次被問到才算。</summary>
    private IReadOnlyList<string>? _schemasWithObjects;

    public SqlDatabaseSnapshot(
        string databaseName,
        IReadOnlyList<SqlObjectInfo> objects,
        IReadOnlyList<string> schemas,
        IReadOnlyList<string> databases,
        DateTimeOffset loadedAt,
        IReadOnlyList<string>? linkedServers = null)
        : this(
            databaseName,
            SortByName(objects),
            schemas,
            databases,
            loadedAt,
            linkedServers,
            Array.Empty<SqlObjectInfo>())
    {
    }

    /// <param name="objects">已經排序過的物件清單。</param>
    private SqlDatabaseSnapshot(
        string databaseName,
        IReadOnlyList<SqlObjectInfo> objects,
        IReadOnlyList<string> schemas,
        IReadOnlyList<string> databases,
        DateTimeOffset loadedAt,
        IReadOnlyList<string>? linkedServers,
        IReadOnlyList<SqlObjectInfo> systemObjects)
    {
        DatabaseName = databaseName ?? string.Empty;
        Objects = objects;
        Schemas = schemas ?? Array.Empty<string>();
        Databases = databases ?? Array.Empty<string>();
        LinkedServers = linkedServers ?? Array.Empty<string>();
        LoadedAt = loadedAt;
        SystemObjects = systemObjects;
        _index = BuildIndex(Objects);
        _systemIndex = BuildIndex(SystemObjects);
    }

    /// <summary>
    /// 接上分開載入的那一份系統物件，回傳新的快照。
    /// </summary>
    /// <remarks>
    /// 併進同一份快照而不是讓每個呼叫端各問一次：「這個名稱是哪個物件」在滑鼠停留、
    /// F12、欄位建議、<c>SELECT *</c> 展開與結構預覽上是同一個問題，各接一條的症狀是
    /// <c>FROM sys.triggers</c> 之後有的位置列得出欄位、有的位置什麼都沒有，
    /// 而畫面上看不出差別。
    ///
    /// 刻意不併進 <see cref="Objects"/>：那一份是列給使用者看的清單，而系統物件有
    /// 一兩千筆，混進去等於打第一個字元時真正要找的東西被 <c>sp_</c> 開頭的名稱淹掉。
    /// <see cref="Find"/> 只在限定字是系統結構描述時才問這一份。
    /// </remarks>
    public SqlDatabaseSnapshot WithSystemObjects(IReadOnlyList<SqlObjectInfo>? systemObjects)
    {
        if (systemObjects is null || systemObjects.Count == 0)
        {
            return this;
        }

        return new SqlDatabaseSnapshot(
            DatabaseName,
            Objects,
            Schemas,
            Databases,
            LoadedAt,
            LinkedServers,
            systemObjects);
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

    /// <summary>這個資料庫的每一個結構描述，包括底下沒有任何物件的。</summary>
    /// <remarks>
    /// 認限定字要用這一份完整名單（<see cref="SqlQualifierResolver"/>）：空的結構描述
    /// 少掉的話，它與某個資料庫或連結伺服器同名時會被改認成那一個，清單換成另一個
    /// 地方的內容。列給使用者挑的是 <see cref="SchemasWithObjects"/>。
    /// </remarks>
    public IReadOnlyList<string> Schemas { get; }

    /// <summary>底下至少有一個第一層物件的結構描述，順序與 <see cref="Schemas"/> 相同。</summary>
    /// <remarks>
    /// 建議清單讀這一份。每個資料庫都帶著一批與固定角色同名、裡面什麼都沒有的結構描述
    /// （<c>db_denydatareader</c>、<c>guest</c>…），全列出來的話，<c>FROM d</c> 的前幾名
    /// 就被選了也接不到任何東西的名稱佔掉。
    ///
    /// 快照不可變，所以只算一次；在第一次被問到時才算，是因為只拿快照認名稱的路
    /// （滑鼠停留、F12）用不到它。多執行緒同時第一次問時可能各算一次，結果相同，
    /// 不必上鎖。
    /// </remarks>
    public IReadOnlyList<string> SchemasWithObjects =>
        _schemasWithObjects ??= FilterSchemasWithObjects(Schemas, Objects);

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

    /// <summary>
    /// <c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 底下的物件；那一份還沒載入時是空的。
    /// </summary>
    /// <remarks>與 <see cref="Objects"/> 分開放，理由見 <see cref="WithSystemObjects"/>。</remarks>
    public IReadOnlyList<SqlObjectInfo> SystemObjects { get; }

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
    /// <remarks>
    /// 限定字是 <c>sys</c> 或 <c>INFORMATION_SCHEMA</c> 時查的是
    /// <see cref="SystemObjects"/>，而且<b>只</b>查那一份：使用者建不出那兩個結構描述
    /// 底下的東西。反過來，沒有限定字時一個系統物件都不查——<c>FROM objects</c> 在
    /// T-SQL 裡本來就不成立，答得出來的只會是我們自己編的。
    /// </remarks>
    public IReadOnlyList<SqlObjectInfo> Find(string name, string? schemaName = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Array.Empty<SqlObjectInfo>();
        }

        var index = SqlSystemSchemas.IsSystem(schemaName) ? _systemIndex : _index;

        if (!index.TryGetValue(name, out var candidates))
        {
            return Array.Empty<SqlObjectInfo>();
        }

        if (string.IsNullOrEmpty(schemaName))
        {
            return candidates;
        }

        // 同一個名稱落在兩個結構描述上是少數，那時才配置一份過濾後的清單。
        if (candidates.Count == 1)
        {
            return InSchema(candidates[0], schemaName!)
                ? candidates
                : Array.Empty<SqlObjectInfo>();
        }

        var matches = new List<SqlObjectInfo>(candidates.Count);

        foreach (var info in candidates)
        {
            if (InSchema(info, schemaName!))
            {
                matches.Add(info);
            }
        }

        return matches;
    }

    private static bool InSchema(SqlObjectInfo info, string schemaName)
    {
        return string.Equals(info.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 依名稱分組，同名的多筆把 dbo 排在前面。
    /// </summary>
    /// <remarks>
    /// 建索引的成本付在載入那一次，而 <see cref="Find"/> 在每一次按鍵上——一條敘述有
    /// 幾個資料來源就問幾次。掃過幾千個物件的原本是每一次按鍵，現在是每一次載入。
    /// </remarks>
    private static Dictionary<string, IReadOnlyList<SqlObjectInfo>> BuildIndex(
        IReadOnlyList<SqlObjectInfo> objects)
    {
        var index = new Dictionary<string, IReadOnlyList<SqlObjectInfo>>(
            objects.Count,
            StringComparer.OrdinalIgnoreCase);

        foreach (var info in objects)
        {
            if (index.TryGetValue(info.Name, out var existing))
            {
                ((List<SqlObjectInfo>)existing).Add(info);
                continue;
            }

            index[info.Name] = new List<SqlObjectInfo> { info };
        }

        foreach (var bucket in index.Values)
        {
            if (bucket.Count > 1)
            {
                ((List<SqlObjectInfo>)bucket).Sort(
                    (left, right) => Rank(left).CompareTo(Rank(right)));
            }
        }

        return index;
    }

    private static IReadOnlyList<string> FilterSchemasWithObjects(
        IReadOnlyList<string> schemas,
        IReadOnlyList<SqlObjectInfo> objects)
    {
        if (schemas.Count == 0)
        {
            return schemas;
        }

        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in objects)
        {
            owners.Add(info.SchemaName);
        }

        var result = new List<string>(Math.Min(schemas.Count, owners.Count));

        foreach (var schema in schemas)
        {
            if (owners.Contains(schema))
            {
                result.Add(schema);
            }
        }

        return result;
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
