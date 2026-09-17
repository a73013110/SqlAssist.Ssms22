using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using SqlAssist.Ssms22;
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
    private readonly HashSet<string> _warmingDetails = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _serviceProvider;
    private SqlMetadataCatalog? _catalog;

    /// <summary>上一次從編輯器連線算出的快取鍵，用來判斷連線或資料庫有沒有換過。</summary>
    private string? _editorCacheKey;

    /// <summary>上一次真的去問 SSMS 目前連線的時間。</summary>
    private DateTimeOffset _catalogCheckedAt;

    private int _recheckInFlight;

    /// <summary>使用者按過幾次「重新整理建議」。</summary>
    /// <remarks>
    /// 靜態的：那個命令是整個擴充一起清，而每個查詢視窗各有一份服務。
    /// 少了這一個計數，清空的只有快取內容——USE 換過資料庫之後按下重新整理，
    /// 重新載入的仍然是<b>舊資料庫</b>的物件，而那正是使用者按它的原因。
    /// </remarks>
    private static int _invalidationGeneration;

    /// <summary>這個編輯器已經照第幾次清空確認過連線。</summary>
    private int _confirmedGeneration;

    /// <summary>SSMS 說這個查詢視窗換過幾次連線。</summary>
    /// <remarks>
    /// 由 <see cref="SqlEditorConnectionWatcher"/> 在 UI 執行緒上加一。事件是
    /// 「該再問一次」的訊號，不是答案——真的去問 SSMS 留在背景路徑上。
    /// </remarks>
    private int _connectionEvents;

    /// <summary>這個編輯器已經照第幾次連線事件確認過。</summary>
    private int _confirmedConnectionEvents;

    /// <summary>這個查詢視窗在 SSMS 裡的識別；還沒取得過焦點時為 null。</summary>
    /// <remarks>
    /// 有它就問得到<b>這一個</b>視窗的連線；沒有就只能問作用中的那一個，
    /// 而使用者切到別的分頁時，那會把別的視窗的連線寫進這一份服務。
    /// </remarks>
    private string? _editorMoniker;

    /// <summary>正在進行的那一次重新確認；同時只留一份，其他呼叫端一起等它。</summary>
    private Task<SqlMetadataCatalog?>? _confirming;

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

    /// <remarks>
    /// 接線放在靜態建構函式而不是套件初始化：MEF 的補全元件不等 AsyncPackage
    /// 就會開始查詢，接晚了的症狀是最想看的那一次失敗剛好沒有留下紀錄。
    /// 這個型別是 Ssms22 進入中繼資料層的唯一入口，任何一條查詢都在它之後。
    ///
    /// 走 <see cref="SqlAssistDiagnostics.Write"/> 而不是 <c>WriteAlways</c>：
    /// 連線斷掉時每按一次鍵就失敗一次，平常一律不寫才留得住紀錄檔的訊噪比。
    /// 使用者看得到的入口是預覽裡那句「第四層查詢失敗」，它會把人帶去打開詳細記錄。
    /// 只寫訊息不寫堆疊——這裡要的是伺服器說了什麼，例如
    /// <c>Invalid column name 'uses_quoted_identifier'</c>。
    /// </remarks>
    static SqlMetadataService()
    {
        SqlMetadataFailure.Reporter = (operation, exception) =>
            SqlAssistDiagnostics.Write($"中繼資料查詢失敗：{operation}｜{exception.Message}");
    }

    public SqlMetadataService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>清空所有資料庫的快取，並讓每個編輯器重新確認自己連到哪裡。</summary>
    public static void InvalidateAll()
    {
        SqlMetadataCatalogRegistry.Default.InvalidateAll();
        Interlocked.Increment(ref _invalidationGeneration);
    }

    /// <summary>記下 SSMS 給這個查詢視窗的識別。</summary>
    /// <summary>這個查詢視窗在 SSMS 眼裡的識別；還沒取得時為 null。</summary>
    /// <remarks>SQL Memory 用它向 <c>SqlWindowConnections</c> 換出伺服器與資料庫名稱。</remarks>
    public string? EditorMoniker
    {
        get
        {
            lock (_syncRoot)
            {
                return _editorMoniker;
            }
        }
    }

    public void NoteEditorMoniker(string? editorMoniker)
    {
        lock (_syncRoot)
        {
            _editorMoniker = string.IsNullOrEmpty(editorMoniker) ? null : editorMoniker;
        }
    }

    /// <summary>SSMS 說有一個查詢視窗換了連線。</summary>
    /// <remarks>
    /// 認得出不是自己那個視窗就不理。認不出來（還沒取得識別，或事件沒指名視窗）
    /// 一律當成自己的：多問一次連線只是一次背景往返，錯過一次則是整份清單都錯。
    /// </remarks>
    public void NoteConnectionChanged(string? editorMoniker)
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            if (!string.IsNullOrEmpty(editorMoniker) &&
                _editorMoniker is { } mine &&
                !string.Equals(mine, editorMoniker, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Interlocked.Increment(ref _connectionEvents);
        }
    }

    /// <summary>現在連到哪個資料庫這件事需要重新確認。</summary>
    /// <remarks>
    /// 給按鍵路徑判斷「這一次能不能直接用快取回答」用，它自己不問 SSMS，
    /// 只比一次字串與一次時間。
    /// </remarks>
    public bool NeedsConnectionConfirmation
    {
        get
        {
            lock (_syncRoot)
            {
                return !_disposed && IsConfirmationDue();
            }
        }
    }

    /// <summary>
    /// 重新確認「現在連到哪個資料庫」，確認完才回來。
    /// </summary>
    /// <remarks>
    /// 目前資料庫會變，而本擴充只靠十秒一次的背景輪詢發現它——執行完
    /// <c>USE LibArchive</c> 之後的第一份清單因此仍然是舊資料庫的物件，
    /// 而畫面上完全看不出退過。這條路徑讓等得起的呼叫端（建議清單、Tab 展開、
    /// F12 都跑在背景工作上）先確認再回答：晚幾毫秒沒有代價，
    /// 拿另一個資料庫的同名物件回答則是看不出來的錯。
    ///
    /// 按鍵與滑鼠停留路徑一律不呼叫這裡，它們維持「這一輪沒有資料」的規則。
    /// 不必確認時連一個工作都不開。
    /// </remarks>
    public Task ConfirmConnectionAsync()
    {
        lock (_syncRoot)
        {
            if (_disposed || !IsConfirmationDue())
            {
                return Task.CompletedTask;
            }

            // 同一個編輯器的三個 MEF 元件會在同一輪各問一次，開三個工作等於
            // 對同一件事排三次 UI 執行緒往返。
            if (_confirming is { IsCompleted: false } running)
            {
                return running;
            }

            return _confirming = Task.Run(() => SqlAssistPlatformGuard.Probe<SqlMetadataCatalog?>(
                "重新確認目前連線",
                ResolveCatalogFromEditor,
                fallback: null));
        }
    }

    /// <summary>呼叫前必須握著 <see cref="_syncRoot"/>。</summary>
    private bool IsConfirmationDue()
    {
        // 四種換法收斂在這一個判斷上：三種都由 SSMS 的連線事件送來，第四種是
        // 使用者按重新整理。每一種各長一套判斷的話，漏掉的那一種會安靜地用舊
        // 資料庫的物件回答。
        return _catalog is null ||
            _confirmedGeneration != Volatile.Read(ref _invalidationGeneration) ||
            _confirmedConnectionEvents != Volatile.Read(ref _connectionEvents);
    }

    /// <summary>
    /// 取得目前資料庫的物件建議。回傳的建議只帶名稱層級的資訊，
    /// 欄位與定義要另外呼叫 <see cref="GetDetailAsync"/>。
    /// </summary>
    public Task<IReadOnlyList<SqlSuggestion>> GetSuggestionsAsync(
        CancellationToken cancellationToken)
    {
        return GetSuggestionsAsync(qualifierPath: null, cancellationToken);
    }

    /// <summary>
    /// 取得限定字所指位置的物件建議：沒有限定字或本地限定字用目前連線的資料庫，
    /// 跨資料庫則用同一台伺服器上的那一個。
    /// </summary>
    /// <remarks>
    /// 跨資料庫第一次一定要查一輪，而這條路徑跑在平台的背景工作上，不是按鍵路徑
    /// ——清單會晚一點出現，不會讓打字卡住。之後就與本地的目錄一樣命中快取。
    ///
    /// 只在<b>使用者真的打出資料庫名稱</b>之後才走到這裡。預先把每一個進得去的
    /// 資料庫都撈一份的話，共用主機上等於幾十輪查詢與幾十份常駐快照，
    /// 而其中九成九不會有人用到。
    /// </remarks>
    public async Task<IReadOnlyList<SqlSuggestion>> GetSuggestionsAsync(
        SqlObjectPath? qualifierPath,
        CancellationToken cancellationToken)
    {
        var catalog = ScopeTo(ResolveCatalog(), qualifierPath);

        if (catalog is null)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var timer = Stopwatch.StartNew();
        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // 跨伺服器那一條的耗時由對方決定，紀錄裡要看得出是哪一台——本機慢與
        // 對面慢的處理方式完全不同，混成同一行等於每次都要再問一次。
        if (qualifierPath is { IsCrossServer: true })
        {
            ReportIfSlow(
                $"連結伺服器建議 {qualifierPath.ServerName}.{qualifierPath.DatabaseName}（第一層）",
                timer);
        }
        else if (qualifierPath is { IsCrossDatabase: true })
        {
            ReportIfSlow($"跨資料庫建議 {qualifierPath.DatabaseName}（第一層）", timer);
        }

        // 連結伺服器只在目前這條連線的清單裡才對：往右走過任何一格之後，
        // 那一格的下一段不可能再是一台伺服器（T-SQL 沒有五段式名稱）。
        return BuildSuggestions(snapshot, includeLinkedServers: qualifierPath is null or { IsLocal: true });
    }

    /// <summary>
    /// 用目前這條連線的名單認出限定字，回傳重新對齊過的上下文。
    /// </summary>
    /// <remarks>
    /// 只看文字時 <c>dbo.</c>、<c>LibArchive.</c> 與 <c>SQL209.</c> 是同一個形狀，
    /// 建議清單、插入文字與目錄選擇卻要三種不同的答案。因此在<b>問清單之前</b>
    /// 先把上下文換成對齊過的那一個，後面三條路都讀同一份——各自再判一次的話，
    /// 症狀是清單列得出來、Tab 下去卻少一段。
    ///
    /// 讀的是本機第一層快照，也就是候選清單下一步無論如何都要載入的那一份，
    /// 所以這裡不會多送一輪查詢。刻意等它而不是只取已經快取的：查詢視窗剛開的
    /// 第一次補全還沒有快照，只取快取的症狀是「第一次沒有清單，再按一次才有」。
    ///
    /// 認不出來就維持右對齊的原判，也就是這個功能出現之前的行為。
    /// </remarks>
    public async Task<SqlCompletionContext> ResolveQualifierAsync(
        SqlCompletionContext context,
        CancellationToken cancellationToken)
    {
        // 限定字已經解析成別名時（u.），清單裡放的是欄位，那一段不是任何名稱空間。
        if (context.QualifierPath is not { } qualifier ||
            context.Target == CompletionTarget.Column)
        {
            return context;
        }

        if (ResolveCatalog() is not { } catalog)
        {
            return context;
        }

        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var resolved = SqlQualifierResolver.Resolve(qualifier, snapshot);

        return ReferenceEquals(resolved, qualifier) ? context : context.WithQualifierPath(resolved);
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
        SqlObjectPath? qualifierPath,
        CancellationToken cancellationToken)
    {
        if (ScopeTo(ResolveCatalog(), qualifierPath) is not { } catalog)
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

    /// <summary>
    /// 取得 <c>COLLATE</c> 之後的定序建議。
    /// </summary>
    /// <remarks>
    /// 一律問查詢視窗自己那條連線的目錄，不跟著限定字換——定序屬於<b>執行個體</b>，
    /// 而使用者正在編輯的這份指令碼跑在那條連線上。跨資料庫或跨伺服器的目錄
    /// 回答的是別台機器支援什麼，選中的名稱在這裡可能根本不存在。
    ///
    /// 目前資料庫的那一個換成 <see cref="SuggestionKind.CollationInUse"/>：五千多個
    /// 名稱長得幾乎一樣，模糊比對撈回來的順序沒有意義，而使用者要的幾乎一定是它。
    ///
    /// 查不到時回傳空清單。那個位置不會因此空掉——<c>DATABASE_DEFAULT</c> 與
    /// 這份指令碼已經寫過的定序都不必問伺服器，由呼叫端補上。
    /// </remarks>
    /// <param name="exclude">呼叫端已經放進清單的名稱；同一個名稱不列第二次。</param>
    public async Task<IReadOnlyList<SqlSuggestion>> GetCollationSuggestionsAsync(
        ISet<string> exclude,
        CancellationToken cancellationToken)
    {
        if (exclude is null)
        {
            throw new ArgumentNullException(nameof(exclude));
        }

        if (ResolveCatalog() is not { } catalog)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var timer = Stopwatch.StartNew();
        var collations = await catalog.GetCollationsAsync(cancellationToken).ConfigureAwait(false);
        var suggestions = new List<SqlSuggestion>(collations.Names.Count + 1);

        // 名單查不到而資料庫的定序查得到時仍然列出它：那一個正是使用者最常要的。
        if (collations.DatabaseCollation is { Length: > 0 } database && !exclude.Contains(database))
        {
            exclude.Add(database);
            suggestions.Add(CreateCollation(database, isDatabaseDefault: true));
        }

        foreach (var name in collations.Names)
        {
            if (!exclude.Contains(name))
            {
                suggestions.Add(CreateCollation(name, isDatabaseDefault: false));
            }
        }

        ReportIfSlow($"定序建議（{suggestions.Count} 筆）", timer);
        return suggestions;
    }

    private static SqlSuggestion CreateCollation(string name, bool isDatabaseDefault)
    {
        var description = isDatabaseDefault ? "目前資料庫的定序" : "定序";

        return new SqlSuggestion(
            name,
            name,
            description,
            description,
            isDatabaseDefault ? SuggestionKind.CollationInUse : SuggestionKind.Collation);
    }

    /// <summary>取得目前資料庫的第一層中繼資料；沒有可用連線時回傳 null。</summary>
    public Task<SqlDatabaseSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        return GetSnapshotAsync(path: null, cancellationToken);
    }

    /// <summary>取得路徑指名的那個資料庫的第一層中繼資料；查不到時回傳 null。</summary>
    public async Task<SqlDatabaseSnapshot?> GetSnapshotAsync(
        SqlObjectPath? path,
        CancellationToken cancellationToken)
    {
        var catalog = ScopeTo(ResolveCatalog(), path);

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
                    suggestions.Add(BuildScriptColumnSuggestion(
                        name,
                        settings,
                        qualifier: null,
                        source.SourceName));
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
        var matches = await catalog
            .FindObjectsAsync(module.ObjectName, module.SchemaName, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0 || !matches[0].Kind.IsExecutable())
        {
            return Array.Empty<SqlSuggestion>();
        }

        var detail = await catalog
            .GetDetailAsync(matches[0], cancellationToken, NotificationOrigin.Typing)
            .ConfigureAwait(false);

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
        var catalog = ScopeTo(PeekCatalog(), table?.Path);
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

        var catalog = ScopeTo(ResolveCatalog(), table.Path);

        if (catalog is null)
        {
            return null;
        }

        // 走目錄那一支而不是自己比對快照：sys.triggers 這一類名稱的答案不在第一層，
        // 而那一份只有被指名時才載入。
        var matches = await catalog
            .FindObjectsAsync(table.ObjectName, table.SchemaName, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return null;
        }

        var cached = catalog.TryGetCachedDetail(matches[0].ObjectId, out _);
        var detail = await catalog
            .GetDetailAsync(matches[0], cancellationToken, NotificationOrigin.Typing)
            .ConfigureAwait(false);
        return new ResolvedTable(matches[0], detail, cached);
    }

    /// <summary>
    /// 同一套解析規則的唯讀版本，只認快取裡現成的明細。
    /// </summary>
    /// <remarks>快照由呼叫端傳進來：敘述裡有好幾個資料來源時，那一份要重複用。</remarks>
    private static bool TryPeekResolved(
        SqlMetadataCatalog catalog,
        SqlDatabaseSnapshot snapshot,
        SqlTableReference? table,
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
                        qualify ? source.Qualifier : null,
                        source.SourceName));
                }

                continue;
            }

            // 目錄與第一層快照只在真的有資料表來源時才解析：一份全是子查詢的敘述
            // 不必為了列欄位去碰連線。
            catalog ??= ResolveCatalog();

            // 敘述可以同時 JOIN 本地與跨資料庫的表，所以快照要跟著來源走，
            // 不能整段共用一份——共用的症狀是跨資料庫那一張比對到本地的同名表。
            var sourceCatalog = ScopeTo(catalog, source.Table!.Path);
            var sourceSnapshot = ReferenceEquals(sourceCatalog, catalog)
                ? snapshot ??= catalog?.CachedSnapshot
                : sourceCatalog?.CachedSnapshot;

            if (sourceCatalog is null || sourceSnapshot is null || sourceSnapshot.IsEmpty)
            {
                continue;
            }

            if (!TryPeekResolved(sourceCatalog, sourceSnapshot, source.Table!, out var objectInfo, out var detail))
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
    /// <remarks>只回傳現成資料；缺少或過期時排程背景預載，不在呼叫端等待查詢或解析連線。</remarks>
    public SqlDatabaseSnapshot? PeekSnapshot(SqlObjectPath? path = null)
    {
        var catalog = ScopeTo(PeekCatalog(), path);
        var snapshot = catalog?.CachedSnapshot;
        if (catalog is not null)
        {
            SqlAssistPlatformGuard.BeginProbe("預載物件清單", catalog.WarmSnapshotAsync);
        }

        return snapshot is null || snapshot.IsEmpty ? null : snapshot;
    }

    /// <summary>只看第二層快取裡有沒有這個物件的明細；沒有就回傳 null，不觸發查詢。</summary>
    public SqlObjectDetail? PeekDetail(SqlObjectInfo objectInfo)
    {
        if (objectInfo is null)
        {
            return null;
        }

        var catalog = ScopeTo(PeekCatalog(), objectInfo);

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

        var catalog = ScopeTo(PeekCatalog(), objectInfo);

        if (catalog is null || catalog.TryGetCachedDetail(objectInfo.ObjectId, out _))
        {
            return;
        }

        // object_id 只在自己那個資料庫裡唯一，所以在途的鍵要帶上資料庫；
        // 只用編號的話，兩個資料庫裡剛好同號的物件會互相把對方的預熱擋掉。
        // 伺服器也要進鍵：object_id 跨到別台之後同號的物件毫無關係，
        // 少了它會讓兩個不同物件的預熱互相抵銷成一次。
        var warmingKey = objectInfo.ServerName + "|" +
                         objectInfo.DatabaseName + "|" +
                         objectInfo.ObjectId;

        lock (_syncRoot)
        {
            if (!_warmingDetails.Add(warmingKey))
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
                    await catalog
                        .GetDetailAsync(objectInfo, CancellationToken.None, NotificationOrigin.Ambient)
                        .ConfigureAwait(false);
                    SqlAssistDiagnostics.Write(
                        $"已預先載入 {objectInfo.QualifiedName} 的結構（{timer.ElapsedMilliseconds} ms）");
                }
                finally
                {
                    lock (_syncRoot)
                    {
                        _warmingDetails.Remove(warmingKey);
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
            var editorCatalog = ResolveCatalog();

            if (editorCatalog is null)
            {
                return;
            }

            foreach (var source in sources)
            {
                // 子查詢與 CTE 的欄位名稱已經從指令碼讀出來了，沒有什麼好預熱的。
                if (_disposed || source.Kind != SqlColumnSourceKind.Table)
                {
                    continue;
                }

                var table = source.Table!;
                var catalog = ScopeTo(editorCatalog, table.Path);

                if (catalog is null)
                {
                    continue;
                }

                // 目前這條連線的第一層不在這裡觸發：那是建議清單自己的工作，
                // 這裡只是背景加速，不該再排一輪同樣的查詢。
                //
                // 跨資料庫的目錄相反——沒有別人會去載它。敘述已經把那個資料庫的
                // 名字寫出來了（FROM LibArchive.dbo.Loan l），等於使用者指名要它；
                // 從前要等他真的打出那一整串限定字才載，症狀是 SET | 與 WHERE |
                // 這種沒有限定字的位置永遠列不出跨庫來源的欄位，而同一份欄位
                // 打出 l. 就有。載一次就進快取，重複與失敗退避由這一支自己擋。
                if (!catalog.IsSnapshotFresh)
                {
                    if (ReferenceEquals(catalog, editorCatalog))
                    {
                        continue;
                    }

                    await catalog.WarmSnapshotAsync().ConfigureAwait(false);

                    if (!catalog.IsSnapshotFresh)
                    {
                        continue;
                    }
                }

                // 第一層在上面已經確認新鮮，這一支不會多送一輪查詢；但敘述寫出
                // sys.triggers 時它會把系統物件那一份載進來——只讀快取的
                // GetCachedScopeColumns 沒有別的機會等到它，症狀是 SELECT | 與
                // WHERE | 永遠列不出系統檢視的欄位，而打出 t. 卻列得出來。
                var matches = await catalog
                    .FindObjectsAsync(table.ObjectName, table.SchemaName, CancellationToken.None)
                    .ConfigureAwait(false);

                if (matches.Count == 0 || catalog.TryGetCachedDetail(matches[0].ObjectId, out _))
                {
                    continue;
                }

                var timer = Stopwatch.StartNew();
                await catalog
                    .GetDetailAsync(matches[0], CancellationToken.None, NotificationOrigin.Ambient)
                    .ConfigureAwait(false);
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
    /// <param name="origin">
    /// 誰觸發了這一次。同一條查詢在打字路徑與使用者按下去的命令上要有不同的降噪門檻，
    /// 而目錄那一層看不出差別——只有呼叫端知道。
    /// </param>
    public async Task<SqlObjectDetail?> GetDetailAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken,
        NotificationOrigin origin)
    {
        var catalog = ScopeTo(ResolveCatalog(), objectInfo);

        if (catalog is null || objectInfo is null)
        {
            return null;
        }

        return await catalog.GetDetailAsync(objectInfo, cancellationToken, origin).ConfigureAwait(false);
    }

    /// <summary>只看第四層快取裡有沒有這個物件的結構；沒有就回傳 null，不觸發查詢。</summary>
    public SqlObjectStructure? PeekStructure(SqlObjectInfo objectInfo)
    {
        if (objectInfo is null)
        {
            return null;
        }

        var catalog = ScopeTo(PeekCatalog(), objectInfo);

        return catalog is not null && catalog.TryGetCachedStructure(objectInfo.ObjectId, out var structure)
            ? structure
            : null;
    }

    /// <summary>清掉單一物件的明細與結構快取，下一次要求會重新查詢。</summary>
    public void InvalidateObject(SqlObjectInfo objectInfo)
    {
        if (objectInfo is not null)
        {
            ScopeTo(PeekCatalog(), objectInfo)?.InvalidateObject(objectInfo.ObjectId);
        }
    }

    /// <summary>
    /// 載入單一物件的完整結構，含索引與外來鍵。
    /// </summary>
    /// <remarks>
    /// 只有結構面板會走到這裡，允許等資料庫；連線還沒解析出來時也願意問一次 SSMS。
    /// </remarks>
    /// <param name="origin"><inheritdoc cref="GetDetailAsync" path="/param[@name='origin']"/></param>
    public async Task<SqlObjectStructure?> GetStructureAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken,
        NotificationOrigin origin)
    {
        var catalog = ScopeTo(ResolveCatalog(), objectInfo);

        if (catalog is null || objectInfo is null)
        {
            return null;
        }

        var timer = Stopwatch.StartNew();
        var structure = await catalog.GetStructureAsync(objectInfo, cancellationToken, origin).ConfigureAwait(false);
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
            _catalog = null;
        }

        // 在鎖外面解除：watcher 拿自己的鎖，而它發通知時會反過來拿這裡這一個。
        SqlEditorConnectionWatcher.Unregister(this);
    }

    /// <summary>
    /// 把目錄換成路徑指名的那一台伺服器、那一個資料庫。
    /// </summary>
    /// <remarks>
    /// 沒有路徑、或路徑就在目前這條連線上時原樣回傳，所以呼叫端不必自己分三種
    /// 情形——分開寫的症狀是某一條路徑忘了換，而它會安靜地拿本地同名的物件回答。
    ///
    /// 兩種「別的地方」在這裡收斂成同一個回傳型別：同一台伺服器的別的資料庫是
    /// 換連線，別台伺服器是換 SQL 的限定字。上面四層一行都不知道差別。
    /// </remarks>
    private SqlMetadataCatalog? ScopeTo(SqlMetadataCatalog? catalog, SqlObjectPath? path)
    {
        if (catalog is null || path is null || path.IsLocal)
        {
            return catalog;
        }

        return ScopeTo(catalog, path.DatabaseName, path.ServerName);
    }

    /// <summary>
    /// 把目錄換成這個物件自己記下的來源。
    /// </summary>
    /// <remarks>
    /// 每一條路徑都問物件，而不是各自從物件身上拆出資料庫與伺服器再傳進來：
    /// 拆的地方有六處，漏掉一處的症狀是那一條安靜地拿本機同號的物件回答，
    /// 而 <c>object_id</c> 撞號在跨資料庫是常態、跨伺服器更是毫無關係。
    ///
    /// 指令碼自己宣告的名稱在這裡一律沒有目錄可換。它們不在任何一個
    /// <c>sys.objects</c> 裡，<c>object_id</c> 也一律是 0——而第二、三層快取正是
    /// 照編號存的，放行的症狀是兩個不同的暫存資料表互相蓋掉對方的欄位，
    /// 外加每一次都白跑一趟查不到東西的查詢。它們的明細由
    /// <see cref="SqlObjectLookup"/> 直接讀出來，本來就不必經過這一層。
    /// </remarks>
    private SqlMetadataCatalog? ScopeTo(SqlMetadataCatalog? catalog, SqlObjectInfo? objectInfo)
    {
        if (objectInfo is null)
        {
            return catalog;
        }

        return objectInfo.Kind.IsScriptDeclared()
            ? null
            : ScopeTo(catalog, objectInfo.DatabaseName, objectInfo.ServerName);
    }

    /// <summary>把目錄換成指定的伺服器與資料庫；兩者都沒指定時原樣回傳。</summary>
    private SqlMetadataCatalog? ScopeTo(
        SqlMetadataCatalog? catalog,
        string? databaseName,
        string? serverName = null)
    {
        if (catalog is null || (string.IsNullOrEmpty(databaseName) && string.IsNullOrEmpty(serverName)))
        {
            return catalog;
        }

        // 連線來源一律從手上這份目錄取，不另外留一份：所有權在註冊表，
        // 交出去的那一份可能已經被當成重複的釋放掉，理由見
        // SqlMetadataCatalog.ConnectionSource。
        var source = catalog.ConnectionSource;

        if (!string.IsNullOrEmpty(serverName))
        {
            return SqlMetadataCatalogRegistry.Default.GetOrCreateFor(source, serverName!, databaseName);
        }

        // 與查詢視窗自己那個資料庫同名時就是同一份目錄。這一條讓 SqlObjectInfo
        // 可以無條件記下自己的資料庫，而不必分「本地」與「跨庫」兩種寫法——
        // 分兩種的症狀是某一條路徑忘了標，於是拿本地同號的物件回答。
        return string.Equals(source.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase)
            ? catalog
            : SqlMetadataCatalogRegistry.Default.GetOrCreateFor(source, databaseName!);
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
                // 確認條件成立時不必等滿十秒：指令碼換過資料庫，或使用者按過
                // 重新整理。這兩條路不等結果，等得起的呼叫端走
                // ConfirmConnectionAsync。
                if (IsConfirmationDue() ||
                    DateTimeOffset.UtcNow - _catalogCheckedAt >= CatalogRecheckInterval)
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
                // 確認條件成立時不必等滿十秒：指令碼換過資料庫，或使用者按過
                // 重新整理。這兩條路不等結果，等得起的呼叫端走
                // ConfirmConnectionAsync。
                if (IsConfirmationDue() ||
                    DateTimeOffset.UtcNow - _catalogCheckedAt >= CatalogRecheckInterval)
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

        // BeginProbe 不整批追蹤——週期性探測反覆閃現提示比沒有提示更糟。這一則由
        // 這裡自己開：Ambient／Debug，只有把詳細度調到「詳細」的人才看得到。
        SqlAssistPlatformGuard.BeginProbe(NotificationCatalog.ReconfirmingConnection, () =>
        {
            using var notification = NotificationCenter.Default.Begin(NotificationCatalog.ReconfirmingConnection,
                NotificationKind.Package, NotificationOrigin.Ambient, NotificationLevel.Debug);
            try
            {
                var catalog = ResolveCatalogFromEditor();
                var settings = SqlAssistSettingsStore.Current;
                if (catalog is not null && settings.Enabled && settings.HoverEnabled)
                {
                    // 清單查詢有自己的載入閘；慢查詢不能把連線重新確認一起鎖住。
                    SqlAssistPlatformGuard.BeginProbe("預載物件清單", catalog.WarmSnapshotAsync);
                }
            }
            finally
            {
                Volatile.Write(ref _recheckInFlight, 0);
            }
        });
    }

    /// <summary>
    /// 主動預熱：在編輯器剛建立時解析連線並預載目前資料庫的物件名稱。
    /// </summary>
    /// <remarks>
    /// 直接貼上 SQL 的使用者不一定會觸發建議清單，Hover 不可依賴建議先替它載入資料。
    /// </remarks>
    public void BeginWarmup()
    {
        BeginCatalogRecheck();
    }

    /// <summary>
    /// 向 SSMS 取得<b>這一個</b>查詢視窗的連線。
    /// </summary>
    /// <remarks>
    /// <c>GetCurrentConnection()</c> 回答的是<b>目前作用中</b>那個視窗，而這條路徑
    /// 跑在背景工作上：使用者切到別的分頁時，它會把別的視窗的連線寫進這一份服務，
    /// 而畫面上看不出來——兩個視窗連到不同資料庫時，切回來的第一份清單就是別人的。
    /// 有識別就指名問，沒有才退回作用中的那一個。
    ///
    /// 指名問回 null 有兩種：這個視窗真的沒有連線（剛斷線），以及手上的識別過期
    /// （另存新檔會換掉它）。前者不能退回作用中視窗的連線——那正是這裡要消滅的
    /// 污染——所以問一次 SSMS 開著哪些查詢視窗，兩種分開處理。
    /// </remarks>
    private IDbConnection? ResolveEditorConnection(Stopwatch timer)
    {
        if (_serviceProvider.GetService(typeof(SSqlEditorService)) is not ISqlEditorService editorService)
        {
            return null;
        }

        string? moniker;

        lock (_syncRoot)
        {
            moniker = _editorMoniker;
        }

        if (moniker is null)
        {
            var active = editorService.GetCurrentConnection();
            ReportIfSlow("向 SSMS 取得目前連線", timer);
            return active;
        }

        var connection = editorService.GetConnectionForSpecificQueryEditor(moniker);
        ReportIfSlow("向 SSMS 取得這個查詢視窗的連線", timer);

        if (connection is not null || IsKnownEditor(editorService, moniker))
        {
            return connection;
        }

        SqlAssistDiagnostics.WriteAlways($"SSMS 不認得查詢視窗識別 {moniker}，改用作用中視窗的連線");

        lock (_syncRoot)
        {
            // 丟掉過期的識別，下一次取得焦點時 watcher 會換上新的。
            if (string.Equals(_editorMoniker, moniker, StringComparison.Ordinal))
            {
                _editorMoniker = null;
            }
        }

        return editorService.GetCurrentConnection();
    }

    /// <summary>SSMS 現在還開著這個識別指的查詢視窗嗎。</summary>
    private static bool IsKnownEditor(ISqlEditorService editorService, string moniker)
    {
        var editors = editorService.ListOpenedQueryEditorCaptionsWithMonikers(false);

        if (editors is null)
        {
            return false;
        }

        foreach (var editor in editors)
        {
            if (string.Equals(editor.moniker, moniker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SqlMetadataCatalog? ResolveCatalogFromEditor()
    {
        var timer = Stopwatch.StartNew();

        // 先讀再問：問的途中使用者又按了一次重新整理、或 SSMS 又送來一次連線事件時，
        // 那一次不該被這一輪的答案蓋掉——下一次仍然要重新確認。
        var generation = Volatile.Read(ref _invalidationGeneration);
        var connectionEvents = Volatile.Read(ref _connectionEvents);

        var editorConnection = SqlAssistPlatformGuard.Run<IDbConnection?>(
            "向 SSMS 取得查詢視窗的連線",
            () => ResolveEditorConnection(timer),
            fallback: null);

        if (editorConnection is null)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return null;
                }

                // 記下這一次真的問過了。不記的話，斷線期間每一條等得起的路徑都會
                // 因為旗標還在而再排一次 UI 往返，而那個呼叫塞住時要 1908 ms。
                // 手上那份目錄留著不動：它是同一個資料庫的，只是現在查不動而已，
                // 丟掉等於重新連上之前連一個名稱都列不出來。
                _catalogCheckedAt = DateTimeOffset.UtcNow;
                _confirmedGeneration = generation;
                _confirmedConnectionEvents = connectionEvents;
            }

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
            _confirmedGeneration = generation;
            _confirmedConnectionEvents = connectionEvents;

            if (_catalog is not null && string.Equals(_editorCacheKey, cacheKey, StringComparison.Ordinal))
            {
                return _catalog;
            }

            _editorCacheKey = cacheKey;

            // 只有真的要換一份連線來源才開這一則：快取鍵沒變的那幾千次在上面就回去了。
            // 資料庫是「資料從哪裡來」而不是這件事作用的物件；放 Subject 會與中繼資料
            // 那幾列同一個字出現在兩種位置上。
            using var notification = NotificationCenter.Default.Begin(
                NotificationCatalog.CreatingMetadataConnection, NotificationKind.Package,
                NotificationOrigin.Ambient, NotificationLevel.Info, source: editorConnection.Database);
            var connectionSource = SsmsConnectionSource.TryCreate(editorConnection);

            if (connectionSource is null)
            {
                notification.Fail();
                _catalog = null;
                return null;
            }

            // 交出去之後就不再持有：註冊表已經有同一個快取鍵的目錄時，這一份會被
            // 當成重複的釋放掉，留著它等於留一個已釋放的物件。
            _catalog = SqlMetadataCatalogRegistry.Default.GetOrCreate(connectionSource);
            return _catalog;
        }
    }

    private static IReadOnlyList<SqlSuggestion> BuildSuggestions(
        SqlDatabaseSnapshot snapshot,
        bool includeLinkedServers = false)
    {
        // 結構描述讀擁有物件的那一份：與角色同名的空結構描述選了也接不到任何東西。
        // 完整名單留給 SqlQualifierResolver 認限定字。
        var schemas = snapshot.SchemasWithObjects;
        var suggestions = new List<SqlSuggestion>(
            snapshot.Objects.Count + schemas.Count + snapshot.Databases.Count);

        AddObjects(suggestions, snapshot.Objects);

        // 名稱的中間段一律只寫名稱本身：點號由使用者自己打，而打出點號會讓上下文
        // 整個換掉，重開清單那條路本來就會接手。連點號一起寫進去等於替使用者決定
        // 「你還要繼續往下走」，想直接用這個名稱的人得先退掉一個他沒要求的字元。
        foreach (var schema in schemas)
        {
            suggestions.Add(new SqlSuggestion(
                schema,
                schema,
                "Schema",
                $"Schema {SqlIdentifier.Quote(schema)}",
                SuggestionKind.Schema,
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

        if (includeLinkedServers)
        {
            foreach (var server in snapshot.LinkedServers)
            {
                // 名稱不保證是識別字的形狀——連結伺服器可以直接以位址命名，
                // 方括號由 SqlInsertionText 依與其他名稱同一條規則補。
                suggestions.Add(new SqlSuggestion(
                    server,
                    server,
                    "Linked server",
                    $"連結伺服器 {SqlIdentifier.QuoteIfNeeded(server)}",
                    SuggestionKind.LinkedServer));
            }
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
    /// 暫存資料表與資料表變數說得出出處，就寫它的名字：在
    /// <c>UPDATE #Loan SET |</c> 看到「查詢結果」會讓人以為認錯了東西。
    ///
    /// 欄位的排序刻意保留選取清單的順序，與資料表欄位保留定義順序同一個理由。
    /// </remarks>
    private static SqlSuggestion BuildScriptColumnSuggestion(
        string name,
        SqlAssistSettings settings,
        string? qualifier,
        string? sourceName)
    {
        var quoted = Quote(name, settings);
        var insertionText = qualifier is null ? quoted : Quote(qualifier, settings) + "." + quoted;
        var origin = sourceName ?? "查詢結果";
        var source = qualifier is null ? string.Empty : $" · {qualifier}";

        return new SqlSuggestion(
            name,
            insertionText,
            $"{origin}{source}",
            $"{origin}\r\n{name}",
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

            // 資料表值函式與純量函式分開：前者接得上 FROM、JOIN 與 APPLY，
            // 後者只出現在運算式位置。壓成同一類的症狀是 FROM 之後一個函式都不出現。
            SqlObjectKind.InlineTableFunction => SuggestionKind.TableFunction,
            SqlObjectKind.TableValuedFunction => SuggestionKind.TableFunction,
            SqlObjectKind.Trigger => SuggestionKind.Trigger,
            SqlObjectKind.Sequence => SuggestionKind.Sequence,
            SqlObjectKind.TableType => SuggestionKind.UserDefinedType,
            _ => null
        };
    }
}
