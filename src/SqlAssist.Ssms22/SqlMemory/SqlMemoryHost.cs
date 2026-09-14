using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// SQL Memory 在 SSMS 裡的接線：讀設定、建立計時器、狀態列與通知、提供 SSMS 路徑。
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
    private static SqlAssistPackage? _package;
    private static bool _initialized;

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
    public static void Initialize(SqlAssistPackage package)
    {
        if (package is null) return;

        lock (SyncRoot)
        {
            if (_initialized) return;
            _initialized = true;
            _package = package;
            SqlAssistSettingsStore.Changed += OnSettingsChanged;
            Runtime.CaptureDropped += OnCaptureDropped;
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
            Runtime.CaptureDropped -= OnCaptureDropped;
            _package = null;
        }

        // 等待有上限，逾時只記錄診斷，不讓 SSMS 卡在關閉。
        Runtime.ShutdownAsync(ShutdownTimeout).GetAwaiter().GetResult();
    }

    private static void OnSettingsChanged(object? sender, EventArgs eventArgs) => Apply();

    private static void Apply()
    {
        var configuration = SqlMemoryConfiguration.From(SqlAssistSettingsStore.Current);

        // 走 Begin 而不是 BeginProbe：開不起來就是使用者打開了設定卻什麼都沒記到，
        // 那要看得見，不是可有可無的探測。開檔與建立 AppDomain 都在這條背景路徑上。
        SqlAssistPlatformGuard.Begin(configuration.Enabled ? "啟用 SQL Memory" : "停用 SQL Memory",
            () => Runtime.ApplyAsync(configuration),
            NotificationKind.Package, NotificationOrigin.Ambient, NotificationLevel.Info,
            document: string.Empty);
    }

    private static void OnCaptureDropped(object? sender, SqlCaptureDroppedEventArgs drop)
    {
        if (drop.ShouldNotify && Volatile.Read(ref _package) is { } package)
            SqlAssistStatusBar.Show(package, drop.NotificationText);
    }

    private static async Task<ISqlMemoryStore> OpenStorageAsync(CancellationToken cancellationToken) =>
        await IsolatedSqlMemoryStore
            .OpenAsync(DatabasePath(), AppDomain.CurrentDomain.BaseDirectory, cancellationToken)
            .ConfigureAwait(false);

    private static string DatabasePath() => Path.Combine(
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
