using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 用量分頁的接線：讀快照、執行整理動作、顯示進度與結果。畫面在 <see cref="SqlMemoryUsageView"/>，文案在 Core。
/// </summary>
/// <remarks>
/// 讀取以取消與宿主世代擋住晚到的結果；整理動作走 <see cref="SqlMemoryOperationGate"/>，連按只送出一次，
/// 停用或換了儲存之後才回來的結果不回報。整理結束一律重讀快照，量表從舊值滑到新值。
///
/// 回饋分兩條：分頁內的不確定進度說「現在動不了」，通知卡片說「這件事怎麼了」。維護與清除可能跑上幾分鐘，
/// 使用者多半已經切回編輯器，所以成敗都走卡片（卡片跟著作用中的宿主走）；分頁狀態列只留卡片放不下的東西，
/// 例如備份檔與自我測試報告的位置——通知文案不放路徑。
/// </remarks>
internal sealed class SqlMemoryUsagePanel : IDisposable
{
    private readonly SqlAssistPackage _package;
    private readonly Action<string> _report;
    private readonly SqlMemoryOperationGate _gate = new();
    private CancellationTokenSource _load = new();
    private readonly CancellationTokenSource _operation = new();
    private bool _disposed;

    public SqlMemoryUsagePanel(SqlAssistPackage package, Action<string> report)
    {
        _package = package;
        _report = report;
        // 診斷卡片跟著「寫入詳細診斷紀錄」走；工具窗建立時決定一次，不隨設定即時增刪卡片。
        View = new SqlMemoryUsageView(SqlAssistSettingsStore.Current.VerboseLogging);
        View.ActionRequested += (_, action) => SqlMemoryActions.Run(() => Run(action), report);
    }

    public SqlMemoryUsageView View { get; }

    /// <summary>維護或清除結束（不論成敗）：History／Favorites 已載入的列可能有被刪掉的，清單回到畫面時要重讀。</summary>
    public event EventHandler? RecordsChanged;

    public void Reload()
    {
        if (_disposed) return;
        _load.Cancel(); _load.Dispose(); _load = new CancellationTokenSource();
        var token = _load.Token;
        // 沒有可讀的儲存時，原因已經在工具窗的宿主狀態與狀態列，分頁本身不再重講一次。
        if (!SqlMemoryHost.Runtime.IsAvailable) { View.Clear(); return; }
        View.BeginLoad();
        var generation = SqlMemoryHost.Runtime.Generation;
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            try
            {
                var snapshot = await SqlMemoryHost.Runtime.ReadUsageSnapshotAsync(token);
                if (!SqlMemoryOperationGate.IsCurrent(generation, token) || _disposed) return;
                View.ShowSummary(SqlMemoryUsageSummary.Create(snapshot, DateTimeOffset.UtcNow));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (SqlMemoryOperationGate.IsCurrent(generation, token) && !_disposed)
                    View.ShowFailure(SqlMemoryTimeText.Failure("讀取用量", error));
            }
        }, _report);
    }

    /// <summary>離開用量分頁或隱藏工具窗：停止讀取；進行中的整理不中斷，完成後照常寫回清理紀錄。</summary>
    public void Suspend() => _load.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _load.Cancel(); _load.Dispose();
        _operation.Cancel(); _operation.Dispose();
    }

    private void Run(SqlMemoryUsageAction action)
    {
        switch (action)
        {
            case SqlMemoryUsageAction.Maintain:
                Start(NotificationCatalog.MaintainingSqlMemory, "維護", "正在依保留規則維護…", deletes: true, async progress =>
                {
                    var result = await SqlMemoryHost.Runtime.MaintainNowAsync(progress, _operation.Token);
                    return (Deleted(result), null);
                });
                break;
            case SqlMemoryUsageAction.Cleanup:
                if (SqlMemoryCleanupWindow.Show(_package) is not { } request) return;
                Start(NotificationCatalog.ClearingSqlMemoryHistory, "清除", "正在清除紀錄…", deletes: true, async progress =>
                {
                    var result = await SqlMemoryHost.Runtime.CleanupAsync(request, progress, _operation.Token);
                    return (Deleted(result), null);
                });
                break;
            case SqlMemoryUsageAction.Compact:
                if (!SqlAssistConfirmationWindow.Confirm(Owner(), "壓縮 SQL Memory 資料庫", "重建資料庫檔案以縮小檔案？",
                    "不會刪除任何紀錄。重建期間需要與資料庫等量的暫存空間，新的擷取會等壓縮完成才寫入。", "壓縮")) return;
                Start(NotificationCatalog.CompactingSqlMemory, "壓縮", "正在壓縮資料庫；可繼續編輯…", deletes: false, async _ =>
                {
                    // 縮小了多少由清理紀錄記下；這裡只說結果，不為了算差值多讀一次完整用量。
                    var after = await SqlMemoryHost.Runtime.CompactAsync(_operation.Token);
                    return ("資料庫檔案現在是 " + SqlMemoryUsageSummary.Bytes(after.DatabaseFileBytes) + "。", null);
                });
                break;
            case SqlMemoryUsageAction.Backup:
                if (AskBackupPath() is not { } path) return;
                Start(NotificationCatalog.BackingUpSqlMemory, "備份", "正在備份資料庫…", deletes: false, async _ =>
                {
                    // 位置只寫在分頁狀態列：通知文案不放路徑，而使用者要知道檔案落在哪裡。
                    var length = await SqlMemoryHost.Runtime.BackupAsync(path, _operation.Token);
                    return ("備份檔 " + SqlMemoryUsageSummary.Bytes(length), "已備份到 " + path + "。");
                });
                break;
            case SqlMemoryUsageAction.OpenFolder:
                SqlMemoryRecoveryService.OpenDatabaseFolder();
                break;
            case SqlMemoryUsageAction.SelfTest:
                // 與維護、清除互斥：自我測試自己開一份隔離儲存，跟維護搶同一組原生資源。
                Start(NotificationCatalog.TestingSqlMemoryStorage, "自我測試", "正在測試儲存；可繼續編輯…", deletes: false, async _ =>
                {
                    var directory = SelfTestDirectory();
                    SqlAssistDiagnostics.WriteAlways(
                        $"SQL Memory 自我測試開始；版本 {SqlAssistPackage.PackageVersion}；目錄：{directory}");
                    // 宿主的 ApplicationBase 是目前 SSMS IDE 目錄，不寫死安裝版號或路徑。
                    await SqlMemoryStorageSelfTest.RunAsync(directory, AppDomain.CurrentDomain.BaseDirectory, _operation.Token);
                    // 報告是要讀的檔案，位置只有狀態列放得下；卡片只說通過了什麼。
                    return ("已驗證寫入、重送、重新開啟與隔離層卸載。",
                        "報告：" + Path.Combine(directory, SqlMemoryStorageSelfTest.ReportFileName));
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <param name="title"><see cref="NotificationCatalog"/> 的常數標題；成敗都由卡片回報。</param>
    /// <param name="verb">失敗訊息的動作名稱。</param>
    /// <param name="deletes">會刪除紀錄；結束後通知清單重讀，壓縮與備份不動紀錄就不讓清單白讀一次。</param>
    /// <param name="work">背景部分；回傳卡片上的結果短語，以及只有分頁狀態列放得下的那一句（多半沒有）。</param>
    private void Start(string title, string verb, string busy, bool deletes,
        Func<IProgress<long>, Task<(string Note, string? Status)>> work)
    {
        if (_gate.IsBusy || _disposed) return;
        View.SetBusy(busy);
        _report("");
        // Progress 在建立它的 UI 執行緒上回報，批次之間更新進度文字不必另外排派送。
        var progress = new Progress<long>(rows =>
        {
            if (!_disposed && rows > 0) View.SetBusy(busy.TrimEnd('…') + "，已刪除 " + SqlMemoryUsageSummary.Count(rows) + " 列…");
        });
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            // 卡片跟著作用中的宿主走：整理途中關掉工具窗，結果會出現在編輯區或下一個 SqlAssist 視窗上。
            using var notification = NotificationCenter.Default.Begin(title,
                NotificationKind.SqlMemory, NotificationOrigin.User, NotificationLevel.Info);
            try
            {
                await _gate.RunAsync(_operation.Token, Failed, verb, async () =>
                {
                    var (note, status) = await work(progress);
                    return () =>
                    {
                        notification.Report(note);
                        if (status is not null) _report(status);
                    };
                });
            }
            catch (OperationCanceledException)
            {
                // 關掉工具窗或換了儲存：卡片不能停在「已完成」，那一件事並沒有做完。
                notification.Cancel();
                throw;
            }
            finally
            {
                if (!_disposed)
                {
                    View.SetBusy(null);
                    Reload();
                    if (deletes) RecordsChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            void Failed(string message)
            {
                notification.Report(message);
                notification.Fail();
            }
        }, _report);
    }

    /// <summary>每一次跑各自一個資料夾：失敗那一份要留著給診斷，不能被下一次覆寫。</summary>
    private static string SelfTestDirectory() => Path.Combine(
        Path.GetDirectoryName(SqlAssistDiagnostics.LogPath) ?? string.Empty,
        "SqlMemorySelfTest", Guid.NewGuid().ToString("N"));

    private static string Deleted(SqlMemoryCleanupResult result) => result.DeletedRows == 0
        ? "沒有需要回收的資料。"
        : "刪除 " + SqlMemoryUsageSummary.Count(result.DeletedRows) + " 列" +
          (result.ReleasedContentBytes > 0 ? "，釋出 " + SqlMemoryUsageSummary.Bytes(result.ReleasedContentBytes) : "") +
          "。檔案要壓縮後才會變小。";

    /// <summary>選備份位置；同名檔案由原生對話框確認覆寫，確認後才移除舊檔，儲存層本身不覆寫任何檔案。</summary>
    private string? AskBackupPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "備份 SQL Memory 資料庫",
            FileName = "SQLMemory-" + DateTime.Now.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".db",
            DefaultExt = ".db",
            Filter = "SQLite 資料庫 (*.db)|*.db|所有檔案 (*.*)|*.*",
            OverwritePrompt = true,
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(Owner()) != true) return null;
        var path = dialog.FileName;
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(SqlMemoryHost.DatabasePath()), StringComparison.OrdinalIgnoreCase))
        {
            _report("備份不能覆寫目前使用中的資料庫；請選擇其他位置。");
            return null;
        }
        if (File.Exists(path)) File.Delete(path);
        return path;
    }

    private Window Owner() => Window.GetWindow(View) ?? Application.Current?.MainWindow
        ?? throw new InvalidOperationException("找不到 SSMS 主視窗，無法開啟對話框。");
}
