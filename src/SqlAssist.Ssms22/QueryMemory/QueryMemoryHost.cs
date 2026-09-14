using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Core.Settings;
using SqlAssist.QueryMemory.Hosting;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.QueryMemory;

/// <summary>
/// Query Memory 在 SSMS 裡的生命週期：隔離儲存、背景寫入、Session 心跳與維護排程。
/// </summary>
/// <remarks>
/// 全部由設定驅動，預設是關的——擷取的是使用者輸入的 SQL，沒有他明確打開就不該存。
/// 關掉時不只停止擷取，連背景整理都不跑：那也是在動使用者的資料。
///
/// 心跳與維護各有計時器與重入旗標，互不等待；宿主閘門只包開啟、換設定與關閉。
///
/// 計時器刻意不在 UI 執行緒上：維護只呼叫隔離 repository，不碰編輯器緩衝區，
/// 排在 UI 執行緒上等於讓清理與打字搶同一條執行緒。宿主閒置由
/// <see cref="NoteEdit"/> 記下的最後一次編輯推得，不另外註冊殼層的閒置回呼——
/// 這裡要的「閒置」就是使用者沒在打字，不是殼層的訊息佇列空了。
/// </remarks>
internal static class QueryMemoryHost
{
    /// <summary>排程的最小解析度；真正的間隔由設定決定，這只是問「到了沒」的頻率。</summary>
    private static readonly TimeSpan TimerPeriod = TimeSpan.FromSeconds(30);

    /// <summary>使用者多久沒有編輯才算閒置。比維護間隔短得多，否則閒置提前永遠用不到。</summary>
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);

    /// <summary>心跳間隔；遠短於租約期限，一次排程延遲不該讓別人看到過期租約。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan LeaseExpiry = TimeSpan.FromMinutes(10);

    /// <summary>啟動後先讓 SSMS 自己開完；維護不跟開窗搶 I/O。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    /// <summary>佇列上限；文字位元組是估計值，不是程序記憶體硬上限。</summary>
    private const int MaximumPendingCaptures = 64;

    private const long MaximumPendingTextBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 關閉 SSMS 時排空與卸載的總時限。逾時就放棄剩下的擷取，不讓殼層卡在關閉；
    /// 強制結束本來就可能遺失未落盤的內容。
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private static readonly object SyncRoot = new();

    /// <summary>只保護開啟、換設定與關閉這些狀態轉換；讀取、心跳與維護都不經過它。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static SqlAssistPackage? _package;
    private static CancellationTokenSource? _lifetime;
    private static Timer? _maintenanceTimer;
    private static Timer? _heartbeatTimer;
    private static State? _state;
    private static long _lastEditTicks;
    private static int _maintaining;
    private static int _beating;
    private static bool _initialized;
    private static int _queueFullReported;
    private static int _busyReported;
    private static string _status = "SQL Memory 尚未啟用；請在設定中啟用。";
    private static long _generation;
    public static string Status => Volatile.Read(ref _status);
    public static long Generation => Interlocked.Read(ref _generation);
    public static bool IsAvailable => IsCapturing && SqlAssistSettingsStore.Current.Enabled && SqlAssistSettingsStore.Current.QueryMemoryEnabled;

    // 不經過宿主閘門：隔離 repository 自己保證卸載會等進行中的呼叫。關閉時取消這份狀態的
    // 生命週期，長時間的全文搜尋才不會拖住卸載。
    private static async Task<T> UseAsync<T>(Func<IsolatedQueryMemoryRepository, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var settings = SqlAssistSettingsStore.Current;
        if (!settings.Enabled || !settings.QueryMemoryEnabled || Volatile.Read(ref _state) is not { } state)
            throw new InvalidOperationException(Status);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Lifetime.Token);
        try
        {
            return await operation(state.Repository, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when ((error is OperationCanceledException or ObjectDisposedException) &&
            !cancellationToken.IsCancellationRequested && state.Lifetime.IsCancellationRequested)
        {
            // 呼叫端沒有取消，是儲存在途中被關閉或重開；世代已經換過，舊畫面不會採用這個結果。
            throw new InvalidOperationException("SQL Memory 已重新開啟或停用；請重新整理後再操作。");
        }
    }

    // 對視窗只提供 Core DTO，repository 不會逃離單一操作的範圍。
    public static Task<QueryMemoryPage<QueryHistoryItem>> ReadHistoryAsync(QueryHistoryRequest request, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadHistoryAsync(request, ct), token);
    public static Task<QueryMemoryPage<FavoriteQueryEntry>> ReadFavoriteQueriesAsync(FavoriteQueryRequest request, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadFavoriteQueriesAsync(request, ct), token);
    public static Task<string[]> ReadConnectionFacetsAsync(QueryConnectionFacetRequest request, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadConnectionFacetsAsync(request, ct), token);
    public static Task<QueryContent?> ReadContentAsync(string contentId, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadContentAsync(contentId, ct), token);
    public static Task<FavoriteQueryWriteResult> WriteFavoriteQueryAsync(FavoriteQueryWrite write, CancellationToken token) =>
        UseAsync((repository, ct) => repository.WriteFavoriteQueryAsync(write, ct), token);
    public static Task<FavoriteQueryWriteResult> EditFavoriteQuerySqlAsync(FavoriteQueryEdit edit, CancellationToken token) =>
        UseAsync((repository, ct) => repository.EditFavoriteQuerySqlAsync(edit, ct), token);
    public static Task<FavoriteQueryWriteResult> DeleteFavoriteQueryAsync(Guid id, Guid version, CancellationToken token) =>
        UseAsync((repository, ct) => repository.DeleteFavoriteQueryAsync(id, version, ct), token);

    /// <summary>目前是否真的在擷取；沒有接上儲存時一律 false，呼叫端不必自己判斷設定。</summary>
    public static bool IsCapturing => Volatile.Read(ref _state) is not null;

    /// <summary>停止輸入多久之後記下草稿。</summary>
    public static TimeSpan IdleDebounce =>
        TimeSpan.FromSeconds(SqlAssistSettingsStore.Current.QueryMemoryIdleSeconds);

    /// <summary>只在 UI 執行緒呼叫；重複呼叫只有第一次接線。</summary>
    public static void Initialize(SqlAssistPackage package)
    {
        if (package is null) return;

        lock (SyncRoot)
        {
            if (_initialized) return;
            _initialized = true;
            _package = package;
            _lifetime = new CancellationTokenSource();
            SqlAssistSettingsStore.Changed += OnSettingsChanged;
            // 沒有人接結果的背景工作一律走 Guard；維護失敗只代表下一輪重跑同一個游標。
            // 心跳另有計時器：慢的維護批次或手動整理不能讓本程序的租約看起來過期。
            _maintenanceTimer = new Timer(_ => SqlAssistPlatformGuard.BeginProbe("SQL Memory 維護排程", MaintainAsync),
                null, TimerPeriod, TimerPeriod);
            _heartbeatTimer = new Timer(_ => SqlAssistPlatformGuard.BeginProbe("SQL Memory 租約心跳", BeatAsync),
                null, TimerPeriod, TimerPeriod);
        }

        Apply();
    }

    /// <summary>套件卸載：先排空背景寫入器，再卸載隔離 AppDomain；總時間受 <see cref="ShutdownTimeout"/> 限制。</summary>
    public static void Shutdown()
    {
        Timer? maintenanceTimer;
        Timer? heartbeatTimer;
        CancellationTokenSource? lifetime;

        lock (SyncRoot)
        {
            if (!_initialized) return;
            _initialized = false;
            SqlAssistSettingsStore.Changed -= OnSettingsChanged;
            maintenanceTimer = _maintenanceTimer;
            _maintenanceTimer = null;
            heartbeatTimer = _heartbeatTimer;
            _heartbeatTimer = null;
            lifetime = _lifetime;
            _lifetime = null;
            _package = null;
        }

        maintenanceTimer?.Dispose();
        heartbeatTimer?.Dispose();
        lifetime?.Cancel();
        // 同步等：SQLite 檔案要在殼層收尾之前放開。等待有上限，逾時只記錄診斷，不讓 SSMS 卡在關閉。
        // 逾時時開啟流程可能還拿著 lifetime，不處置它，交給程序結束。
        if (CloseAsync(timeout: ShutdownTimeout).GetAwaiter().GetResult()) lifetime?.Dispose();
    }

    /// <summary>記下最後一次編輯；閒置提前維護只看這個值，不問殼層。</summary>
    public static void NoteEdit() => Interlocked.Exchange(ref _lastEditTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>把一次擷取交給背景寫入器。被拒絕時回報一次可見的降級訊息，不靜靜丟掉。</summary>
    public static QueryMemoryEnqueueResult TryEnqueue(QueryMemoryCapture capture)
    {
        var state = Volatile.Read(ref _state);
        if (state is null || capture is null) return QueryMemoryEnqueueResult.Stopped;

        var result = state.Writer.TryEnqueue(capture, state.Policy);
        if (result is QueryMemoryEnqueueResult.QueueFull or QueryMemoryEnqueueResult.SnapshotTooLarge)
        {
            ReportRejected(result);
        }

        return result;
    }

    /// <summary>設定頁的手動整理；重建整個資料庫，時間隨資料量成長，不進背景排程。</summary>
    /// <remarks>
    /// 先等本程序的 writer 排空，已接受的擷取不必在整理期間擱在記憶體裡。整理期間 writer 不停止：
    /// 新擷取照常排隊；VACUUM 持有 SQLite 寫鎖，提交等到 busy timeout 回報忙碌後由 processor 退避重試，
    /// 與其他程序整理時的情形相同。讀取與心跳不受影響。
    /// </remarks>
    public static async Task<QueryMemoryUsage> CompactAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) is { } state)
        {
            await state.Writer.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        }

        return await UseAsync((repository, token) => repository.CompactAsync(token), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void OnSettingsChanged(object? sender, EventArgs eventArgs) => Apply();

    /// <summary>設定改變時重新接線；開關與保留值在這裡收斂成一份不可變狀態。</summary>
    private static void Apply()
    {
        var settings = SqlAssistSettingsStore.Current;
        var wanted = settings.Enabled && settings.QueryMemoryEnabled;
        _status = wanted ? "正在開啟 SQL Memory…" : "SQL Memory 已停用；請在設定中啟用。";

        // 走 Begin 而不是 BeginProbe：開不起來就是使用者打開了設定卻什麼都沒記到，
        // 那要看得見，不是可有可無的探測。開檔與建立 AppDomain 都在這條背景路徑上。
        SqlAssistPlatformGuard.Begin(wanted ? "啟用 SQL Memory" : "停用 SQL Memory",
            () => wanted ? OpenOrUpdateAsync(settings) : (Task)CloseAsync(),
            NotificationKind.Package, NotificationOrigin.Ambient, NotificationLevel.Info,
            document: string.Empty);
    }

    private static async Task OpenOrUpdateAsync(SqlAssistSettings settings)
    {
        var lifetime = Volatile.Read(ref _lifetime);
        if (lifetime is null) return;

        try
        {
            await Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            var owner = QueryMemoryProcessProbe.CurrentOwner();
            if (_state is { } existing)
            {
                // 儲存庫不必重開；只換政策、排程與保留分級，游標跟著整條分級重來。
                _state = existing.With(settings, owner);
                _status = "";
                return;
            }

            var repository = await IsolatedQueryMemoryRepository
                .OpenAsync(DatabasePath(), AppDomain.CurrentDomain.BaseDirectory, lifetime.Token)
                .ConfigureAwait(false);

            try
            {
                // 開檔期間套件已經卸載：關閉可能已逾時放棄等待，這份儲存不能再掛上去。
                lifetime.Token.ThrowIfCancellationRequested();
                var state = State.Create(repository, settings, owner);
                ObserveWriter(state);
                _state = state;
                Interlocked.Increment(ref _generation);
                _status = "";
                SqlAssistDiagnostics.WriteAlways("SQL Memory 已啟用；擷取與背景整理開始運作。");
            }
            catch
            {
                repository.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // 狀態供工具窗顯示，例外仍交給既有啟用通知，不把失敗當成空清單。
            _status = "無法開啟 SQL Memory：" + error.Message;
            throw;
        }
        finally { Gate.Release(); }
    }

    /// <param name="failed">writer fault 時傳入；狀態已被換掉就什麼都不做。</param>
    /// <param name="timeout">總時限；null 表示等到完成。逾時只記錄診斷並放棄，不擲出。</param>
    /// <returns>是否在時限內完成關閉（含沒有東西要關）。</returns>
    private static async Task<bool> CloseAsync(State? failed = null, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        TimeSpan Remaining() => timeout is { } limit
            ? TimeSpan.FromTicks(Math.Max(0, (limit - elapsed.Elapsed).Ticks))
            : Timeout.InfiniteTimeSpan;

        // 閘門只在開啟或換設定時被占住；開檔卡住時關閉照樣受時限約束。
        if (!await Gate.WaitAsync(Remaining()).ConfigureAwait(false))
        {
            SqlAssistDiagnostics.WriteAlways("SQL Memory 關閉逾時：開啟流程尚未結束，放棄等待。");
            return false;
        }

        try
        {
            var state = _state;
            if (failed is not null && !ReferenceEquals(state?.Writer, failed.Writer)) return true;
            if (failed is not null) _status = "SQL Memory 寫入失敗，已停止擷取；請檢查診斷後重新啟用。";
            _state = null;
            Interlocked.Increment(ref _generation);
            if (state is null) return true;

            // 讀取、心跳與維護不必做完；取消後隔離層只剩 writer 已接受的擷取與提交中的交易要等。
            state.Lifetime.Cancel();

            // 排空之後才卸載：已接受的擷取必須先完成交易。
            var drain = state.Writer.CompleteAsync();
            if (!await CompletesWithinAsync(drain, Remaining()).ConfigureAwait(false))
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"SQL Memory 關閉逾時：放棄 {state.Writer.PendingCount} 筆尚未提交的擷取，未卸載隔離儲存。");
                return false;
            }

            try
            {
                await drain.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                SqlAssistDiagnostics.WriteAlways($"SQL Memory 排空時失敗：{error.Message}");
            }

            var dispose = Task.Run(state.Repository.Dispose);
            if (!await CompletesWithinAsync(dispose, Remaining()).ConfigureAwait(false))
            {
                SqlAssistDiagnostics.WriteAlways("SQL Memory 關閉逾時：仍有儲存操作進行中，未卸載隔離儲存。");
                return false;
            }

            await dispose.ConfigureAwait(false);

            SqlAssistDiagnostics.WriteAlways("SQL Memory 已停用；儲存已釋放。");
            return true;
        }
        finally { Gate.Release(); }
    }

    /// <summary>逾時不取消工作本身；呼叫端放棄等待，工作在背景自然結束或隨程序終止。</summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan || task.IsCompleted)
        {
            await Task.WhenAny(task).ConfigureAwait(false);
            return true;
        }

        using var delay = new CancellationTokenSource();
        var completed = await Task.WhenAny(task, Task.Delay(timeout, delay.Token)).ConfigureAwait(false);
        delay.Cancel();
        return ReferenceEquals(completed, task);
    }

    private static async Task MaintainAsync()
    {
        if (Interlocked.CompareExchange(ref _maintaining, 1, 0) != 0) return;

        var state = Volatile.Read(ref _state);
        try
        {
            if (state is null || state.Lifetime.IsCancellationRequested) return;

            var now = DateTimeOffset.UtcNow;
            var lastEdit = new DateTimeOffset(Interlocked.Read(ref _lastEditTicks), TimeSpan.Zero);
            var tick = await state.Runner
                .RunOnceAsync(now, now - lastEdit >= IdleThreshold, state.Heartbeat.LeaseId is not null, state.Lifetime.Token)
                .ConfigureAwait(false);

            if (tick.Outcome == QueryMemoryMaintenanceOutcome.Maintained && tick.Result is { } result)
            {
                SqlAssistDiagnostics.Write(
                    $"SQL Memory 維護：分級 {tick.Level} 檢查 {result.ExaminedCandidates} 刪除 {result.DeletedRows} " +
                    $"容量 {result.CapacityStatus} 釋放租約 {tick.ReleasedLeases} 截斷 {tick.Checkpointed}");
            }
        }
        catch (Exception error) when (IsClosing(error, state)) { }
        catch (Exception error)
        {
            // 維護失敗不讓擷取跟著停：下一輪重跑同一個游標即可。
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 維護失敗：{error.Message}");
        }
        finally { Interlocked.Exchange(ref _maintaining, 0); }
    }

    /// <summary>續約或重開租約；不等維護批次，也不和維護共用任何鎖。</summary>
    private static async Task BeatAsync()
    {
        if (Interlocked.CompareExchange(ref _beating, 1, 0) != 0) return;

        var state = Volatile.Read(ref _state);
        try
        {
            if (state is null || state.Lifetime.IsCancellationRequested) return;

            var now = DateTimeOffset.UtcNow;
            if (state.Heartbeat.IsDue(now))
            {
                await state.Heartbeat.BeatAsync(now, state.Lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (IsClosing(error, state)) { }
        catch (Exception error)
        {
            // 下一次計時器再試；租約期限遠長於心跳間隔，一次失敗不會讓別人看到過期。
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 租約心跳失敗：{error.Message}");
        }
        finally { Interlocked.Exchange(ref _beating, 0); }
    }

    /// <summary>關閉期間的取消與已卸載不是失敗；只有這份狀態真的被關掉時才靜默。</summary>
    private static bool IsClosing(Exception error, State? state) =>
        (error is OperationCanceledException or ObjectDisposedException) && state?.Lifetime.IsCancellationRequested == true;

    /// <summary>
    /// 寫入器 fault 之後不再接收；宿主必須察覺並停用，不能把排空當成保存成功。
    /// 忙碌不會走到這裡：writer 只在損毀、不相容或未知錯誤時 fault。
    /// </summary>
    private static void ObserveWriter(State state) => _ = state.Writer.Completion.ContinueWith(
        completion =>
        {
            if (!completion.IsFaulted) return;
            SqlAssistDiagnostics.WriteAlways(
                $"SQL Memory 背景寫入器已停止：{completion.Exception?.GetBaseException().Message}");
            SqlAssistPlatformGuard.Begin("釋放失敗的 SQL Memory 寫入器", () => CloseAsync(state),
                NotificationKind.Package, NotificationOrigin.Ambient, NotificationLevel.Info, document: string.Empty);
        },
        TaskScheduler.Default);

    /// <summary>儲存持續忙碌而放棄一筆擷取；writer 繼續運作，但這一筆沒有保存必須看得見。</summary>
    private static void ReportBusy(QueryMemoryCapture capture, QueryMemoryStorageException error)
    {
        SqlAssistDiagnostics.WriteAlways(
            $"SQL Memory 儲存持續忙碌，放棄擷取 {capture.Kind}（SQLite {error.ErrorCode}/{error.ExtendedErrorCode}）：{error.Message}");
        _status = "SQL Memory 資料庫忙碌中，有擷取未記錄。";

        // 忙碌通常一連串發生（例如另一個 SSMS 正在整理），狀態列只寫第一次。
        if (Interlocked.Exchange(ref _busyReported, 1) != 0) return;

        if (Volatile.Read(ref _package) is { } package)
        {
            SqlAssistStatusBar.Show(package, "SQL Memory 資料庫正忙，這一次沒有記錄；稍後的擷取會繼續保存。");
        }
    }

    private static void ReportRejected(QueryMemoryEnqueueResult result)
    {
        SqlAssistDiagnostics.WriteAlways("SQL Memory 拒絕擷取：" + result);
        _status = result == QueryMemoryEnqueueResult.SnapshotTooLarge
            ? "這份 SQL 太大，本次未記錄。" : "SQL Memory 佇列已滿，本次未記錄。";

        // 佇列滿是一連串的，狀態列只寫第一次；快照太大每次都值得說，那是使用者改得動的事。
        if (result == QueryMemoryEnqueueResult.QueueFull &&
            Interlocked.Exchange(ref _queueFullReported, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _package) is { } package)
        {
            SqlAssistStatusBar.Show(package, result == QueryMemoryEnqueueResult.SnapshotTooLarge
                ? "這份 SQL 太大，SQL Memory 這一次沒有記錄。"
                : "SQL Memory 正在忙，這一次的草稿沒有記錄。");
        }
    }

    private static string DatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlAssist.Ssms22", "SQLMemory", "SQLMemory.db");

    /// <summary>一份接好線的狀態；設定改變時整份換掉，呼叫端拿到的永遠是一致的一組。</summary>
    private sealed class State
    {
        private State(IsolatedQueryMemoryRepository repository, CancellationTokenSource lifetime, QueryMemoryBackgroundWriter writer,
            QueryMemoryLeaseHeartbeat heartbeat, QueryMemoryMaintenanceRunner runner, QueryMemoryPolicy policy)
        {
            Repository = repository;
            Lifetime = lifetime;
            Writer = writer;
            Heartbeat = heartbeat;
            Runner = runner;
            Policy = policy;
        }

        public IsolatedQueryMemoryRepository Repository { get; }

        /// <summary>
        /// 這個儲存庫的使用期間；關閉時取消讀取、心跳與維護，writer 不受影響。
        /// 刻意不處置：晚到的呼叫仍可能讀取 Token，而它沒有計時器或等待控制代碼要釋放。
        /// </summary>
        public CancellationTokenSource Lifetime { get; }

        public QueryMemoryBackgroundWriter Writer { get; }

        public QueryMemoryLeaseHeartbeat Heartbeat { get; }

        public QueryMemoryMaintenanceRunner Runner { get; }

        public QueryMemoryPolicy Policy { get; }

        public static State Create(IsolatedQueryMemoryRepository repository, SqlAssistSettings settings,
            QueryMemoryLeaseOwner owner)
        {
            var heartbeat = new QueryMemoryLeaseHeartbeat(repository, owner, HeartbeatInterval);
            // 租約明確隨每次提交傳入，不藏在 repository 欄位裡；心跳重開後的下一筆就標上新租約。
            var writer = new QueryMemoryBackgroundWriter(
                new QueryMemoryProcessor(repository, new QueryRevisionEngine(), leaseId: () => heartbeat.LeaseId),
                MaximumPendingCaptures, MaximumPendingTextBytes, ReportBusy);

            return new State(repository, new CancellationTokenSource(), writer, heartbeat,
                BuildRunner(repository, settings, owner), BuildPolicy(settings));
        }

        /// <summary>沿用同一個儲存庫、使用期間、寫入器與心跳，只換掉政策與整條保留分級。</summary>
        /// <remarks>進行中的維護批次仍用舊分級做完這一批；它與新分級各自有界，下一輪才用新的。</remarks>
        public State With(SqlAssistSettings settings, QueryMemoryLeaseOwner owner) =>
            new(Repository, Lifetime, Writer, Heartbeat, BuildRunner(Repository, settings, owner), BuildPolicy(settings));

        private static QueryMemoryPolicy BuildPolicy(SqlAssistSettings settings) => new(
            settings.QueryMemoryCaptureDrafts && settings.QueryMemoryRecoverUnsavedDrafts,
            settings.QueryMemoryCaptureDrafts,
            TimeSpan.FromMinutes(settings.QueryMemoryAutoRevisionMinutes),
            settings.QueryMemoryCaptureExecuted,
            settings.QueryMemoryCaptureDrafts);

        private static QueryMemoryMaintenanceRunner BuildRunner(IsolatedQueryMemoryRepository repository,
            SqlAssistSettings settings, QueryMemoryLeaseOwner owner)
        {
            var interval = TimeSpan.FromMinutes(settings.QueryMemoryMaintenanceMinutes);
            var schedule = new QueryMemoryMaintenanceSchedule(DateTimeOffset.UtcNow, StartupDelay, interval,
                // 還有工作時排近一點，但仍然是下一輪；閒置提前另有最小間隔，免得變成忙碌迴圈。
                TimeSpan.FromTicks(Math.Max(TimerPeriod.Ticks, interval.Ticks / 10)),
                TimeSpan.FromTicks(interval.Ticks / 4));

            // 沒有在保留未存檔草稿就不給期限：有期限才需要回收，沒有就完全不憑年齡刪。
            var unsaved = settings.QueryMemoryCaptureDrafts && settings.QueryMemoryRecoverUnsavedDrafts
                ? TimeSpan.FromDays(settings.QueryMemoryUnsavedDraftRetentionDays)
                : (TimeSpan?)null;

            var plan = new QueryMemoryRetentionPlan(
                TimeSpan.FromDays(settings.QueryMemoryDraftRetentionDays),
                TimeSpan.FromDays(settings.QueryMemoryExecutionRetentionDays),
                unsaved,
                QueryMemoryRetentionPlan.ToContentBytes(settings.QueryMemoryStorage),
                settings.QueryMemoryMaxExecutions,
                settings.QueryMemoryMaxSessionRevisions,
                settings.QueryMemoryMaxFavoriteRevisions);

            return new QueryMemoryMaintenanceRunner(repository, repository,
                new QueryMemoryLeaseReaper(Environment.MachineName, QueryMemoryProcessProbe.IsOwnerRunning),
                owner, schedule, plan, LeaseExpiry);
        }
    }
}
