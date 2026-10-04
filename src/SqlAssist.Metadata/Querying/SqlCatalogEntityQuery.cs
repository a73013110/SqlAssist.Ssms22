using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 目錄物件的名冊：每一種 <see cref="SqlCatalogEntity"/> 的名稱向哪一條查詢要。
/// </summary>
/// <remarks>
/// 種類、範圍與位置在 Core 的 <see cref="SqlCatalogEntity"/>；這裡只放中繼資料層才有的東西：查詢本身，
/// 或第一層快照裡已經有的那一份（結構描述、資料庫）。每一條只回一欄名稱、照名稱排好。
///
/// 每一條都是目錄檢視，看不到的列由伺服器依權限濾掉（沒有 VIEW DEFINITION 的登入看到的是自己那幾列），
/// 不是錯誤；真的查不了（檢視在這一版還沒有、權限整個被拒）由目錄的降級接住，那一格只剩片語的字。
/// 2016 才有的檢視先問 <c>sys.all_views</c>，與時區名單同一條：舊版是一份成功的空名單，不是每一次都失敗的降級。
/// 系統自己的主體與金鑰（<c>##MS_…##</c>、<c>sys</c>、<c>INFORMATION_SCHEMA</c>）寫不進 ALTER、DROP，不列。
/// </remarks>
public sealed class SqlCatalogEntityQuery
{
    private const string Principals = "('S', 'U', 'G', 'E', 'X', 'C', 'K')";

    private static readonly Dictionary<SqlCatalogEntity, SqlCatalogEntityQuery> Queries = new()
    {
        [SqlCatalogEntity.Login] = Sql($@"
SELECT p.name
FROM sys.server_principals AS p
WHERE p.type IN {Principals}
  AND p.name NOT LIKE N'##%'
ORDER BY p.name;"),
        [SqlCatalogEntity.ServerRole] = Sql(@"
SELECT p.name
FROM sys.server_principals AS p
WHERE p.type = 'R'
ORDER BY p.name;"),
        [SqlCatalogEntity.Credential] = Sql(@"
SELECT c.name
FROM sys.credentials AS c
ORDER BY c.name;"),
        [SqlCatalogEntity.Endpoint] = Sql(@"
SELECT e.name
FROM sys.endpoints AS e
ORDER BY e.name;"),
        [SqlCatalogEntity.EventSession] = Sql(@"
SELECT s.name
FROM sys.server_event_sessions AS s
ORDER BY s.name;"),
        [SqlCatalogEntity.ServerAudit] = Sql(@"
SELECT a.name
FROM sys.server_audits AS a
ORDER BY a.name;"),
        [SqlCatalogEntity.ServerAuditSpecification] = Sql(@"
SELECT s.name
FROM sys.server_audit_specifications AS s
ORDER BY s.name;"),
        [SqlCatalogEntity.Database] = Snapshot(snapshot => snapshot.Databases),
        [SqlCatalogEntity.User] = Sql($@"
SELECT p.name
FROM sys.database_principals AS p
WHERE p.type IN {Principals}
  AND p.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
  AND p.name NOT LIKE N'##%'
ORDER BY p.name;"),
        [SqlCatalogEntity.Role] = Sql(@"
SELECT p.name
FROM sys.database_principals AS p
WHERE p.type = 'R'
ORDER BY p.name;"),
        [SqlCatalogEntity.ApplicationRole] = Sql(@"
SELECT p.name
FROM sys.database_principals AS p
WHERE p.type = 'A'
ORDER BY p.name;"),

        // DROP SCHEMA 要的多半正是空的那一個，取完整名單，不是建議清單用的 SchemasWithObjects。
        [SqlCatalogEntity.Schema] = Snapshot(snapshot => snapshot.Schemas),
        [SqlCatalogEntity.Certificate] = Sql(@"
SELECT c.name
FROM sys.certificates AS c
WHERE c.name NOT LIKE N'##%'
ORDER BY c.name;"),
        [SqlCatalogEntity.AsymmetricKey] = Sql(@"
SELECT k.name
FROM sys.asymmetric_keys AS k
WHERE k.name NOT LIKE N'##%'
ORDER BY k.name;"),
        [SqlCatalogEntity.SymmetricKey] = Sql(@"
SELECT k.name
FROM sys.symmetric_keys AS k
WHERE k.name NOT LIKE N'##%'
ORDER BY k.name;"),
        [SqlCatalogEntity.DatabaseScopedCredential] = SqlSince("database_scoped_credentials", "c"),
        [SqlCatalogEntity.DatabaseAuditSpecification] = Sql(@"
SELECT s.name
FROM sys.database_audit_specifications AS s
ORDER BY s.name;"),
        [SqlCatalogEntity.PartitionFunction] = Sql(@"
SELECT f.name
FROM sys.partition_functions AS f
ORDER BY f.name;"),
        [SqlCatalogEntity.PartitionScheme] = Sql(@"
SELECT s.name
FROM sys.partition_schemes AS s
ORDER BY s.name;"),
        [SqlCatalogEntity.FulltextCatalog] = Sql(@"
SELECT c.name
FROM sys.fulltext_catalogs AS c
ORDER BY c.name;"),
        [SqlCatalogEntity.Assembly] = Sql(@"
SELECT a.name
FROM sys.assemblies AS a
WHERE a.is_user_defined = 1
ORDER BY a.name;"),
        [SqlCatalogEntity.ExternalDataSource] = SqlSince("external_data_sources", "d"),
        [SqlCatalogEntity.ExternalFileFormat] = SqlSince("external_file_formats", "f"),

        // Service Broker：系統的合約、訊息類型與服務（DEFAULT、事件通知的那一組）照列，ON CONTRACT、CREATE SERVICE 的合約清單引用得到；
        // 系統佇列只給 Service Broker 自己用，不列。
        [SqlCatalogEntity.Queue] = Sql(@"
SELECT q.name
FROM sys.service_queues AS q
WHERE q.is_ms_shipped = 0
ORDER BY q.name;"),
        [SqlCatalogEntity.Service] = Sql(@"
SELECT s.name
FROM sys.services AS s
ORDER BY s.name;"),
        [SqlCatalogEntity.Contract] = Sql(@"
SELECT c.name
FROM sys.service_contracts AS c
ORDER BY c.name;"),
        [SqlCatalogEntity.MessageType] = Sql(@"
SELECT m.name
FROM sys.service_message_types AS m
ORDER BY m.name;"),
        [SqlCatalogEntity.Route] = Sql(@"
SELECT r.name
FROM sys.routes AS r
ORDER BY r.name;"),
        [SqlCatalogEntity.RemoteServiceBinding] = Sql(@"
SELECT b.name
FROM sys.remote_service_bindings AS b
ORDER BY b.name;"),
        [SqlCatalogEntity.BrokerPriority] = Sql(@"
SELECT p.name
FROM sys.conversation_priorities AS p
ORDER BY p.name;"),
    };

    private SqlCatalogEntityQuery(string? text, Func<SqlDatabaseSnapshot, IReadOnlyList<string>>? fromSnapshot)
    {
        Text = text;
        FromSnapshot = fromSnapshot;
    }

    /// <summary>查詢；名稱已在第一層快照裡的那幾種是 <c>null</c>。</summary>
    public string? Text { get; }

    /// <summary>從第一層快照取名稱；要另外查詢的是 <c>null</c>。</summary>
    public Func<SqlDatabaseSnapshot, IReadOnlyList<string>>? FromSnapshot { get; }

    public static SqlCatalogEntityQuery For(SqlCatalogEntity entity)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        return Queries.TryGetValue(entity, out var query)
            ? query
            : throw new ArgumentOutOfRangeException(nameof(entity), entity.Kind, null);
    }

    private static SqlCatalogEntityQuery Sql(string text) => new(text, null);

    private static SqlCatalogEntityQuery Snapshot(Func<SqlDatabaseSnapshot, IReadOnlyList<string>> select) => new(null, select);

    /// <summary>SQL Server 2016 才有的目錄檢視：這一版沒有時是一份成功的空名單。</summary>
    private static SqlCatalogEntityQuery SqlSince(string view, string alias) => Sql($@"
IF EXISTS (SELECT 1 FROM sys.all_views AS v WHERE v.name = N'{view}' AND v.schema_id = 4)
    EXEC (N'SELECT {alias}.name FROM sys.{view} AS {alias} ORDER BY {alias}.name;');");
}
