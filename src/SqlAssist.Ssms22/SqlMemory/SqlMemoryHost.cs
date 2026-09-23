using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// SQL Memory 在 SSMS 裡的接線：讀設定、建立計時器、送出通知、提供 SSMS 路徑。
/// </summary>
/// <remarks>
/// 開啟、關閉、世代、故障恢復、心跳與維護排程都在 <see cref="SqlMemoryRuntime"/>，
/// 那裡不需要 SSMS 就能測；這一層只把 SSMS 的服務接上去。
///
/// 計時器刻意不在 UI 執行緒上：維護只呼叫隔離儲存，不碰編輯器緩衝區，
/// 排在 UI 執行緒上等於讓清理與打字搶同一條執行緒。
/// </remarks>
internal static class SqlMemoryHost
{
    /// <summary>
    /// 關閉 SSMS 時排空與卸載的總時限。逾時就放棄剩下的擷取，不讓殼層卡在關閉；
    /// 強制結束本來就可能遺失未落盤的內容。
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private static readonly object SyncRoot = new();
    private static bool _initialized;
    private static int _captureNoticeScheduled;

    /// <summary>
    /// 程序內唯一的宿主；在套件載入前就存在，編輯器接線可以先問它「有沒有在擷取」，答案是否。
    /// </summary>
    public static SqlMemoryRuntime Runtime { get; } = new(
        OpenStorageAsync,
        ProcessLivenessProbe.CurrentOwner,
        new SqlMemoryLeaseReaper(Environment.MachineName, ProcessLivenessProbe.IsOwnerRunning),
        new GuardedTimers(),
        new DiagnosticsLog());

    /// <summary>只在 UI 執行緒呼叫；重複呼叫只有第一次接線。</summary>
    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized) return;
            _initialized = true;
            SqlAssistSettingsStore.Changed += OnSettingsChanged;
            Runtime.StatusChanged += OnStatusChanged;
            Runtime.CaptureDropped += OnCaptureDropped;
            Runtime.CapacityChanged += OnCapacityChanged;
            Runtime.MaintenanceFailed += OnMaintenanceFailed;
            Runtime.Start();
        }

        Apply();
    }

    /// <summary>套件卸載：同步等待排空與釋放，SQLite 檔案要在殼層收尾之前放開。</summary>
    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            if (!_initialized) return;
            _initialized = false;
            SqlAssistSettingsStore.Changed -= OnSettingsChanged;
            Runtime.StatusChanged -= OnStatusChanged;
            Runtime.CaptureDropped -= OnCaptureDropped;
            Runtime.CapacityChanged -= OnCapacityChanged;
            Runtime.MaintenanceFailed -= OnMaintenanceFailed;
        }

        // 等待有上限，逾時只記錄診斷，不讓 SSMS 卡在關閉。
        // 卸載這一步沒有非同步的出口（殼層等的是同步的 Dispose），所以只能同步等。
        // 不會卡死的理由要寫在這裡：SqlMemoryRuntime 在 Core，整條關閉路徑全程
        // ConfigureAwait(false)，續程不需要回到 UI 執行緒。哪天它改成碰 UI，這裡要
        // 一起改成 JoinableTaskFactory.Run，而不是把抑制範圍擴大。
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        Runtime.ShutdownAsync(ShutdownTimeout).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    private static void OnSettingsChanged(object? sender, EventArgs eventArgs) => Apply();

    private static void Apply()
    {
        var configuration = SqlMemoryConfiguration.From(SqlAssistSettingsStore.Current);

        // 走 Begin 而不是 BeginProbe：開不起來就是使用者打開了設定卻什麼都沒記到，
        // 那要看得見，不是可有可無的探測。開檔與建立 AppDomain 都在這條背景路徑上。
        SqlAssistPlatformGuard.Begin(
            configuration.Enabled ? NotificationCatalog.EnablingSqlMemory : NotificationCatalog.DisablingSqlMemory,
            () => Runtime.ApplyAsync(configuration),
            NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Info,
            document: string.Empty);
    }

    /// <summary>
    /// 依目前設定重新開啟儲存；復原流程用，失敗直接擲給呼叫端。
    /// </summary>
    /// <remarks>
    /// 不走 <see cref="Apply"/> 的 <c>Guard.Begin</c>：那是沒有人接結果的背景套用，
    /// 而這一條是使用者按下「備份並重建」之後的下一步，開不起來要當場回到他面前。
    /// </remarks>
    internal static async Task ReapplyAsync()
    {
        var configuration = SqlMemoryConfiguration.From(SqlAssistSettingsStore.Current);
        await Runtime.ApplyAsync(configuration).ConfigureAwait(false);
    }

    /// <summary>
    /// 第一次真正開始擷取時提醒一次：資料留在哪裡、怎麼關掉，按鈕直接開 SQL Memory。
    /// </summary>
    /// <remarks>
    /// 總開關預設是開的，所以不能安靜地開始記錄使用者的 SQL。說過就記在狀態存放區裡，
    /// 每台電腦只出現一次；提醒不逾時，使用者沒看到之前不會自己消失。
    /// 路徑不進通知，位置由用量分頁的「開啟資料夾」回答。
    ///
    /// 狀態可能在任何執行緒上發出，而狀態存放區只在 UI 執行緒讀寫，所以要排回去。
    /// 用 <c>BeginProbe</c> 只是因為沒有人接這個工作的結果：它一個工作階段最多跑一次，
    /// 不是會連續失敗的探測。
    /// </remarks>
    private static void OnStatusChanged(object? sender, SqlMemoryRuntimeStatus status)
    {
        if (status.Phase != SqlMemoryRuntimePhase.Ready) return;
        if (Interlocked.Exchange(ref _captureNoticeScheduled, 1) != 0) return;

        SqlAssistPlatformGuard.BeginProbe("SQL Memory 首次擷取說明", async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (SqlAssistState.SqlMemoryCaptureNoticeShown) return;
            SqlAssistState.SqlMemoryCaptureNoticeShown = true;
            NotificationCenter.Default.Prompt(NotificationCatalog.SqlMemoryFirstCapturePrompt(),
                NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Notice);
        });
    }

    /// <summary>
    /// 擷取沒有保存：當下已經發生、沒有執行期間，走事件型通知。
    /// </summary>
    /// <remarks>
    /// 失敗而不是降級：這一次的 SQL 完全沒有記錄，而且值得在「通知失敗」裡回看是哪一段時間掉的。
    /// 連續發生時靠 <see cref="SqlCaptureDroppedEventArgs.ShouldNotify"/> 防抖與合併的 ×N 收斂，
    /// 不每一次新增一列；原因短語是常數，熱路徑上不組字串。
    /// </remarks>
    private static void OnCaptureDropped(object? sender, SqlCaptureDroppedEventArgs drop)
    {
        if (!drop.ShouldNotify) return;
        NotificationCenter.Default.Post(NotificationCatalog.DroppingSqlCapture,
            NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Notice,
            NotificationStatus.Failed, message: drop.Reason);
    }

    /// <summary>
    /// 容量剛進入 Critical 時提醒一次，按鈕直接開維護分頁。
    /// </summary>
    /// <remarks>
    /// 提醒而不是事件：資料都還在，要使用者決定清哪些，不該進「通知失敗」清單，也不該到期就消失。
    /// 防抖在 Core 的 <see cref="SqlMemoryCapacityMonitor"/>：降到 80% 以下才重新武裝，維護逐批刪除時不會反覆跳出；
    /// 同鍵的新提醒取代舊的那一則。按了「稍後」之後這次工作階段不再出現，工具列的警示點一直留著。
    /// </remarks>
    private static void OnCapacityChanged(object? sender, SqlMemoryCapacityChangedEventArgs change)
    {
        if (!change.Notify) return;
        NotificationCenter.Default.Prompt(NotificationCatalog.SqlMemoryCapacityPrompt(change.Reason),
            NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Notice);
    }

    /// <summary>
    /// 背景保留清理失敗：擷取照常，下一輪重跑同一個游標。
    /// </summary>
    /// <remarks>
    /// 失敗走獨立通道，等級不決定它上不上畫面；標 <see cref="NotificationLevel.Debug"/> 是因為
    /// 這是背景的自家事，使用者沒有可做的決定，其他成功的維護結果不該跟著冒出來。
    /// </remarks>
    private static void OnMaintenanceFailed(string reason) =>
        NotificationCenter.Default.Post(NotificationCatalog.MaintainingSqlMemory,
            NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Debug,
            NotificationStatus.Failed, message: reason);

    private static async Task<ISqlMemoryStore> OpenStorageAsync(CancellationToken cancellationToken) =>
        await IsolatedSqlMemoryStore
            .OpenAsync(DatabasePath(), AppDomain.CurrentDomain.BaseDirectory, cancellationToken)
            .ConfigureAwait(false);

    internal static string DatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlAssist.Ssms22", "SQLMemory", "SQLMemory.db");

    /// <summary>沒有人接結果的背景工作一律走 Guard；維護與心跳失敗的內容已由宿主記錄，這裡只擋未觀察的例外。</summary>
    private sealed class GuardedTimers : ISqlMemoryTimerFactory
    {
        public IDisposable Start(string name, TimeSpan period, Func<Task> tick) =>
            new Timer(_ => SqlAssistPlatformGuard.BeginProbe(name, tick), null, period, period);
    }

    private sealed class DiagnosticsLog : ISqlMemoryRuntimeLog
    {
        public void Detail(string message) => SqlAssistDiagnostics.Write(message);

        public void Important(string message) => SqlAssistDiagnostics.WriteAlways(message);
    }
}
