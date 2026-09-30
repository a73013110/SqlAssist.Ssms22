using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Completion;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 銜接 SSMS 的查詢視窗連線與中繼資料層。
/// </summary>
/// <remarks>
/// 只負責從 SSMS 取得目前連線，並在連線或資料庫改變時重建連線來源。
/// 實際的查詢、分層與快取都在 <see cref="SqlMetadataCatalog"/>；建議清單那一份
/// （物件、欄位與參數轉成 <see cref="SqlSuggestion"/>）在 <see cref="SqlCatalogCompletionMetadata"/>，
/// 與召回稽核共用。
/// </remarks>
internal sealed class SqlMetadataService : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly HashSet<string> _warmingDetails = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _serviceProvider;
    private readonly SqlCatalogCompletionMetadata _completion;
    private SqlMetadataCatalog? _catalog;

    /// <summary>上一次從編輯器連線算出的快取鍵，用來判斷連線或資料庫有沒有換過。</summary>
    private string? _editorCacheKey;

    /// <summary><see cref="_catalog"/> 連著的伺服器；與它同一次解析、在同一把鎖下寫入。</summary>
    private string _catalogServer = "";

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
        _completion = new SqlCatalogCompletionMetadata(
            ResolveCatalog,
            (operation, timer) => ReportIfSlow(operation, timer),
            message => SqlAssistDiagnostics.Write(message));
    }

    /// <summary>建議清單要問資料庫的那一份，接在這個查詢視窗的連線上。</summary>
    public ISqlCompletionMetadata Completion => _completion;

    /// <summary>
    /// 這個查詢視窗目前那條連線的目錄，以及它連著的伺服器；還沒解析出來時為 null，並在背景補上。
    /// </summary>
    /// <remarks>
    /// 給不在按鍵路徑上、等得起下一輪的呼叫端用（搜尋工具窗）。交出的是<b>目錄</b>不是連線來源：
    /// <c>ISqlConnectionSource</c> 的所有權在 <see cref="SqlMetadataCatalogRegistry"/>，
    /// 呼叫端要換資料庫時從 <c>catalog.ConnectionSource</c> 當場取，不留自己那一份。
    ///
    /// 伺服器與目錄<b>一起</b>交出，而且是同一次解析的結果：換連線之後、背景確認之前，
    /// 這裡的目錄仍是上一台的。伺服器另外去問 SSMS 的話，拿到的已經是新的那一台，
    /// 用上一台的目錄搜出來的每一筆都會標成新的那一台——在樹上找不到，移至定義還會沿用錯的連線。
    /// 要最新的那一份，先等 <see cref="ConfirmConnectionAsync"/>。
    /// </remarks>
    public (SqlMetadataCatalog Catalog, string Server)? PeekCurrentConnection()
    {
        // 鎖可重入：PeekCatalog 在同一條執行緒上再拿一次；背景確認換目錄時拿的也是這一把，
        // 所以交出去的兩個值一定是同一次解析的。
        lock (_syncRoot)
        {
            return PeekCatalog() is { } catalog ? (catalog, _catalogServer) : null;
        }
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
    /// 取得單一資料來源的欄位名稱，查不到時回傳 null。
    /// </summary>
    /// <remarks>
    /// 展開 <c>SELECT *</c> 用的。與欄位建議走同一條解析（<see cref="SqlResolvedTable"/>），
    /// 但只要名稱：展開後寫進編輯器的就只有名稱，型別與 PK 那些
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

        if (await SqlResolvedTable.ResolveAsync(ResolveCatalog(), table, cancellationToken).ConfigureAwait(false)
            is not { } resolved)
        {
            return null;
        }

        ReportIfSlow($"展開欄位 {resolved.Object.QualifiedName}（第二層）", timer);
        return ToColumnNames(resolved.Detail);
    }

    /// <summary>只看快取裡有沒有這個資料來源的欄位名稱；沒有就回傳 null，不觸發查詢。</summary>
    /// <remarks>
    /// 按下 Tab 的當下先問這裡：建議清單開過一次就已經把敘述裡的資料表預熱好了，
    /// 命中快取時展開是同一個交易裡的一次編輯，看起來就像按鍵直接改了文字。
    /// </remarks>
    public IReadOnlyList<string>? PeekColumnNames(SqlTableReference table)
    {
        return SqlResolvedTable.TryPeek(PeekCatalog(), table, out var resolved)
            ? ToColumnNames(resolved.Detail)
            : null;
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
    /// 在背景預先載入敘述中各資料來源的欄位；規則在 <see cref="SqlCatalogCompletionMetadata.WarmColumnsAsync"/>。
    /// </summary>
    public void WarmColumns(IReadOnlyList<SqlColumnSource> sources)
    {
        if (sources is null || sources.Count == 0 || _disposed)
        {
            return;
        }

        // 呼叫端在按鍵路徑上，一定要先離開它的執行緒再開始查。
        SqlAssistPlatformGuard.BeginProbe("預先載入欄位", () => Task.Run(
            () => _completion.WarmColumnsAsync(sources, CancellationToken.None)));
    }

    /// <summary>
    /// 超過門檻的操作一律記錄，不必先打開詳細診斷。
    /// </summary>
    /// <remarks>
    /// 建議清單的延遲只有在使用者遇到時才觀察得到，事後要求對方重現並開啟追蹤
    /// 才拿得到數字，等於白白浪費一次。慢的操作本來就少，直接記下來成本可以忽略。
    /// </remarks>
    private static void ReportIfSlow([Localizable(false)] string operation, Stopwatch timer, int thresholdMilliseconds = 200)
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

    /// <summary>把目錄換成限定字指名的那一台伺服器、那一個資料庫。</summary>
    private static SqlMetadataCatalog? ScopeTo(SqlMetadataCatalog? catalog, SqlObjectPath? path) =>
        SqlMetadataCatalogRegistry.Default.ScopeTo(catalog, path);

    /// <summary>
    /// 把目錄換成這個物件自己記下的來源。
    /// </summary>
    /// <remarks>
    /// 實作在 <see cref="SqlMetadataCatalogRegistry.ScopeTo(SqlMetadataCatalog?, SqlObjectInfo?)"/>，
    /// 這裡只是轉呼叫：指名別台伺服器的 SQL Search 手上只有一份目錄、沒有中繼資料服務，
    /// 兩邊各寫一份換目錄的規則，症狀是其中一份忘了換而答案看起來完全正常。
    /// 指令碼自己宣告的名稱在那裡一律換不到目錄，它們的明細由
    /// <see cref="SqlObjectLookup"/> 直接讀出來，本來就不必經過這一層。
    /// </remarks>
    private static SqlMetadataCatalog? ScopeTo(SqlMetadataCatalog? catalog, SqlObjectInfo? objectInfo) =>
        SqlMetadataCatalogRegistry.Default.ScopeTo(catalog, objectInfo);

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
            // 快取鍵含伺服器，所以上面那條沿用舊目錄的路不必重寫它。
            _catalogServer = SqlWindowConnections.ServerName(editorConnection.ConnectionString);

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
}
