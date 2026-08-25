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
    private readonly HashSet<int> _warmingDetails = new();
    private readonly IServiceProvider _serviceProvider;
    private SsmsConnectionSource? _connectionSource;
    private SqlMetadataCatalog? _catalog;

    /// <summary>上一次從編輯器連線算出的快取鍵，用來判斷連線或資料庫有沒有換過。</summary>
    private string? _editorCacheKey;

    /// <summary>上一次真的去問 SSMS 目前連線的時間。</summary>
    private DateTimeOffset _catalogCheckedAt;

    private int _recheckInFlight;

    /// <summary>
    /// 多久重新問一次 SSMS「現在連到哪裡」。
    /// </summary>
    /// <remarks>
    /// <c>ISqlEditorService.GetCurrentConnection()</c> 有 UI 執行緒相依性，從背景執行緒
    /// 呼叫會被 marshal 回 UI 執行緒。平常只要幾毫秒，但 SSMS 內建 IntelliSense
    /// 正在忙的時候會排隊到將近兩秒——而那正是使用者輸入 <c>a.</c> 的同一刻。
    /// 實機紀錄：同一個呼叫平常 2 到 7 ms，塞住時 1908 ms。
    ///
    /// 使用者切換資料庫或重新連線並不頻繁，晚幾秒才反映完全可以接受，
    /// 但每一次按鍵都要冒一次卡住兩秒的風險不行。
    /// </remarks>
    private static readonly TimeSpan CatalogRecheckInterval = TimeSpan.FromSeconds(10);
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

        return BuildColumnSuggestions(matches[0], detail, SettingsService.Default.GetSnapshot());
    }

    /// <summary>
    /// 取得敘述中所有資料來源的欄位，供沒有限定字的位置使用。
    /// </summary>
    /// <remarks>
    /// 只回傳<b>已經在快取裡</b>的欄位，絕不觸發查詢：這條路徑在每一次按鍵上。
    /// 沒命中就這一輪不顯示欄位，<see cref="WarmColumns"/> 會在背景補上，
    /// 下一次按鍵就有了。
    ///
    /// 敘述裡有兩個以上的資料來源時，插入的文字會補上別名，
    /// 否則 <c>SELECT Name FROM A a JOIN B b</c> 這種寫法會因為欄位名稱模稜兩可而執行失敗。
    /// </remarks>
    public IReadOnlyList<SqlSuggestion> GetCachedScopeColumns(IReadOnlyList<SqlTableReference> tables)
    {
        if (tables is null || tables.Count == 0 || _disposed)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var catalog = ResolveCatalog();

        if (catalog is null)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var snapshot = catalog.CachedSnapshot;

        if (snapshot.IsEmpty)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var settings = SettingsService.Default.GetSnapshot();
        var sources = 0;

        foreach (var table in tables)
        {
            if (!table.IsDerived)
            {
                sources++;
            }
        }

        var qualify = sources > 1;
        var suggestions = new List<SqlSuggestion>();

        foreach (var table in tables)
        {
            if (table.IsDerived)
            {
                continue;
            }

            var matches = snapshot.Find(table.ObjectName, table.SchemaName);

            if (matches.Count == 0 || !catalog.TryGetCachedDetail(matches[0].ObjectId, out var detail))
            {
                continue;
            }

            foreach (var column in detail.Columns)
            {
                suggestions.Add(BuildColumnSuggestion(
                    matches[0],
                    column,
                    settings,
                    qualify ? table.EffectiveName : null));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// 目前已快取的第一層資料；沒有現成的目錄或還沒載入時回傳 null。
    /// </summary>
    /// <remarks>不觸發任何查詢，也不向 SSMS 詢問連線。滑鼠停留提示走這條路。</remarks>
    public SqlDatabaseSnapshot? PeekSnapshot()
    {
        var snapshot = PeekCatalog()?.CachedSnapshot;
        return snapshot is null || snapshot.IsEmpty ? null : snapshot;
    }

    /// <summary>只看第二層快取裡有沒有這個物件的明細；沒有就回傳 null，不觸發查詢。</summary>
    public SqlObjectDetail? PeekDetail(SqlObjectInfo objectInfo)
    {
        if (objectInfo is null)
        {
            return null;
        }

        var catalog = PeekCatalog();

        return catalog is not null && catalog.TryGetCachedDetail(objectInfo.ObjectId, out var detail)
            ? detail
            : null;
    }

    /// <summary>
    /// 在背景把單一物件的明細載入快取。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留提示只讀快取，沒命中就顯示標題並呼叫這裡；滑鼠移到下一個識別字再回來時
    /// 就有內容了。同一個物件同時只會有一次載入在飛，否則滑鼠在同一個字上晃動
    /// 就會連續丟出好幾次相同的查詢。
    /// </remarks>
    public void WarmDetail(SqlObjectInfo objectInfo)
    {
        if (objectInfo is null || _disposed)
        {
            return;
        }

        var catalog = PeekCatalog();

        if (catalog is null || catalog.TryGetCachedDetail(objectInfo.ObjectId, out _))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (!_warmingDetails.Add(objectInfo.ObjectId))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            var timer = Stopwatch.StartNew();

            try
            {
                await catalog.GetDetailAsync(objectInfo, CancellationToken.None).ConfigureAwait(false);
                SqlAssistDiagnostics.Write(
                    $"已預先載入 {objectInfo.QualifiedName} 的結構（{timer.ElapsedMilliseconds} ms）");
            }
            catch (Exception exception)
            {
                SqlAssistDiagnostics.Write($"預先載入 {objectInfo.QualifiedName} 的結構失敗：{exception.Message}");
            }
            finally
            {
                lock (_syncRoot)
                {
                    _warmingDetails.Remove(objectInfo.ObjectId);
                }
            }
        });
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

    /// <summary>只看第四層快取裡有沒有這個物件的結構；沒有就回傳 null，不觸發查詢。</summary>
    public SqlObjectStructure? PeekStructure(SqlObjectInfo objectInfo)
    {
        if (objectInfo is null)
        {
            return null;
        }

        var catalog = PeekCatalog();

        return catalog is not null && catalog.TryGetCachedStructure(objectInfo.ObjectId, out var structure)
            ? structure
            : null;
    }

    /// <summary>清掉單一物件的明細與結構快取，下一次要求會重新查詢。</summary>
    public void InvalidateObject(SqlObjectInfo objectInfo)
    {
        if (objectInfo is not null)
        {
            PeekCatalog()?.InvalidateObject(objectInfo.ObjectId);
        }
    }

    /// <summary>
    /// 載入單一物件的完整結構，含索引與外來鍵。
    /// </summary>
    /// <remarks>
    /// 只有結構面板會走到這裡，允許等資料庫；連線還沒解析出來時也願意問一次 SSMS。
    /// </remarks>
    public async Task<SqlObjectStructure?> GetStructureAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken)
    {
        var catalog = ResolveCatalog();

        if (catalog is null)
        {
            return null;
        }

        var timer = Stopwatch.StartNew();
        var structure = await catalog.GetStructureAsync(objectInfo, cancellationToken).ConfigureAwait(false);
        ReportIfSlow($"物件結構 {objectInfo.QualifiedName}（第四層）", timer);
        return structure;
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
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return null;
            }

            // 已經知道連到哪裡就直接用，過一段時間再到背景去確認有沒有換過。
            // 這條路徑在每一次按鍵上，絕不能等 SSMS 的 UI 執行緒。
            if (_catalog is not null)
            {
                if (DateTimeOffset.UtcNow - _catalogCheckedAt >= CatalogRecheckInterval)
                {
                    BeginCatalogRecheck();
                }

                return _catalog;
            }
        }

        return ResolveCatalogFromEditor();
    }

    /// <summary>
    /// 只取已經解析好的目錄，絕不向 SSMS 詢問目前連線。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留提示會在滑鼠掃過每一個識別字時觸發，而
    /// <c>GetCurrentConnection()</c> 有 UI 執行緒相依性，SSMS 忙的時候實測要 1908 ms。
    /// 提示晚一輪出現沒有代價，讓 UI 執行緒排隊則會直接反映成打字延遲，
    /// 所以這條路徑寧可放棄這一次，順手在背景解析，下一次滑鼠停留就有了。
    /// </remarks>
    private SqlMetadataCatalog? PeekCatalog()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return null;
            }

            if (_catalog is not null)
            {
                if (DateTimeOffset.UtcNow - _catalogCheckedAt >= CatalogRecheckInterval)
                {
                    BeginCatalogRecheck();
                }

                return _catalog;
            }
        }

        BeginCatalogRecheck();
        return null;
    }

    /// <summary>在背景重新確認連線，結果留給下一次按鍵使用。</summary>
    private void BeginCatalogRecheck()
    {
        if (Interlocked.Exchange(ref _recheckInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                ResolveCatalogFromEditor();
            }
            catch (Exception exception)
            {
                SqlAssistDiagnostics.Write($"重新確認連線失敗：{exception.Message}");
            }
            finally
            {
                Volatile.Write(ref _recheckInFlight, 0);
            }
        });
    }

    /// <summary>
    /// 主動預熱：在編輯器剛建立、SSMS 還不忙的時候先問一次連線。
    /// </summary>
    /// <remarks>
    /// 沒有預熱的話，第一次按鍵仍然要付一次完整的連線解析成本。
    /// </remarks>
    public void BeginWarmup()
    {
        _ = Task.Run(() =>
        {
            try
            {
                ResolveCatalog();
            }
            catch (Exception exception)
            {
                SqlAssistDiagnostics.Write($"預熱連線失敗：{exception.Message}");
            }
        });
    }

    private SqlMetadataCatalog? ResolveCatalogFromEditor()
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
            _catalogCheckedAt = DateTimeOffset.UtcNow;

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
        SqlObjectDetail detail,
        SqlAssistSettings settings)
    {
        var suggestions = new List<SqlSuggestion>(detail.Columns.Count);

        foreach (var column in detail.Columns)
        {
            suggestions.Add(BuildColumnSuggestion(info, column, settings, qualifier: null));
        }

        return suggestions;
    }

    /// <param name="qualifier">
    /// 插入時要補在欄位前面的別名或資料表名稱；不需要限定時為 null。
    /// </param>
    private static SqlSuggestion BuildColumnSuggestion(
        SqlObjectInfo info,
        SqlColumnInfo column,
        SqlAssistSettings settings,
        string? qualifier)
    {
        var annotations = column.IsPrimaryKey ? " · PK" : string.Empty;
        var source = qualifier is null ? string.Empty : $" · {qualifier}";
        var name = Quote(column.Name, settings);
        var insertionText = qualifier is null ? name : Quote(qualifier, settings) + "." + name;

        return new SqlSuggestion(
            column.Name,
            insertionText,
            $"{column.DataType}{(column.IsNullable ? " NULL" : " NOT NULL")}{annotations}{source}",
            $"{info.QualifiedName}\r\n{column.ToScriptLine()}",
            SuggestionKind.Column,
            schemaName: info.SchemaName,
            tag: column);
    }

    private static string Quote(string name, SqlAssistSettings settings)
    {
        return settings.Suggestions.UseSquareBrackets
            ? SqlIdentifier.Quote(name)
            : SqlIdentifier.QuoteIfNeeded(name);
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
