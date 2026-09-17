using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

/// <summary>週期性計時器的來源；宿主決定回呼跑在哪條執行緒、失敗如何記錄。</summary>
public interface ISqlMemoryTimerFactory
{
    /// <param name="name">診斷用名稱。</param>
    /// <param name="period">第一次與之後每次觸發的間隔。</param>
    /// <param name="tick">觸發時呼叫；宿主必須觀察回傳的工作，不留下未觀察的例外。</param>
    /// <returns>釋放即停止計時器。</returns>
    IDisposable Start(string name, TimeSpan period, Func<Task> tick);
}

/// <summary>診斷紀錄；<see cref="Detail"/> 只在使用者開啟詳細診斷時寫入。</summary>
public interface ISqlMemoryRuntimeLog
{
    void Detail(string message);

    void Important(string message);
}

/// <summary>排程的解析度與上限；只有測試需要改。</summary>
public sealed class SqlMemoryRuntimeOptions
{
    public static SqlMemoryRuntimeOptions Default { get; } = new();

    /// <summary>排程的最小解析度；真正的間隔由設定決定，這只是問「到了沒」的頻率。</summary>
    public TimeSpan TimerPeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>使用者多久沒有編輯才算閒置。比維護間隔短得多，否則閒置提前永遠用不到。</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>心跳間隔；遠短於租約期限，一次排程延遲不該讓別人看到過期租約。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan LeaseExpiry { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>啟動後先讓 SSMS 自己開完；維護不跟開窗搶 I/O。</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>佇列上限；文字位元組是估計值，不是程序記憶體硬上限。</summary>
    public int MaximumPendingCaptures { get; init; } = 64;

    public long MaximumPendingTextBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>
    /// 還有工作時的下一批距離。等於計時器解析度：一個計時器週期最多一批有界交易，
    /// 讓出執行權的同時，積壓一萬個候選約 25 分鐘巡完，不必等上數十個維護間隔。
    /// </summary>
    public TimeSpan PendingDelay => TimerPeriod;
}

/// <summary>
/// SQL Memory 的宿主協調：開啟、換設定、關閉、世代、寫入器故障、心跳與維護排程。
/// </summary>
/// <remarks>
/// 只依賴儲存契約、時鐘、計時器與設定快照，不碰 SSMS；VSIX 只讀設定、建立計時器與顯示通知。
///
/// 閘門只包開啟、換設定與關閉這些狀態轉換；讀取、擷取、心跳與維護都不經過它，
/// 心跳與維護各有計時器與重入旗標，互不等待——慢的維護批次或手動整理不能讓本程序的租約看起來過期。
///
/// 狀態轉換一律收斂到最新的設定：排隊中的轉換拿到閘門後重讀設定，
/// 連續切換開關時不會先開再關，也不會把晚到的舊設定套上去。
/// </remarks>
public sealed class SqlMemoryRuntime
{
    private readonly Func<CancellationToken, Task<ISqlMemoryStore>> _openStorage;
    private readonly Func<SqlMemoryLeaseOwner> _owner;
    private readonly SqlMemoryLeaseReaper _reaper;
    private readonly ISqlMemoryTimerFactory _timers;
    private readonly ISqlMemoryRuntimeLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SqlMemoryRuntimeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly SqlMemoryActivityLog _activity = new();
    private readonly SqlMemoryCapacityMonitor _capacity = new();

    private SqlMemoryConfiguration _configuration = SqlMemoryConfiguration.Disabled;
    private SqlMemoryRuntimeStatus _status = SqlMemoryRuntimeStatus.Initial;
    private State? _state;
    private IDisposable? _maintenanceTimer;
    private IDisposable? _heartbeatTimer;
    private bool _started;
    private bool _stopped;
    private long _lastEditTicks;
    private int _maintaining;
    private int _beating;
    private long _lastMaintainedTicks;

    /// <param name="openStorage">開啟一份儲存；取消代表宿主正在卸載。失敗以 <see cref="SqlMemoryStorageException"/> 分類為佳。</param>
    /// <param name="owner">本程序的租約擁有者；開啟儲存時才取得，探測失敗會走開啟失敗的路徑。</param>
    /// <param name="reaper">判斷過期租約的程序是否真的不在。</param>
    public SqlMemoryRuntime(Func<CancellationToken, Task<ISqlMemoryStore>> openStorage, Func<SqlMemoryLeaseOwner> owner,
        SqlMemoryLeaseReaper reaper, ISqlMemoryTimerFactory timers, ISqlMemoryRuntimeLog log,
        Func<DateTimeOffset>? clock = null, SqlMemoryRuntimeOptions? options = null)
    {
        _openStorage = openStorage ?? throw new ArgumentNullException(nameof(openStorage));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _reaper = reaper ?? throw new ArgumentNullException(nameof(reaper));
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _options = options ?? SqlMemoryRuntimeOptions.Default;
    }

    /// <summary>狀態或世代改變時觸發，可能在任何執行緒；處理常式不得擲出，也不得同步等待本物件的操作。</summary>
    public event EventHandler<SqlMemoryRuntimeStatus>? StatusChanged;

    /// <summary>一筆擷取沒有保存時觸發，可能在 UI 或背景寫入器的執行緒上。</summary>
    public event EventHandler<SqlCaptureDroppedEventArgs>? CaptureDropped;

    /// <summary>容量分級改變，或剛進入 Critical 需要通知；可能在任何執行緒，處理常式不得擲出。</summary>
    public event EventHandler<SqlMemoryCapacityChangedEventArgs>? CapacityChanged;

    public SqlMemoryRuntimeStatus Status => Volatile.Read(ref _status);

    public long Generation => Status.Generation;

    /// <summary>目前是否真的在擷取；沒有接上儲存時一律 false。按鍵熱路徑只讀這一個欄位。</summary>
    public bool IsCapturing => Volatile.Read(ref _state) is not null;

    /// <summary>設定開著且儲存已開啟；設定剛關掉、還在關閉途中就已經是 false。</summary>
    public bool IsAvailable => IsCapturing && Volatile.Read(ref _configuration).Enabled;

    public TimeSpan IdleDebounce => Volatile.Read(ref _configuration).IdleDebounce;

    /// <summary>最近一次觀測到的容量分級；來源是維護批次、用量頁與手動清理，沒有觀測過是 Normal。</summary>
    public SqlMemoryUsageSeverity CapacitySeverity => _capacity.Severity;

    /// <summary>本程序的維護排程觀測；儲存沒有開啟時只剩最後一次維護的時間。</summary>
    public SqlMemoryMaintenanceOverview MaintenanceOverview
    {
        get
        {
            var state = Volatile.Read(ref _state);
            var last = Interlocked.Read(ref _lastMaintainedTicks);
            return new SqlMemoryMaintenanceOverview(state?.Runner.NextDueAt, last == 0 ? null : new DateTimeOffset(last, TimeSpan.Zero),
                state?.Runner.Level ?? 0, state?.Runner.PendingWork ?? false, state?.Heartbeat.LeaseId != null);
        }
    }

    /// <summary>建立心跳與維護計時器；重複呼叫只有第一次有效，卸載後不再啟動。</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started || _stopped) return;
            _started = true;
            // 維護失敗只代表下一輪重跑同一個游標；心跳另有計時器，兩者互不等待。
            _maintenanceTimer = _timers.Start("SQL Memory 維護排程", _options.TimerPeriod, MaintainOnceAsync);
            _heartbeatTimer = _timers.Start("SQL Memory 租約心跳", _options.TimerPeriod, BeatOnceAsync);
        }
    }

    /// <summary>套用新設定：開啟、只換政策或關閉。開啟失敗會擲出，狀態同時變成 <see cref="SqlMemoryRuntimePhase.OpenFailed"/>。</summary>
    public Task ApplyAsync(SqlMemoryConfiguration configuration)
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        lock (_sync)
        {
            if (_stopped) return Task.CompletedTask;
            Volatile.Write(ref _configuration, configuration);
        }

        // 先讓畫面知道要去哪裡；真正的轉換在閘門後依最新設定收斂。
        if (!configuration.Enabled) Publish(status => status.With(SqlMemoryRuntimePhase.Disabled));
        else if (Volatile.Read(ref _state) is null) Publish(status => status.With(SqlMemoryRuntimePhase.Opening));
        return ConvergeAsync();
    }

    /// <summary>記下最後一次編輯；閒置提前維護只看這個值，不問宿主的訊息佇列。</summary>
    public void NoteEdit() => Interlocked.Exchange(ref _lastEditTicks, _clock().UtcTicks);

    /// <summary>把一次擷取交給背景寫入器。被拒絕時回報一次可見的降級訊息，不靜靜丟掉。</summary>
    public SqlCaptureEnqueueResult TryEnqueue(SqlCapture capture)
    {
        if (capture == null) throw new ArgumentNullException(nameof(capture));
        var state = Volatile.Read(ref _state);
        if (state is null) return SqlCaptureEnqueueResult.Stopped;

        var result = state.Writer.TryEnqueue(capture, state.Policy);
        if (result == SqlCaptureEnqueueResult.QueueFull) Drop(state, SqlCaptureDrop.QueueFull, "SQL Memory 拒絕擷取：" + result);
        else if (result == SqlCaptureEnqueueResult.SnapshotTooLarge) Drop(state, SqlCaptureDrop.SnapshotTooLarge, "SQL Memory 拒絕擷取：" + result);
        return result;
    }

    public Task<SqlMemoryPage<SqlHistoryItem>> ReadHistoryAsync(SqlHistoryRequest request, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.ReadHistoryAsync(request, token), cancellationToken);

    public Task<SqlMemoryPage<SqlFavoriteItem>> ReadFavoritesAsync(SqlFavoriteRequest request, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.ReadFavoritesAsync(request, token), cancellationToken);

    public Task<IReadOnlyList<string>> ReadConnectionFacetsAsync(SqlConnectionFacetRequest request,
        CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.ReadConnectionFacetsAsync(request, token), cancellationToken);

    public Task<SqlContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.ReadContentAsync(contentId, token), cancellationToken);

    public Task<SqlHistoryDeleteResult> DeleteHistoryAsync(SqlHistoryItem item, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.DeleteHistoryAsync(item, token), cancellationToken);

    public Task<SqlFavoriteItem?> ReadFavoriteAsync(Guid favoriteId, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.ReadFavoriteAsync(favoriteId, token), cancellationToken);

    public Task<SqlFavoriteWriteResult> SaveFavoriteAsync(SqlFavoriteSave save, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.SaveFavoriteAsync(save, token), cancellationToken);

    public Task<SqlMemoryPage<SqlFavoriteRevisionItem>> ReadFavoriteRevisionsAsync(SqlFavoriteRevisionRequest request,
        CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.ReadFavoriteRevisionsAsync(request, token), cancellationToken);

    public Task<SqlFavoriteWriteResult> DeleteFavoriteAsync(Guid favoriteId, Guid expectedVersion, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.DeleteFavoriteAsync(favoriteId, expectedVersion, token), cancellationToken);

    /// <summary>用量頁的完整快照：報表與共用維護狀態讀自儲存，保留計畫、排程與清理紀錄讀自本程序。</summary>
    public async Task<SqlMemoryUsageSnapshot> ReadUsageSnapshotAsync(CancellationToken cancellationToken)
    {
        var plan = Volatile.Read(ref _configuration).Plan;
        var (report, maintenance) = await UseAsync(async (storage, token) =>
            (await storage.ReadUsageReportAsync(token).ConfigureAwait(false),
                await storage.ReadMaintenanceStateAsync(token).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        ObserveCapacity(report.Usage, plan.MaxContentBytes, maintenance?.CapacityStatus);
        return new SqlMemoryUsageSnapshot(report, maintenance, plan, MaintenanceOverview, _activity.Snapshot());
    }

    public Task<SqlMemoryCleanupEstimate> EstimateCleanupAsync(SqlMemoryCleanupRequest request, CancellationToken cancellationToken) =>
        UseAsync((storage, token) => storage.EstimateCleanupAsync(request, token), cancellationToken);

    /// <summary>使用者確認後的手動清理；保護根與逐筆刪除相同，回復內容只在本程序續著心跳時才能清。</summary>
    public Task<SqlMemoryCleanupResult> CleanupAsync(SqlMemoryCleanupRequest request, IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return RecordAsync(SqlMemoryActivityKind.Cleanup, (state, token) =>
        {
            // 本程序沒有租約時，自己開著的視窗在儲存層看起來也「沒有租約」，會被當成已關閉而清掉。
            if (request.Includes(SqlMemoryCleanupTargets.ClosedRecovery) && state.Heartbeat.LeaseId == null)
                throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Unavailable,
                    "SQL Memory 還在建立工作階段租約，暫時無法分辨哪些回復內容屬於已關閉的視窗；請一分鐘後再試。");
            return SqlMemoryCleanup.RunAsync(state.Storage, request, progress, token);
        }, result => (result.DeletedRows, result.ReleasedContentBytes, result.Usage), cancellationToken);
    }

    /// <summary>不等排程，以日常保留規則立即巡完一輪；不升級分級、不寫共用輪次。</summary>
    public Task<SqlMemoryCleanupResult> MaintainNowAsync(IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var plan = Volatile.Read(ref _configuration).Plan;
        return RecordAsync(SqlMemoryActivityKind.ManualMaintenance, async (state, token) =>
        {
            var now = _clock();
            var result = await SqlMemoryCleanup.MaintainAsync(state.Storage,
                plan.BuildLadder(now, state.Heartbeat.LeaseId != null)[0], progress, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastMaintainedTicks, now.UtcTicks);
            return result;
        }, result => (result.DeletedRows, result.ReleasedContentBytes, result.Usage), cancellationToken);
    }

    /// <summary>寫出一份完整備份到不存在的檔案；回傳備份檔大小。</summary>
    public Task<long> BackupAsync(string destinationPath, CancellationToken cancellationToken) =>
        RecordAsync(SqlMemoryActivityKind.Backup, (state, token) => state.Storage.BackupAsync(destinationPath, token),
            length => (0L, length, (SqlMemoryUsage?)null), cancellationToken);

    /// <summary>設定頁的手動整理；重建整個資料庫，時間隨資料量成長，不進背景排程。</summary>
    /// <remarks>
    /// 先等本程序的 writer 排空，已接受的擷取不必在整理期間擱在記憶體裡。整理期間 writer 不停止：
    /// 新擷取照常排隊；VACUUM 持有 SQLite 寫鎖，提交等到 busy timeout 回報忙碌後由 processor 退避重試，
    /// 與其他程序整理時的情形相同。讀取與心跳不受影響。
    /// </remarks>
    public async Task<SqlMemoryUsage> CompactAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) is { } state)
            await state.Writer.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        var before = 0L;
        return await RecordAsync(SqlMemoryActivityKind.Compact, async (current, token) =>
        {
            before = (await current.Storage.ReadUsageAsync(token).ConfigureAwait(false)).DatabaseFileBytes;
            return await current.Storage.CompactAsync(token).ConfigureAwait(false);
        }, after => (0L, Math.Max(0, before - after.DatabaseFileBytes), (SqlMemoryUsage?)after), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>宿主卸載：停止計時器、排空背景寫入器、交回維護租約，再釋放儲存；總時間受 <paramref name="timeout"/> 限制。</summary>
    /// <returns>是否在時限內完成（含沒有東西要關）。逾時只記錄診斷並放棄，不擲出。</returns>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        IDisposable? maintenanceTimer, heartbeatTimer;
        lock (_sync)
        {
            if (_stopped) return true;
            _stopped = true;
            maintenanceTimer = _maintenanceTimer;
            heartbeatTimer = _heartbeatTimer;
            _maintenanceTimer = _heartbeatTimer = null;
            Volatile.Write(ref _configuration, SqlMemoryConfiguration.Disabled);
        }

        maintenanceTimer?.Dispose();
        heartbeatTimer?.Dispose();
        // 取消開檔中的轉換；開檔回來後看到已卸載，不會把儲存掛上去。
        _lifetime.Cancel();
        var closed = await CloseAsync(null, timeout).ConfigureAwait(false);
        Publish(status => status.With(SqlMemoryRuntimePhase.Disabled));
        return closed;
    }

    /// <summary>計時器觸發的一次維護；進行中就略過，不排隊。</summary>
    private async Task MaintainOnceAsync()
    {
        if (Interlocked.CompareExchange(ref _maintaining, 1, 0) != 0) return;

        var state = Volatile.Read(ref _state);
        try
        {
            if (state is null || state.Lifetime.IsCancellationRequested) return;

            var now = _clock();
            var lastEdit = new DateTimeOffset(Interlocked.Read(ref _lastEditTicks), TimeSpan.Zero);
            var tick = await state.Runner
                .RunOnceAsync(now, now - lastEdit >= _options.IdleThreshold, state.Heartbeat.LeaseId, state.Lifetime.Token)
                .ConfigureAwait(false);

            if (tick.Outcome == SqlMemoryMaintenanceOutcome.Maintained && tick.Result is { } result)
            {
                Interlocked.Exchange(ref _lastMaintainedTicks, now.UtcTicks);
                if (result.DeletedRows > 0)
                    _activity.Record(new SqlMemoryActivity(now, SqlMemoryActivityKind.ScheduledMaintenance, result.DeletedRows, 0));
                ObserveCapacity(result.Usage, Volatile.Read(ref _configuration).Plan.MaxContentBytes, result.CapacityStatus);
                _log.Detail(
                    $"SQL Memory 維護：{tick.Scan} 分級 {tick.Level} 檢查 {result.ExaminedCandidates} 刪除 {result.DeletedRows} " +
                    $"容量 {result.CapacityStatus} 續巡 {result.Cursor != null} 釋放租約 {tick.ReleasedLeases} 截斷 {tick.Checkpointed}");
            }
        }
        catch (Exception error) when (IsClosing(error, state)) { }
        catch (Exception error)
        {
            // 維護失敗不讓擷取跟著停：下一輪重跑同一個游標即可。
            _log.Important($"SQL Memory 維護失敗：{error.Message}");
        }
        finally { Interlocked.Exchange(ref _maintaining, 0); }
    }

    /// <summary>計時器觸發的一次心跳；續約或重開租約，不等維護批次，也不和維護共用任何鎖。</summary>
    private async Task BeatOnceAsync()
    {
        if (Interlocked.CompareExchange(ref _beating, 1, 0) != 0) return;

        var state = Volatile.Read(ref _state);
        try
        {
            if (state is null || state.Lifetime.IsCancellationRequested) return;

            var now = _clock();
            if (state.Heartbeat.IsDue(now))
                await state.Heartbeat.BeatAsync(now, state.Lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsClosing(error, state)) { }
        catch (Exception error)
        {
            // 下一次計時器再試；租約期限遠長於心跳間隔，一次失敗不會讓別人看到過期。
            _log.Important($"SQL Memory 租約心跳失敗：{error.Message}");
        }
        finally { Interlocked.Exchange(ref _beating, 0); }
    }

    private Task<T> UseAsync<T>(Func<ISqlMemoryStore, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        UseStateAsync((state, token) => operation(state.Storage, token), cancellationToken);

    /// <summary>
    /// 使用者主動的整理：成功與失敗都進清理紀錄，結果的容量交給容量分級。取消不記錄——使用者自己停下來的不是失敗。
    /// </summary>
    /// <param name="describe">從結果取出刪除列數、位元組與清理後容量；容量為 null 表示這個操作不改變內容量。</param>
    private async Task<T> RecordAsync<T>(SqlMemoryActivityKind kind, Func<State, CancellationToken, Task<T>> operation,
        Func<T, (long Rows, long Bytes, SqlMemoryUsage? Usage)> describe, CancellationToken cancellationToken)
    {
        T result;
        try
        {
            result = await UseStateAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _activity.Record(new SqlMemoryActivity(_clock(), kind, 0, 0, error.Message));
            throw;
        }
        var (rows, bytes, usage) = describe(result);
        _activity.Record(new SqlMemoryActivity(_clock(), kind, rows, bytes));
        if (usage != null) ObserveCapacity(usage, Volatile.Read(ref _configuration).Plan.MaxContentBytes, null);
        return result;
    }

    private void ObserveCapacity(SqlMemoryUsage usage, long? limit, SqlMemoryCapacityStatus? status)
    {
        var (changed, notify) = _capacity.Observe(usage, limit, status);
        if (changed || notify)
            CapacityChanged?.Invoke(this, new SqlMemoryCapacityChangedEventArgs(_capacity.Severity, _capacity.Ratio, notify));
    }

    // 不經過閘門：儲存自己保證釋放會等進行中的呼叫。關閉時取消這份狀態的生命週期，
    // 長時間的全文搜尋才不會拖住卸載。
    private async Task<T> UseStateAsync<T>(Func<State, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!Volatile.Read(ref _configuration).Enabled || Volatile.Read(ref _state) is not { } state)
            throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Unavailable, Status.Message);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Lifetime.Token);
        try
        {
            return await operation(state, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when ((error is OperationCanceledException or ObjectDisposedException) &&
            !cancellationToken.IsCancellationRequested && state.Lifetime.IsCancellationRequested)
        {
            // 呼叫端沒有取消，是儲存在途中被關閉或重開；世代已經換過，舊畫面不會採用這個結果。
            throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Unavailable,
                "SQL Memory 已重新開啟或停用；請重新整理後再操作。");
        }
    }

    private async Task ConvergeAsync()
    {
        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            while (true)
            {
                var configuration = Volatile.Read(ref _configuration);
                if (!configuration.Enabled) await CloseLockedAsync(null, () => Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                else if (!await OpenOrUpdateLockedAsync(configuration).ConfigureAwait(false)) return;

                // 轉換途中又換了設定：在同一次持有閘門時收斂到最新的那一份。
                if (ReferenceEquals(configuration, Volatile.Read(ref _configuration))) return;
            }
        }
        finally { _gate.Release(); }
    }

    /// <returns>false 表示宿主已卸載，不必再收斂。</returns>
    private async Task<bool> OpenOrUpdateLockedAsync(SqlMemoryConfiguration configuration)
    {
        if (_state is { } existing)
        {
            // 儲存與維護排程都不重開；只換擷取政策與保留計畫，計畫指紋變了游標才跟著重來。
            existing.Reconfigure(configuration, _options);
            Publish(status => status.With(SqlMemoryRuntimePhase.Ready));
            return true;
        }

        Publish(status => status.With(SqlMemoryRuntimePhase.Opening));
        ISqlMemoryStore storage;
        try
        {
            var owner = _owner();
            storage = await _openStorage(_lifetime.Token).ConfigureAwait(false);
            try
            {
                // 開檔期間宿主已經卸載：關閉可能已逾時放棄等待，這份儲存不能再掛上去。
                _lifetime.Token.ThrowIfCancellationRequested();
                var state = State.Create(storage, configuration, owner, _reaper, _options, _clock(), OnBusy);
                ObserveWriter(state);
                Volatile.Write(ref _state, state);
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            // 狀態供工具窗顯示，例外仍交給呼叫端的啟用通知，不把失敗當成空清單。
            Publish(status => status.WithOpenFailure(error));
            throw;
        }

        Publish(status => status.With(SqlMemoryRuntimePhase.Ready, nextGeneration: true));
        _log.Important("SQL Memory 已啟用；擷取與背景整理開始運作。");
        return true;
    }

    /// <param name="failed">writer fault 時傳入；狀態已被換掉就什麼都不做。</param>
    private async Task<bool> CloseAsync(State? failed, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        TimeSpan Remaining() => timeout == Timeout.InfiniteTimeSpan
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromTicks(Math.Max(0, (timeout - elapsed.Elapsed).Ticks));

        // 閘門只在轉換期間被占住；開檔卡住時關閉照樣受時限約束。
        if (!await _gate.WaitAsync(Remaining()).ConfigureAwait(false))
        {
            _log.Important("SQL Memory 關閉逾時：開啟流程尚未結束，放棄等待。");
            return false;
        }

        try
        {
            return await CloseLockedAsync(failed, Remaining).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> CloseLockedAsync(State? failed, Func<TimeSpan> remaining)
    {
        var state = _state;
        if (state is null || (failed is not null && !ReferenceEquals(state, failed))) return true;

        Volatile.Write(ref _state, null);
        Publish(status => status.With(failed is null ? SqlMemoryRuntimePhase.Disabled : SqlMemoryRuntimePhase.WriterFailed,
            nextGeneration: true));
        // 容量分級屬於這份儲存；重開或換檔案之後的警示點要等新的觀測。
        if (_capacity.Reset())
            CapacityChanged?.Invoke(this, new SqlMemoryCapacityChangedEventArgs(SqlMemoryUsageSeverity.Normal, null, false));

        // 讀取、心跳與維護不必做完；取消後儲存只剩 writer 已接受的擷取與提交中的交易要等。
        state.Lifetime.Cancel();

        // 排空之後才釋放：已接受的擷取必須先完成交易。
        var drain = state.Writer.CompleteAsync();
        if (!await CompletesWithinAsync(drain, remaining()).ConfigureAwait(false))
        {
            _log.Important($"SQL Memory 關閉逾時：放棄 {state.Writer.PendingCount} 筆尚未提交的擷取，未釋放儲存。");
            return false;
        }

        try { await drain.ConfigureAwait(false); }
        catch (Exception error) { _log.Important($"SQL Memory 排空時失敗：{error.Message}"); }

        // 交回維護租約，另一個 SSMS 不必等租約過期才能接續共用輪次。停止會等被取消的那一批離開，
        // 不帶生命週期的 token：它已經取消了，而交回本身只是一句有界的刪除。
        var stop = state.Runner.StopAsync(CancellationToken.None);
        if (!await CompletesWithinAsync(stop, remaining()).ConfigureAwait(false))
        {
            _log.Important("SQL Memory 關閉逾時：維護批次尚未結束，未交回維護租約也未釋放儲存。");
            return false;
        }

        // 交回失敗只代表別人要等租約過期，不影響釋放。
        try { await stop.ConfigureAwait(false); }
        catch (Exception error) { _log.Important($"SQL Memory 交回維護租約失敗：{error.Message}"); }

        var dispose = Task.Run(state.Storage.Dispose);
        if (!await CompletesWithinAsync(dispose, remaining()).ConfigureAwait(false))
        {
            _log.Important("SQL Memory 關閉逾時：仍有儲存操作進行中，未釋放儲存。");
            return false;
        }

        await dispose.ConfigureAwait(false);
        _log.Important("SQL Memory 已停用；儲存已釋放。");
        return true;
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

    /// <summary>關閉期間的取消與已釋放不是失敗；只有這份狀態真的被關掉時才靜默。</summary>
    private static bool IsClosing(Exception error, State? state) =>
        (error is OperationCanceledException or ObjectDisposedException) && state?.Lifetime.IsCancellationRequested == true;

    /// <summary>
    /// 寫入器 fault 之後不再接收；必須察覺並停用，不能把排空當成保存成功。
    /// 忙碌不會走到這裡：writer 只在損毀、不相容或未知錯誤時 fault。
    /// </summary>
    private void ObserveWriter(State state) => _ = state.Writer.Completion.ContinueWith(
        completion => completion.IsFaulted ? OnWriterFaultedAsync(state, completion.Exception) : Task.CompletedTask,
        TaskScheduler.Default).Unwrap();

    private async Task OnWriterFaultedAsync(State state, AggregateException? error)
    {
        try
        {
            _log.Important($"SQL Memory 背景寫入器已停止：{error?.GetBaseException().Message}");
            await CloseAsync(state, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
        catch (Exception closing)
        {
            // 沒有人接這個工作的結果；失敗只能留在診斷裡，不能變成未觀察的例外。
            _log.Important($"釋放失敗的 SQL Memory 寫入器時失敗：{closing.Message}");
        }
    }

    /// <summary>儲存持續忙碌而放棄一筆擷取；writer 繼續運作，但這一筆沒有保存必須看得見。</summary>
    private void OnBusy(State state, SqlCapture capture, SqlMemoryStorageException error) =>
        Drop(state, SqlCaptureDrop.StorageBusy,
            $"SQL Memory 儲存持續忙碌，放棄擷取 {capture.Kind}（SQLite {error.ErrorCode}/{error.ExtendedErrorCode}）：{error.Message}");

    private void Drop(State state, SqlCaptureDrop drop, string diagnostic)
    {
        _log.Important(diagnostic);
        if (ReferenceEquals(Volatile.Read(ref _state), state))
            Publish(status => status.Phase == SqlMemoryRuntimePhase.Ready ? status.WithDrop(drop) : status);

        var first = drop switch
        {
            SqlCaptureDrop.StorageBusy => Interlocked.Exchange(ref state.BusyReported, 1) == 0,
            SqlCaptureDrop.QueueFull => Interlocked.Exchange(ref state.QueueFullReported, 1) == 0,
            _ => true,
        };
        CaptureDropped?.Invoke(this, new SqlCaptureDroppedEventArgs(drop, first));
    }

    private void Publish(Func<SqlMemoryRuntimeStatus, SqlMemoryRuntimeStatus> change)
    {
        SqlMemoryRuntimeStatus current, next;
        do
        {
            current = Volatile.Read(ref _status);
            next = change(current);
            if (next.Equals(current)) return;
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _status, next, current), current));

        StatusChanged?.Invoke(this, next);
    }

    /// <summary>一份開啟中的儲存與接在它上面的寫入器、心跳與維護；身分在關閉前不變，換設定只換政策。</summary>
    private sealed class State
    {
        public int BusyReported;
        public int QueueFullReported;
        private SqlCapturePolicy _policy;

        private State(ISqlMemoryStore storage, CancellationTokenSource lifetime, SqlCaptureQueue writer,
            SqlMemoryLeaseHeartbeat heartbeat, SqlMemoryMaintenanceRunner runner, SqlCapturePolicy policy)
        {
            Storage = storage;
            Lifetime = lifetime;
            Writer = writer;
            Heartbeat = heartbeat;
            Runner = runner;
            _policy = policy;
        }

        public ISqlMemoryStore Storage { get; }

        /// <summary>
        /// 這份儲存的使用期間；關閉時取消讀取、心跳與維護，writer 不受影響。
        /// 刻意不處置：晚到的呼叫仍可能讀取 Token，而它沒有計時器或等待控制代碼要釋放。
        /// </summary>
        public CancellationTokenSource Lifetime { get; }

        public SqlCaptureQueue Writer { get; }

        public SqlMemoryLeaseHeartbeat Heartbeat { get; }

        public SqlMemoryMaintenanceRunner Runner { get; }

        /// <summary>擷取當下讀一次；換設定只影響之後排入的擷取。</summary>
        public SqlCapturePolicy Policy => Volatile.Read(ref _policy);

        public static State Create(ISqlMemoryStore storage, SqlMemoryConfiguration configuration, SqlMemoryLeaseOwner owner,
            SqlMemoryLeaseReaper reaper, SqlMemoryRuntimeOptions options, DateTimeOffset now,
            Action<State, SqlCapture, SqlMemoryStorageException> busy)
        {
            var heartbeat = new SqlMemoryLeaseHeartbeat(storage, owner, options.HeartbeatInterval);
            var schedule = new SqlMemoryMaintenanceSchedule(now, options.StartupDelay, configuration.MaintenanceInterval,
                options.PendingDelay, configuration.IdleMinimumGap);
            var runner = new SqlMemoryMaintenanceRunner(storage, storage, reaper, owner, schedule, configuration.Plan,
                options.LeaseExpiry);

            State? created = null;
            // 租約明確隨每次提交傳入，不藏在儲存層；心跳重開後的下一筆就標上新租約。
            var writer = new SqlCaptureQueue(
                new SqlCaptureCommitter(storage, new SqlCapturePlanner(), leaseId: () => heartbeat.LeaseId),
                options.MaximumPendingCaptures, options.MaximumPendingTextBytes,
                (capture, error) => { if (created is not null) busy(created, capture, error); });
            created = new State(storage, new CancellationTokenSource(), writer, heartbeat, runner, configuration.Policy);
            return created;
        }

        /// <summary>沿用同一份儲存、使用期間、寫入器、心跳與維護排程，只換掉擷取政策與保留計畫。</summary>
        /// <remarks>
        /// 維護不重建：重建會把啟動延遲重新算一次，而且新舊兩個 runner 可能同時跑批次。
        /// 進行中的一批仍用舊計畫做完；下一批才讀共用狀態、依新計畫指紋決定是否重開輪次。
        /// 狀態物件本身不換：寫入器故障與忙碌回報都以它的身分比對是不是目前這份儲存。
        /// </remarks>
        public void Reconfigure(SqlMemoryConfiguration configuration, SqlMemoryRuntimeOptions options)
        {
            Runner.Reconfigure(configuration.Plan, configuration.MaintenanceInterval, options.PendingDelay,
                configuration.IdleMinimumGap);
            Volatile.Write(ref _policy, configuration.Policy);
        }
    }
}
