using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using SqlAssist.Core;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22;

/// <summary>
/// 銜接 SSMS 的查詢視窗連線與中繼資料層。
/// </summary>
/// <remarks>
/// 只負責兩件事：從 SSMS 取得目前連線並在連線或資料庫改變時重建連線來源，
/// 以及把中繼資料層的物件描述轉成建議清單用的 <see cref="SqlSuggestion"/>。
/// 實際的查詢、分層與快取都在 <see cref="SqlMetadataCatalog"/>。
/// </remarks>
internal sealed class SqlMetadataService : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly IServiceProvider _serviceProvider;
    private SsmsConnectionSource? _connectionSource;
    private SqlMetadataCatalog? _catalog;
    private bool _disposed;

    public SqlMetadataService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>清空所有資料庫的快取。</summary>
    public static void InvalidateAll()
    {
        SqlMetadataCatalogRegistry.Default.InvalidateAll();
    }

    /// <summary>
    /// 取得目前資料庫的物件建議。回傳的建議只帶名稱層級的資訊，
    /// 欄位與定義要另外呼叫 <see cref="GetDetailAsync"/>。
    /// </summary>
    public async Task<IReadOnlyList<SqlSuggestion>> GetSuggestionsAsync(
        CancellationToken cancellationToken)
    {
        var catalog = ResolveCatalog();

        if (catalog is null)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return BuildSuggestions(snapshot);
    }

    /// <summary>取得目前資料庫的第一層中繼資料；沒有可用連線時回傳 null。</summary>
    public async Task<SqlDatabaseSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var catalog = ResolveCatalog();

        if (catalog is null)
        {
            return null;
        }

        return await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>載入單一物件的欄位、參數與定義。</summary>
    public async Task<SqlObjectDetail?> GetDetailAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken)
    {
        var catalog = ResolveCatalog();

        if (catalog is null)
        {
            return null;
        }

        return await catalog.GetDetailAsync(objectInfo, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connectionSource?.Dispose();
            _connectionSource = null;
            _catalog = null;
        }
    }

    /// <summary>
    /// 取得目前連線對應的目錄。使用者切換資料庫或重新連線時，快取鍵會改變，
    /// 這裡會重建連線來源並換到對應的目錄。
    /// </summary>
    private SqlMetadataCatalog? ResolveCatalog()
    {
        IDbConnection? editorConnection;

        try
        {
            var editorService = _serviceProvider.GetService(typeof(SSqlEditorService)) as ISqlEditorService;
            editorConnection = editorService?.GetCurrentConnection();
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"取得 SSMS 目前連線失敗：{exception.Message}");
            return null;
        }

        if (editorConnection is null)
        {
            return null;
        }

        var cacheKey = SqlConnectionCacheKey.Create(
            editorConnection.ConnectionString,
            editorConnection.Database);

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return null;
            }

            if (_catalog is not null && string.Equals(_catalog.CacheKey, cacheKey, StringComparison.Ordinal))
            {
                return _catalog;
            }

            _connectionSource?.Dispose();
            _connectionSource = SsmsConnectionSource.TryCreate(editorConnection);

            if (_connectionSource is null)
            {
                _catalog = null;
                return null;
            }

            _catalog = SqlMetadataCatalogRegistry.Default.GetOrCreate(_connectionSource);
            return _catalog;
        }
    }

    private static IReadOnlyList<SqlSuggestion> BuildSuggestions(SqlDatabaseSnapshot snapshot)
    {
        var suggestions = new List<SqlSuggestion>(snapshot.Objects.Count + snapshot.Schemas.Count);

        foreach (var info in snapshot.Objects)
        {
            var kind = ToSuggestionKind(info.Kind);

            if (kind is null)
            {
                continue;
            }

            suggestions.Add(new SqlSuggestion(
                info.Name,
                info.QualifiedName,
                $"{info.Kind.ToDisplayName()} · {info.SchemaName}",
                // 預覽內容改為選取時才載入，這裡只放立即可得的標題。
                $"{info.Kind.ToDisplayName()} {info.QualifiedName}",
                kind.Value,
                schemaName: info.SchemaName,
                tag: info));
        }

        foreach (var schema in snapshot.Schemas)
        {
            suggestions.Add(new SqlSuggestion(
                schema,
                SqlIdentifier.Quote(schema) + ".",
                "Schema",
                $"Schema {SqlIdentifier.Quote(schema)}",
                SuggestionKind.Schema,
                triggerFollowUp: true,
                schemaName: schema));
        }

        return suggestions;
    }

    private static SuggestionKind? ToSuggestionKind(SqlObjectKind kind)
    {
        return kind switch
        {
            SqlObjectKind.Table => SuggestionKind.Table,
            // 同義字幾乎都指向資料表或檢視，放在資料來源清單裡才找得到。
            SqlObjectKind.Synonym => SuggestionKind.Table,
            SqlObjectKind.View => SuggestionKind.View,
            SqlObjectKind.Procedure => SuggestionKind.Procedure,
            SqlObjectKind.ScalarFunction => SuggestionKind.Function,
            SqlObjectKind.InlineTableFunction => SuggestionKind.Function,
            SqlObjectKind.TableValuedFunction => SuggestionKind.Function,
            _ => null
        };
    }
}
