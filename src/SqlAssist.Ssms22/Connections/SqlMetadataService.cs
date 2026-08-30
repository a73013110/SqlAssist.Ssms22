using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Connections;

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

    /// <summary>
    /// 取得 <c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 底下的系統物件建議。
    /// </summary>
    /// <remarks>
    /// 呼叫端必須先確認這個位置真的要它——這一份有一兩千筆，混進一般清單的話，
    /// 打第一個字元時真正要找的東西會被 <c>sp_</c> 開頭的名稱淹掉。
    /// 第一次被問到才查資料庫，之後整個工作階段都用快取。
    /// </remarks>
    public async Task<IReadOnlyList<SqlSuggestion>> GetSystemSuggestionsAsync(
        CancellationToken cancellationToken)
    {
        if (ResolveCatalog() is not { } catalog)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var timer = Stopwatch.StartNew();
        var objects = await catalog.GetSystemObjectsAsync(cancellationToken).ConfigureAwait(false);

        if (objects.Count == 0)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var suggestions = new List<SqlSuggestion>(objects.Count);
        AddObjects(suggestions, objects);
        ReportIfSlow($"系統物件建議（{suggestions.Count} 筆）", timer);
        return suggestions;
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
    /// 取得限定字所指資料來源的欄位建議。
    /// </summary>
    /// <remarks>
    /// 只在使用者真的輸入 <c>別名.</c> 時才觸發，因此會落在第二層按需載入：
    /// 一次只查一個物件的欄位，不會因為敘述裡有幾張資料表就全部撈回來。
    ///
    /// 插入的文字一律<b>不</b>補限定字：使用者已經自己打了 <c>a.</c>，
    /// 再補一次會變成 <c>a.a.欄位</c>。
    /// </remarks>
    /// <param name="includeDatabaseObjects">
    /// 關掉時不對資料庫送出任何查詢，只剩欄位名稱寫在指令碼裡的來源（子查詢、CTE）
    /// 列得出來。
    /// </param>
    public async Task<IReadOnlyList<SqlSuggestion>> GetColumnSuggestionsAsync(
        IReadOnlyList<SqlColumnSource> sources,
        bool includeDatabaseObjects,
        CancellationToken cancellationToken)
    {
        if (sources is null || sources.Count == 0)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var settings = SqlAssistSettingsStore.Current;
        var suggestions = new List<SqlSuggestion>();

        foreach (var source in sources)
        {
            if (source.Kind == SqlColumnSourceKind.Names)
            {
                foreach (var name in source.Names)
                {
                    suggestions.Add(BuildScriptColumnSuggestion(name, settings, qualifier: null));
                }

                continue;
            }

            if (!includeDatabaseObjects)
            {
                continue;
            }

            var total = Stopwatch.StartNew();

            if (await ResolveTableAsync(source.Table!, cancellationToken).ConfigureAwait(false) is not { } resolved)
            {
                continue;
            }

            ReportIfSlow(
                $"欄位建議 {resolved.Object.QualifiedName}" +
                $"（第二層{(resolved.DetailWasCached ? "命中快取" : "查詢資料庫")}）",
                total);

            if (resolved.Detail is not { Columns.Count: > 0 } detail)
            {
                continue;
            }

            foreach (var column in detail.Columns)
            {
                suggestions.Add(BuildColumnSuggestion(resolved.Object, column, settings, qualifier: null));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// 取得單一資料來源的欄位名稱，查不到時回傳 null。
    /// </summary>
    /// <remarks>
    /// 展開 <c>SELECT *</c> 用的。與 <see cref="GetColumnSuggestionsAsync"/> 走同一條
    /// 分層路徑，但只要名稱：展開後寫進編輯器的就只有名稱，型別與 PK 那些
    /// 是給建議清單看的。
    ///
    /// 回傳 null 與回傳空清單刻意分開：「查不到這個物件」必須讓呼叫端整個放棄，
    /// 展開成少了幾個欄位的 SELECT 比什麼都不做糟糕得多。
    /// </remarks>
    public async Task<IReadOnlyList<string>?> GetColumnNamesAsync(
        SqlTableReference table,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();

        if (await ResolveTableAsync(table, cancellationToken).ConfigureAwait(false) is not { } resolved)
        {
            return null;
        }

        ReportIfSlow($"展開欄位 {resolved.Object.QualifiedName}（第二層）", timer);
        return ToColumnNames(resolved.Detail);
    }

    /// <summary>
    /// 取得 <c>EXEC</c> 正在呼叫的那個模組的參數建議。
    /// </summary>
    /// <remarks>
    /// 與欄位建議走同一條分層路徑：使用者真的打出小老鼠時才查一個物件的第二層。
    /// 查不到、或那個名稱不是可執行的模組時回傳空清單而不是 null——這裡少列幾筆
    /// 只是少了補字，他自己的變數仍然照列。
    ///
    /// 插入文字連 <c> = </c> 一起寫進去：打出參數名稱就是要做具名傳值，
    /// 而 <c>EXEC p @readerId</c>（沒有等號）在文法上是照順序傳一個變數，
    /// 那是另一件事，由變數那一份負責。
    /// </remarks>
    public async Task<IReadOnlyList<SqlSuggestion>> GetParameterSuggestionsAsync(
        SqlExecutedModule module,
        bool includeDatabaseObjects,
        CancellationToken cancellationToken)
    {
        if (module is null || !includeDatabaseObjects || ResolveCatalog() is not { } catalog)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var timer = Stopwatch.StartNew();
        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var matches = snapshot.Find(module.ObjectName, module.SchemaName);

        if (matches.Count == 0 || !matches[0].Kind.IsExecutable())
        {
            return Array.Empty<SqlSuggestion>();
        }

        var detail = await catalog.GetDetailAsync(matches[0], cancellationToken).ConfigureAwait(false);

        ReportIfSlow($"參數建議 {matches[0].QualifiedName}（第二層）", timer);

        if (detail is not { Parameters.Count: > 0 })
        {
            return Array.Empty<SqlSuggestion>();
        }

        var suggestions = new List<SqlSuggestion>(detail.Parameters.Count);

        foreach (var parameter in detail.Parameters)
        {
            // 純量函式的傳回值也在這一份裡，它的名稱是空字串。
            if (parameter.Name.Length == 0)
            {
                continue;
            }

            suggestions.Add(new SqlSuggestion(
                parameter.Name,
                parameter.Name + " = ",
                parameter.IsOutput ? parameter.DataType + " OUTPUT" : parameter.DataType,
                $"{matches[0].QualifiedName} 的參數：{parameter.ToScriptLine()}",
                SuggestionKind.Parameter));
        }

        return suggestions;
    }

    /// <summary>只看快取裡有沒有這個資料來源的欄位名稱；沒有就回傳 null，不觸發查詢。</summary>
    /// <remarks>
    /// 按下 Tab 的當下先問這裡：建議清單開過一次就已經把敘述裡的資料表預熱好了，
    /// 命中快取時展開是同一個交易裡的一次編輯，看起來就像按鍵直接改了文字。
    /// </remarks>
    public IReadOnlyList<string>? PeekColumnNames(SqlTableReference table)
    {
        var catalog = PeekCatalog();
        var snapshot = catalog?.CachedSnapshot;

        if (catalog is null || snapshot is null || snapshot.IsEmpty)
        {
            return null;
        }

        return TryPeekResolved(catalog, snapshot, table, out _, out var detail)
            ? ToColumnNames(detail)
            : null;
    }

    /// <summary>解析出來的資料來源：物件本身、它的欄位明細，以及明細是不是現成的。</summary>
    private readonly struct ResolvedTable
    {
        public ResolvedTable(SqlObjectInfo objectInfo, SqlObjectDetail? detail, bool detailWasCached)
        {
            Object = objectInfo;
            Detail = detail;
            DetailWasCached = detailWasCached;
        }

        public SqlObjectInfo Object { get; }

        public SqlObjectDetail? Detail { get; }

        /// <summary>明細在這次要求之前就已經在快取裡；只影響診斷紀錄怎麼寫。</summary>
        public bool DetailWasCached { get; }
    }

    /// <summary>
    /// 把敘述裡的資料來源解析成物件與欄位明細，允許查詢資料庫。
    /// </summary>
    /// <remarks>
    /// 「同名物件取哪一個、衍生資料表不查」這些規則只能有一份：欄位建議與
    /// <c>SELECT *</c> 展開各自解析的話，同一個別名在兩個功能會指到不同的資料表。
    /// </remarks>
    private async Task<ResolvedTable?> ResolveTableAsync(
        SqlTableReference table,
        CancellationToken cancellationToken)
    {
        if (table is null || table.IsDerived)
        {
            return null;
        }

        var catalog = ResolveCatalog();

        if (catalog is null)
        {
            return null;
        }

        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var matches = snapshot.Find(table.ObjectName, table.SchemaName);

        if (matches.Count == 0)
        {
            return null;
        }

        var cached = catalog.TryGetCachedDetail(matches[0].ObjectId, out _);
        var detail = await catalog.GetDetailAsync(matches[0], cancellationToken).ConfigureAwait(false);
        return new ResolvedTable(matches[0], detail, cached);
    }

    /// <summary>
    /// 同一套解析規則的唯讀版本，只認快取裡現成的明細。
    /// </summary>
    /// <remarks>快照由呼叫端傳進來：敘述裡有好幾個資料來源時，那一份要重複用。</remarks>
    private static bool TryPeekResolved(
        SqlMetadataCatalog catalog,
        SqlDatabaseSnapshot snapshot,
        SqlTableReference table,
        out SqlObjectInfo objectInfo,
        out SqlObjectDetail detail)
    {
        objectInfo = null!;
        detail = null!;

        if (table is null || table.IsDerived)
        {
            return false;
        }

        var matches = snapshot.Find(table.ObjectName, table.SchemaName);

        if (matches.Count == 0 || !catalog.TryGetCachedDetail(matches[0].ObjectId, out detail))
        {
            return false;
        }

        objectInfo = matches[0];
        return true;
    }

    private static IReadOnlyList<string>? ToColumnNames(SqlObjectDetail? detail)
    {
        if (detail is null || detail.Columns.Count == 0)
        {
            return null;
        }

        var names = new string[detail.Columns.Count];

        for (var index = 0; index < names.Length; index++)
        {
            names[index] = detail.Columns[index].Name;
        }

        return names;
    }

    /// <summary>
    /// 取得敘述中所有資料來源的欄位，供沒有限定字的位置使用。
    /// </summary>
    /// <remarks>
    /// 資料表與檢視的欄位只回傳<b>已經在快取裡</b>的，絕不觸發查詢：這條路徑在
    /// 每一次按鍵上。沒命中就這一輪不顯示欄位，<see cref="WarmColumns"/> 會在背景
    /// 補上，下一次按鍵就有了。子查詢與 CTE 的欄位名稱寫在指令碼裡，不必等任何東西。
    ///
    /// 有兩個以上相異的限定字時，插入的文字會補上別名，否則
    /// <c>SELECT Name FROM A a JOIN B b</c> 這種寫法會因為欄位名稱模稜兩可而執行失敗。
    /// </remarks>
    public IReadOnlyList<SqlSuggestion> GetCachedScopeColumns(IReadOnlyList<SqlColumnSource> sources)
    {
        if (sources is null || sources.Count == 0 || _disposed)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var settings = SqlAssistSettingsStore.Current;
        var qualify = NeedsQualifier(sources);
        var suggestions = new List<SqlSuggestion>();
        SqlMetadataCatalog? catalog = null;
        SqlDatabaseSnapshot? snapshot = null;

        foreach (var source in sources)
        {
            if (source.Kind == SqlColumnSourceKind.Names)
            {
                foreach (var name in source.Names)
                {
                    suggestions.Add(BuildScriptColumnSuggestion(
                        name,
                        settings,
                        qualify ? source.Qualifier : null));
                }

                continue;
            }

            // 目錄與第一層快照只在真的有資料表來源時才解析：一份全是子查詢的敘述
            // 不必為了列欄位去碰連線。
            catalog ??= ResolveCatalog();
            snapshot ??= catalog?.CachedSnapshot;

            if (catalog is null || snapshot is null || snapshot.IsEmpty)
            {
                continue;
            }

            if (!TryPeekResolved(catalog, snapshot, source.Table!, out var objectInfo, out var detail))
            {
                continue;
            }

            foreach (var column in detail.Columns)
            {
                suggestions.Add(BuildColumnSuggestion(
                    objectInfo,
                    column,
                    settings,
                    qualify ? source.Qualifier : null));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// 插入的欄位名稱要不要補限定字。
    /// </summary>
    /// <remarks>
    /// 依據是<b>相異</b>的限定字數量而不是來源數量：<c>FROM (SELECT Id, * FROM T t) d</c>
    /// 攤平出兩個來源，但它們都叫 <c>d</c>，欄位名稱不可能因此模稜兩可。
    /// </remarks>
    private static bool NeedsQualifier(IReadOnlyList<SqlColumnSource> sources)
    {
        string? first = null;

        foreach (var source in sources)
        {
            if (source.Qualifier is null)
            {
                continue;
            }

            if (first is null)
            {
                first = source.Qualifier;
                continue;
            }

            if (!string.Equals(first, source.Qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        // 呼叫端在按鍵路徑上，一定要先離開它的執行緒再開始查。
        SqlAssistPlatformGuard.BeginProbe(
            $"預先載入 {objectInfo.QualifiedName} 的結構",
            () => Task.Run(async () =>
            {
                var timer = Stopwatch.StartNew();

                try
                {
                    await catalog.GetDetailAsync(objectInfo, CancellationToken.None).ConfigureAwait(false);
                    SqlAssistDiagnostics.Write(
                        $"已預先載入 {objectInfo.QualifiedName} 的結構（{timer.ElapsedMilliseconds} ms）");
                }
                finally
                {
                    lock (_syncRoot)
                    {
                        _warmingDetails.Remove(objectInfo.ObjectId);
                    }
                }
            }));
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
    public void WarmColumns(IReadOnlyList<SqlColumnSource> sources)
    {
        if (sources is null || sources.Count == 0 || _disposed)
        {
            return;
        }

        // 呼叫端在按鍵路徑上，一定要先離開它的執行緒再開始查。
        SqlAssistPlatformGuard.BeginProbe("預先載入欄位", () => Task.Run(async () =>
        {
            var catalog = ResolveCatalog();

            if (catalog is null || !catalog.IsSnapshotFresh)
            {
                return;
            }

            var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

            foreach (var source in sources)
            {
                // 子查詢與 CTE 的欄位名稱已經從指令碼讀出來了，沒有什麼好預熱的。
                if (_disposed || source.Kind != SqlColumnSourceKind.Table)
                {
                    continue;
                }

                var table = source.Table!;
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
        }));
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

        SqlAssistPlatformGuard.BeginProbe("重新確認連線", () =>
        {
            try
            {
                ResolveCatalogFromEditor();
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
        SqlAssistPlatformGuard.BeginProbe("預熱連線", () => _ = ResolveCatalog());
    }

    private SqlMetadataCatalog? ResolveCatalogFromEditor()
    {
        var timer = Stopwatch.StartNew();

        var editorConnection = SqlAssistPlatformGuard.Run<IDbConnection?>(
            "取得 SSMS 目前連線",
            () =>
            {
                var editorService =
                    _serviceProvider.GetService(typeof(SSqlEditorService)) as ISqlEditorService;
                var connection = editorService?.GetCurrentConnection();
                ReportIfSlow("向 SSMS 取得目前連線", timer);
                return connection;
            },
            fallback: null);

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
        var suggestions = new List<SqlSuggestion>(
            snapshot.Objects.Count + snapshot.Schemas.Count + snapshot.Databases.Count);

        AddObjects(suggestions, snapshot.Objects);

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

        foreach (var database in snapshot.Databases)
        {
            // 插入文字留空給 SqlInsertionText 依設定加括號：資料庫名稱與其他
            // 物件名稱適用同一條規則，含空白或連字號時一定會加，其餘看使用者偏好。
            suggestions.Add(new SqlSuggestion(
                database,
                database,
                "Database",
                $"USE {SqlIdentifier.QuoteIfNeeded(database)}",
                SuggestionKind.Database));
        }

        return suggestions;
    }

    /// <summary>
    /// 把只知道名稱的欄位轉成建議項。
    /// </summary>
    /// <remarks>
    /// 子查詢與 CTE 的輸出欄位寫在指令碼裡，型別、NULL 與 PK 都無從得知——
    /// 那些要追到最內層的資料表，而中間任何一段運算式都會讓答案不成立。
    /// 說明欄改寫來源本身：使用者要的是「這個名稱打不打得出來」。
    ///
    /// 欄位的排序刻意保留選取清單的順序，與資料表欄位保留定義順序同一個理由。
    /// </remarks>
    private static SqlSuggestion BuildScriptColumnSuggestion(
        string name,
        SqlAssistSettings settings,
        string? qualifier)
    {
        var quoted = Quote(name, settings);
        var insertionText = qualifier is null ? quoted : Quote(qualifier, settings) + "." + quoted;
        var source = qualifier is null ? string.Empty : $" · {qualifier}";

        return new SqlSuggestion(
            name,
            insertionText,
            $"查詢結果{source}",
            $"查詢結果\r\n{name}",
            SuggestionKind.Column);
    }

    /// <summary>
    /// 把中繼資料裡的欄位轉成建議項。
    /// </summary>
    /// <remarks>
    /// 呼叫端一律照資料表的定義順序逐欄呼叫，不重排：模糊比對的分數才是主要排名依據，
    /// 而分數相同時（例如還沒輸入任何字元）依序號排列比字母序更接近使用者的心智模型。
    /// </remarks>
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

    /// <remarks>
    /// 欄位的插入文字在這裡就定案，之後 <see cref="SqlInsertionText"/> 原樣送出，
    /// 所以括號規則必須共用同一份——各寫一份的下場是其中一份漏掉保留字。
    /// </remarks>
    private static string Quote(string name, SqlAssistSettings settings)
    {
        return SqlInsertionText.Quote(name, settings);
    }

    private static void AddObjects(List<SqlSuggestion> suggestions, IReadOnlyList<SqlObjectInfo> objects)
    {
        foreach (var info in objects)
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
            SqlObjectKind.Trigger => SuggestionKind.Trigger,
            SqlObjectKind.Sequence => SuggestionKind.Sequence,
            SqlObjectKind.TableType => SuggestionKind.UserDefinedType,
            _ => null
        };
    }
}
