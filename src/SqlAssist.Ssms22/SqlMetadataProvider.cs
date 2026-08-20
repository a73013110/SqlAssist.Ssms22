using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using SqlAssist.Core;

namespace SqlAssist.Ssms22;

internal sealed class SqlMetadataProvider
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly IServiceProvider _serviceProvider;

    public SqlMetadataProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    public async Task<IReadOnlyList<SqlSuggestion>> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        var editorService = _serviceProvider.GetService(typeof(SSqlEditorService)) as ISqlEditorService;
        var sourceConnection = editorService?.GetCurrentConnection();

        if (sourceConnection is null)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var cacheKey = $"{RuntimeHelpers.GetHashCode(sourceConnection)}|{sourceConnection.Database}";

        lock (CacheLock)
        {
            if (Cache.TryGetValue(cacheKey, out var cached) &&
                DateTimeOffset.UtcNow - cached.CreatedAt < CacheLifetime)
            {
                return cached.Suggestions;
            }
        }

        var clonedConnection = CloneConnection(sourceConnection);

        if (clonedConnection is null)
        {
            SqlAssistDiagnostics.WriteAlways("無法複製目前 SQL 連線，略過資料庫物件建議");
            return Array.Empty<SqlSuggestion>();
        }

        try
        {
            var databaseName = sourceConnection.Database;
            var suggestions = await Task.Run(
                () => LoadSuggestions(clonedConnection, databaseName, cancellationToken),
                cancellationToken);

            lock (CacheLock)
            {
                Cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, suggestions);
            }

            return suggestions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"讀取資料庫物件失敗：{exception.Message}");
            return Array.Empty<SqlSuggestion>();
        }
        finally
        {
            clonedConnection.Dispose();
        }
    }

    private static IDbConnection? CloneConnection(IDbConnection source)
    {
        try
        {
            if (source is ICloneable cloneable && cloneable.Clone() is IDbConnection cloned)
            {
                return cloned;
            }

            if (Activator.CreateInstance(source.GetType()) is IDbConnection created)
            {
                created.ConnectionString = source.ConnectionString;
                return created;
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"複製 SQL 連線失敗：{exception.Message}");
        }

        return null;
    }

    private static IReadOnlyList<SqlSuggestion> LoadSuggestions(
        IDbConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        if (!string.IsNullOrWhiteSpace(databaseName) &&
            !string.Equals(connection.Database, databaseName, StringComparison.OrdinalIgnoreCase))
        {
            connection.ChangeDatabase(databaseName); // 跟隨目前查詢視窗選取的資料庫，而非登入預設資料庫。
        }

        using var command = connection.CreateCommand();
        command.CommandTimeout = 8;
        command.CommandText = MetadataQuery;

        using var reader = command.ExecuteReader();
        var builders = new Dictionary<int, SqlObjectBuilder>();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectId = reader.GetInt32(0);
            builders[objectId] = new SqlObjectBuilder(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
        }

        if (reader.NextResult())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var objectId = reader.GetInt32(0);

                if (builders.TryGetValue(objectId, out var builder))
                {
                    builder.AddColumn(
                        reader.GetString(2),
                        FormatDataType(reader),
                        reader.GetBoolean(7));
                }
            }
        }

        var results = builders.Values
            .Select(builder => builder.Build())
            .ToList();

        if (reader.NextResult())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var schemaName = reader.GetString(0);
                results.Add(new SqlSuggestion(
                    schemaName,
                    $"{QuoteIdentifier(schemaName)}.",
                    "Schema",
                    $"Schema {QuoteIdentifier(schemaName)}",
                    SuggestionKind.Schema,
                    triggerFollowUp: true,
                    schemaName: schemaName));
            }
        }

        return results;
    }

    private static string FormatDataType(IDataRecord record)
    {
        var typeName = record.GetString(3);
        var maxLength = record.GetInt16(4);
        var precision = record.GetByte(5);
        var scale = record.GetByte(6);

        if (typeName.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("nchar", StringComparison.OrdinalIgnoreCase))
        {
            maxLength = maxLength < 0 ? maxLength : (short)(maxLength / 2);
        }

        if (typeName.Equals("varchar", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("char", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("nchar", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("varbinary", StringComparison.OrdinalIgnoreCase))
        {
            return $"{typeName}({(maxLength < 0 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})";
        }

        if (typeName.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("numeric", StringComparison.OrdinalIgnoreCase))
        {
            return $"{typeName}({precision},{scale})";
        }

        return typeName;
    }

    private static string QuoteIdentifier(string name)
    {
        return "[" + name.Replace("]", "]]") + "]";
    }

    private const string MetadataQuery = @"
SELECT
    o.object_id,
    s.name AS schema_name,
    o.name AS object_name,
    o.type,
    sm.definition
FROM sys.objects AS o
INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
LEFT JOIN sys.sql_modules AS sm ON sm.object_id = o.object_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
ORDER BY s.name, o.name;

SELECT
    c.object_id,
    c.column_id,
    c.name AS column_name,
    t.name AS type_name,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable
FROM sys.columns AS c
INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
INNER JOIN sys.objects AS o ON o.object_id = c.object_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V')
ORDER BY c.object_id, c.column_id;

SELECT name
FROM sys.schemas
WHERE name NOT IN ('sys', 'INFORMATION_SCHEMA')
ORDER BY name;";

    private sealed class SqlObjectBuilder
    {
        private readonly List<string> _columns = new();
        private readonly string _definition;
        private readonly string _name;
        private readonly string _schema;
        private readonly string _type;

        public SqlObjectBuilder(string schema, string name, string type, string definition)
        {
            _schema = schema;
            _name = name;
            _type = type;
            _definition = definition;
        }

        public void AddColumn(string name, string dataType, bool nullable)
        {
            _columns.Add($"    {QuoteIdentifier(name)} {dataType}{(nullable ? " NULL" : " NOT NULL")}");
        }

        public SqlSuggestion Build()
        {
            var kind = GetKind(_type);
            var qualifiedName = $"{QuoteIdentifier(_schema)}.{QuoteIdentifier(_name)}";
            var description = $"{GetKindName(kind)} · {_schema}";
            var preview = BuildPreview(kind, qualifiedName);

            return new SqlSuggestion(
                _name,
                qualifiedName,
                description,
                preview,
                kind,
                schemaName: _schema);
        }

        private string BuildPreview(SuggestionKind kind, string qualifiedName)
        {
            if (!string.IsNullOrWhiteSpace(_definition) &&
                kind != SuggestionKind.Table)
            {
                return _definition;
            }

            var builder = new StringBuilder();
            builder.AppendLine($"{GetKindName(kind)} {qualifiedName}");

            if (_columns.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Columns");

                foreach (var column in _columns)
                {
                    builder.AppendLine(column);
                }
            }

            return builder.ToString();
        }

        private static SuggestionKind GetKind(string type)
        {
            return type switch
            {
                "U" => SuggestionKind.Table,
                "V" => SuggestionKind.View,
                "P" or "PC" => SuggestionKind.Procedure,
                _ => SuggestionKind.Function
            };
        }

        private static string GetKindName(SuggestionKind kind)
        {
            return kind switch
            {
                SuggestionKind.Table => "Table",
                SuggestionKind.View => "View",
                SuggestionKind.Procedure => "Procedure",
                SuggestionKind.Function => "Function",
                _ => "Object"
            };
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(DateTimeOffset createdAt, IReadOnlyList<SqlSuggestion> suggestions)
        {
            CreatedAt = createdAt;
            Suggestions = suggestions;
        }

        public DateTimeOffset CreatedAt { get; }

        public IReadOnlyList<SqlSuggestion> Suggestions { get; }
    }
}
