using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using SqlAssist.Core;
using SqlAssist.Core.Parsing;
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

    /// <summary>上一次從編輯器連線算出的快取鍵，用來判斷連線或資料庫有沒有換過。</summary>
    private string? _editorCacheKey;
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

    /// <summary>
    /// 取得敘述中某個資料來源的欄位建議。
    /// </summary>
    /// <remarks>
    /// 只在使用者真的輸入 <c>別名.</c> 時才觸發，因此會落在第二層按需載入：
    /// 一次只查一個物件的欄位，不會因為敘述裡有幾張資料表就全部撈回來。
    /// </remarks>
    public async Task<IReadOnlyList<SqlSuggestion>> GetColumnSuggestionsAsync(
        SqlTableReference table,
        CancellationToken cancellationToken)
    {
        if (table is null || table.IsDerived)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var total = Stopwatch.StartNew();
        var catalog = ResolveCatalog();

        if (catalog is null)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var matches = snapshot.Find(table.ObjectName, table.SchemaName);

        if (matches.Count == 0)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var cached = catalog.TryGetCachedDetail(matches[0].ObjectId, out _);
        var detail = await catalog.GetDetailAsync(matches[0], cancellationToken).ConfigureAwait(false);

        ReportIfSlow($"欄位建議 {matches[0].QualifiedName}（第二層{(cached ? "命中快取" : "查詢資料庫")}）", total);

        if (detail is null || detail.Columns.Count == 0)
        {
            return Array.Empty<SqlSuggestion>();
        }

        return BuildColumnSuggestions(matches[0], detail);
    }

    /// <summary>
    /// 預先載入敘述中各資料來源的欄位。
    /// </summary>
    /// <remarks>
    /// 使用者輸入 <c>a.</c> 的那一刻才去查欄位，等待就完全落在打字的節奏上。
    /// 但在那之前他已經打過 <c>FROM PUBLISHER a</c>，也已經至少開過一次建議清單——
    /// 那時就可以把敘述裡每一張資料表的欄位先撈回來，等到真的按下點號時直接命中快取。
    ///
    /// 失敗一律安靜略過：這只是預熱，真正需要時還會再走一次正規路徑。
    /// </remarks>
    public void WarmColumns(IReadOnlyList<SqlTableReference> tables)
    {
        if (tables is null || tables.Count == 0 || _disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = ResolveCatalog();

                if (catalog is null || !catalog.IsSnapshotFresh)
                {
                    return;
                }

                var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

                foreach (var table in tables)
                {
                    if (_disposed || table.IsDerived)
                    {
                        continue;
                    }

                    var matches = snapshot.Find(table.ObjectName, table.SchemaName);

                    if (matches.Count == 0 || catalog.TryGetCachedDetail(matches[0].ObjectId, out _))
                    {
                        continue;
                    }

                    var timer = Stopwatch.StartNew();
                    await catalog.GetDetailAsync(matches[0], CancellationToken.None).ConfigureAwait(false);
                    SqlAssistDiagnostics.Write(
                        $"已預先載入 {matches[0].QualifiedName} 的欄位（{timer.ElapsedMilliseconds} ms）");
                }
            }
            catch (Exception exception)
            {
                SqlAssistDiagnostics.Write($"預先載入欄位失敗：{exception.Message}");
            }
        });
    }

    /// <summary>
    /// 超過門檻的操作一律記錄，不必先打開詳細診斷。
    /// </summary>
    /// <remarks>
    /// 建議清單的延遲只有在使用者遇到時才觀察得到，事後要求對方重現並開啟追蹤
    /// 才拿得到數字，等於白白浪費一次。慢的操作本來就少，直接記下來成本可以忽略。
    /// </remarks>
    private static void ReportIfSlow(string operation, Stopwatch timer, int thresholdMilliseconds = 200)
    {
        timer.Stop();

        if (timer.ElapsedMilliseconds >= thresholdMilliseconds)
        {
            SqlAssistDiagnostics.WriteAlways($"耗時 {timer.ElapsedMilliseconds} ms：{operation}");
            return;
        }

        SqlAssistDiagnostics.Write($"耗時 {timer.ElapsedMilliseconds} ms：{operation}");
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

            // 連線來源的所有權在 SqlMetadataCatalogRegistry：目錄是跨查詢視窗共用的，
            // 這裡釋放會讓其他還開著的視窗一起失效。
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
        var timer = Stopwatch.StartNew();

        try
        {
            var editorService = _serviceProvider.GetService(typeof(SSqlEditorService)) as ISqlEditorService;
            editorConnection = editorService?.GetCurrentConnection();
            ReportIfSlow("向 SSMS 取得目前連線", timer);
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

            // 拿上一次「從編輯器連線算出的鍵」來比，而不是目錄自己的鍵。
            // 目錄的鍵是從複製出來的樣板連線算的，而複製品與原始連線的
            // ConnectionString 未必逐字相同（例如密碼是否回傳），
            // 一旦不同，這個快取判斷就永遠不成立，每次按鍵都要重建連線來源。
            if (_catalog is not null && string.Equals(_editorCacheKey, cacheKey, StringComparison.Ordinal))
            {
                return _catalog;
            }

            _editorCacheKey = cacheKey;
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

    /// <summary>
    /// 把欄位轉成建議項。
    /// </summary>
    /// <remarks>
    /// 欄位的排序刻意保留資料表定義順序：模糊比對的分數才是主要排名依據，
    /// 而分數相同時（例如還沒輸入任何字元）依序號排列比字母序更接近使用者的心智模型。
    /// </remarks>
    private static IReadOnlyList<SqlSuggestion> BuildColumnSuggestions(
        SqlObjectInfo info,
        SqlObjectDetail detail)
    {
        var suggestions = new List<SqlSuggestion>(detail.Columns.Count);

        foreach (var column in detail.Columns)
        {
            var annotations = column.IsPrimaryKey ? " · PK" : string.Empty;

            suggestions.Add(new SqlSuggestion(
                column.Name,
                SqlIdentifier.QuoteIfNeeded(column.Name),
                $"{column.DataType}{(column.IsNullable ? " NULL" : " NOT NULL")}{annotations}",
                $"{info.QualifiedName}\r\n{column.ToScriptLine()}",
                SuggestionKind.Column,
                schemaName: info.SchemaName,
                tag: column));
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
