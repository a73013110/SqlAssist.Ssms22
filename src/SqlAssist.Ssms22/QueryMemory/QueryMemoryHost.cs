using System;
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

    private static readonly object SyncRoot = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static SqlAssistPackage? _package;
    private static CancellationTokenSource? _lifetime;
    private static Timer? _timer;
    private static State? _state;
    private static long _lastEditTicks;
    private static int _ticking;
    private static bool _initialized;
    private static int _queueFullReported;
    private static string _status = "SQL Memory 尚未啟用；請在設定中啟用。";
    private static long _generation;
    public static string Status => Volatile.Read(ref _status);
    public static long Generation => Interlocked.Read(ref _generation);
    public static bool IsAvailable => IsCapturing && SqlAssistSettingsStore.Current.Enabled && SqlAssistSettingsStore.Current.QueryMemoryEnabled;

    // UI 與維護持有同一道閘門；卸載不能越過仍在隔離 AppDomain 內的呼叫。
    private static Task<T> UseAsync<T>(Func<IsolatedQueryMemoryRepository, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var expected = Volatile.Read(ref _state);
        return Task.Run(async () =>
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var settings = SqlAssistSettingsStore.Current;
                if (!settings.Enabled || !settings.QueryMemoryEnabled || _state is not { } state)
                    throw new InvalidOperationException(Status);
                if (!ReferenceEquals(expected?.Repository, state.Repository))
                    throw new InvalidOperationException("SQL Memory 已重新開啟；請重新整理後再操作。");
                return await operation(state.Repository, cancellationToken).ConfigureAwait(false);
            }
            finally { Gate.Release(); }
        }, cancellationToken);
    }

    // 對視窗只提供 Core DTO，repository 不會逃離持有閘門的操作範圍。
    public static Task<QueryMemoryPage<QueryHistoryItem>> ReadHistoryAsync(QueryHistoryRequest request, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadHistoryAsync(request, ct), token);
    public static Task<QueryMemoryPage<SavedQueryEntry>> ReadSavedQueriesAsync(SavedQueryRequest request, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadSavedQueriesAsync(request, ct), token);
    public static Task<string[]> ReadConnectionFacetsAsync(QueryConnectionFacetRequest request, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadConnectionFacetsAsync(request, ct), token);
    public static Task<QueryContent?> ReadContentAsync(string contentId, CancellationToken token) =>
        UseAsync((repository, ct) => repository.ReadContentAsync(contentId, ct), token);
    public static Task<SavedQueryWriteResult> WriteSavedQueryAsync(SavedQueryWrite write, CancellationToken token) =>
        UseAsync((repository, ct) => repository.WriteSavedQueryAsync(write, ct), token);
    public static Task<SavedQueryWriteResult> EditSavedQuerySqlAsync(SavedQueryEdit edit, CancellationToken token) =>
        UseAsync((repository, ct) => repository.EditSavedQuerySqlAsync(edit, ct), token);
    public static Task<SavedQueryWriteResult> DeleteSavedQueryAsync(Guid id, Guid version, CancellationToken token) =>
        UseAsync((repository, ct) => repository.DeleteSavedQueryAsync(id, version, ct), token);

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
            _timer = new Timer(_ => SqlAssistPlatformGuard.BeginProbe("SQL Memory 維護排程", TickAsync),
                null, TimerPeriod, TimerPeriod);
        }

        Apply();
    }

    /// <summary>套件卸載：先排空背景寫入器，再卸載隔離 AppDomain。</summary>
    public static void Shutdown()
    {
        Timer? timer;
        CancellationTokenSource? lifetime;

        lock (SyncRoot)
        {
            if (!_initialized) return;
            _initialized = false;
            SqlAssistSettingsStore.Changed -= OnSettingsChanged;
            timer = _timer;
            _timer = null;
            lifetime = _lifetime;
            _lifetime = null;
            _package = null;
        }

        timer?.Dispose();
        lifetime?.Cancel();
        // 同步等到釋放：SQLite 檔案要在殼層收尾之前放開，不能拖到處理程序結束才發生。
        CloseAsync().GetAwaiter().GetResult();
        lifetime?.Dispose();
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
    public static Task<QueryMemoryUsage> CompactAsync(CancellationToken cancellationToken) =>
        UseAsync((repository, token) => repository.CompactAsync(token), cancellationToken);

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
            () => wanted ? OpenOrUpdateAsync(settings) : CloseAsync(),
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

    private static async Task CloseAsync(State? failed = null)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        State? state;

        try
        {
            state = _state;
            if (failed is not null && !ReferenceEquals(state?.Writer, failed.Writer)) return;
            if (failed is not null) _status = "SQL Memory 寫入失敗，已停止擷取；請檢查診斷後重新啟用。";
            _state = null;
            Interlocked.Increment(ref _generation);
            if (state is null) return;
            try
            {
                // 排空之後才卸載：已接受的擷取必須先完成交易。
                await state.Writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                SqlAssistDiagnostics.WriteAlways($"SQL Memory 排空時失敗：{error.Message}");
            }

            await Task.Run(() => state.Repository.Dispose()).ConfigureAwait(false);
            SqlAssistDiagnostics.WriteAlways("SQL Memory 已停用；儲存已釋放。");
        }
        finally { Gate.Release(); }
    }

    private static async Task TickAsync()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;

        await Gate.WaitAsync().ConfigureAwait(false);

        try
        {
            var state = Volatile.Read(ref _state);
            var lifetime = Volatile.Read(ref _lifetime);
            if (state is null || lifetime is null || lifetime.IsCancellationRequested) return;

            var now = DateTimeOffset.UtcNow;
            var heartbeat = state.Heartbeat;
            if (heartbeat.IsDue(now))
            {
                await heartbeat.BeatAsync(now, lifetime.Token).ConfigureAwait(false);
            }

            var lastEdit = new DateTimeOffset(Interlocked.Read(ref _lastEditTicks), TimeSpan.Zero);
            var tick = await state.Runner
                .RunOnceAsync(now, now - lastEdit >= IdleThreshold, heartbeat.LeaseId is not null, lifetime.Token)
                .ConfigureAwait(false);

            if (tick.Outcome == QueryMemoryMaintenanceOutcome.Maintained && tick.Result is { } result)
            {
                SqlAssistDiagnostics.Write(
                    $"SQL Memory 維護：分級 {tick.Level} 檢查 {result.ExaminedCandidates} 刪除 {result.DeletedRows} " +
                    $"容量 {result.CapacityStatus} 釋放租約 {tick.ReleasedLeases} 截斷 {tick.Checkpointed}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // 維護失敗不讓擷取跟著停：下一輪重跑同一個游標即可。
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 維護失敗：{error.Message}");
        }
        finally { Gate.Release(); Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>寫入器 fault 之後不再接收；宿主必須察覺並停用，不能把排空當成保存成功。</summary>
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
        "SqlAssist.Ssms22", "QueryMemory", "QueryMemory.db");

    /// <summary>一份接好線的狀態；設定改變時整份換掉，呼叫端拿到的永遠是一致的一組。</summary>
    private sealed class State
    {
        private State(IsolatedQueryMemoryRepository repository, QueryMemoryBackgroundWriter writer,
            QueryMemoryLeaseHeartbeat heartbeat, QueryMemoryMaintenanceRunner runner, QueryMemoryPolicy policy)
        {
            Repository = repository;
            Writer = writer;
            Heartbeat = heartbeat;
            Runner = runner;
            Policy = policy;
        }

        public IsolatedQueryMemoryRepository Repository { get; }

        public QueryMemoryBackgroundWriter Writer { get; }

        public QueryMemoryLeaseHeartbeat Heartbeat { get; }

        public QueryMemoryMaintenanceRunner Runner { get; }

        public QueryMemoryPolicy Policy { get; }

        public static State Create(IsolatedQueryMemoryRepository repository, SqlAssistSettings settings,
            QueryMemoryLeaseOwner owner)
        {
            var writer = new QueryMemoryBackgroundWriter(
                new QueryMemoryProcessor(repository, new QueryRevisionEngine()),
                MaximumPendingCaptures, MaximumPendingTextBytes);

            return new State(repository, writer,
                new QueryMemoryLeaseHeartbeat(repository, owner, HeartbeatInterval),
                BuildRunner(repository, settings, owner), BuildPolicy(settings));
        }

        /// <summary>沿用同一個儲存庫、寫入器與心跳，只換掉政策與整條保留分級。</summary>
        public State With(SqlAssistSettings settings, QueryMemoryLeaseOwner owner) =>
            new(Repository, Writer, Heartbeat, BuildRunner(Repository, settings, owner), BuildPolicy(settings));

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
                settings.QueryMemoryMaxSavedRevisions);

            return new QueryMemoryMaintenanceRunner(repository, repository,
                new QueryMemoryLeaseReaper(Environment.MachineName, QueryMemoryProcessProbe.IsOwnerRunning),
                owner, schedule, plan, LeaseExpiry);
        }
    }
}
