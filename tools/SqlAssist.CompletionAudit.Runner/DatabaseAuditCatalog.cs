using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Completion;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>直連一個資料庫的稽核目錄：建議清單走產品同一份中繼資料層，名稱索引讀同一份快照。</summary>
internal sealed class DatabaseAuditCatalog : IAuditCatalog
{
    private readonly SqlMetadataCatalog _catalog;

    public DatabaseAuditCatalog(AuditConnection connection, string database)
    {
        Database = database;
        _catalog = SqlMetadataCatalogRegistry.Default.GetOrCreate(new ConnectionSource(connection, database));
        Metadata = new SqlCatalogCompletionMetadata(() => _catalog);
    }

    public string Database { get; }

    public ISqlCompletionMetadata Metadata { get; }

    /// <summary>第一層快照的指紋：物件換了，快取裡這個資料庫的結果就不算數。</summary>
    public async Task<string> SignatureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var names = new List<string>(snapshot.Objects.Count + snapshot.Schemas.Count + snapshot.Databases.Count);

        foreach (var info in snapshot.Objects)
        {
            names.Add($"{info.Kind}:{info.SchemaName}.{info.Name}");
        }

        names.AddRange(snapshot.Schemas);
        names.AddRange(snapshot.Databases);
        names.Sort(StringComparer.Ordinal);
        return AuditHash.Of(string.Join("\n", names));
    }

    /// <summary>
    /// 這個批次的名稱索引：資料庫的物件、結構描述與資料庫，加上批次裡寫到的資料表的欄位。
    /// </summary>
    /// <remarks>
    /// 欄位只載寫到的那幾張：整個資料庫的欄位要一張表一次查詢。<c>sys.</c> 之後的名稱另外問系統物件，
    /// 那一份不在第一層。
    /// </remarks>
    public async Task<IAuditNameIndex> IndexAsync(string batch, IReadOnlyList<SqlToken> tokens, CancellationToken cancellationToken)
    {
        var snapshot = await _catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var index = new NameIndex();

        foreach (var database in snapshot.Databases)
        {
            index.Add(database, AuditTokenClass.Database);
        }

        foreach (var schema in snapshot.Schemas)
        {
            index.Add(schema, AuditTokenClass.Schema);
        }

        var loaded = new HashSet<int>();

        for (var position = 0; position < tokens.Count; position++)
        {
            var token = tokens[position];

            if (token.Kind != SqlTokenKind.Identifier)
            {
                continue;
            }

            var schema = position >= 2 && tokens[position - 1].IsPunctuation(".") ? tokens[position - 2].Value : null;
            var matches = string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)
                ? await _catalog.FindObjectsAsync(token.Value, schema, cancellationToken).ConfigureAwait(false)
                : snapshot.Find(token.Value);

            foreach (var match in matches)
            {
                index.Add(match.Name, AuditTokenClass.Object);

                if (!HasColumns(match.Kind) || !loaded.Add(match.ObjectId))
                {
                    continue;
                }

                var detail = await _catalog.GetDetailAsync(match, cancellationToken, NotificationOrigin.Ambient).ConfigureAwait(false);

                foreach (var column in detail?.Columns ?? (IReadOnlyList<SqlColumnInfo>)Array.Empty<SqlColumnInfo>())
                {
                    index.Add(column.Name, AuditTokenClass.Column);
                }
            }
        }

        foreach (var info in snapshot.Objects)
        {
            index.Add(info.Name, AuditTokenClass.Object);
        }

        return index;
    }

    private static bool HasColumns(SqlObjectKind kind) =>
        kind is SqlObjectKind.Table or SqlObjectKind.View or SqlObjectKind.InlineTableFunction or SqlObjectKind.TableValuedFunction;

    /// <summary>名稱到種類；同名時先登記的贏，只有物件蓋得掉欄位（與資料表同名的欄位多半是在指那張表）。</summary>
    /// <remarks>種類只影響簽章與代號的前綴；比對一律照名稱，任何一種清單項目列出同名的都算。</remarks>
    private sealed class NameIndex : IAuditNameIndex
    {
        private readonly Dictionary<string, AuditTokenClass> _names = new(StringComparer.Ordinal);

        public void Add(string name, AuditTokenClass tokenClass)
        {
            var key = AuditText.Normalize(name);

            if (!_names.TryGetValue(key, out var existing) ||
                existing == AuditTokenClass.Column && tokenClass == AuditTokenClass.Object)
            {
                _names[key] = tokenClass;
            }
        }

        public AuditTokenClass? Find(string name) =>
            _names.TryGetValue(AuditText.Normalize(name), out var tokenClass) ? tokenClass : null;
    }

    /// <summary>以稽核的連線設定開連線；認證走 SqlCredential，不進連線字串。</summary>
    private sealed class ConnectionSource : ISqlConnectionSource
    {
        private readonly AuditConnection _connection;

        public ConnectionSource(AuditConnection connection, string database)
        {
            _connection = connection;
            DatabaseName = database;
            ServerCacheKey = SqlConnectionCacheKey.CreateServerKey(connection.ConnectionString(null));
            CacheKey = SqlConnectionCacheKey.Compose(ServerCacheKey, database);
        }

        public string CacheKey { get; }

        public string ServerCacheKey { get; }

        public string DatabaseName { get; }

        public IDbConnection OpenConnection() => _connection.Open(DatabaseName);
    }
}
