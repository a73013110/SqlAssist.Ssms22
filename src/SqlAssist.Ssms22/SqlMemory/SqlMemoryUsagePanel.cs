using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 用量分頁的接線：讀快照、執行整理動作、顯示進度與結果。畫面在 <see cref="SqlMemoryUsageView"/>，文案在 Core。
/// </summary>
/// <remarks>
/// 讀取以取消與宿主世代擋住晚到的結果；整理動作走 <see cref="SqlMemoryOperationGate"/>，連按只送出一次，
/// 停用或換了儲存之後才回來的結果不回報。整理結束一律重讀快照，量表從舊值滑到新值。
/// 使用者按了卻失敗必須看得見，所以錯誤回到工具窗狀態列，不交給 Guard。
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
        View.ActionRequested += (_, action) => SqlMemoryActions.Run(() => Run(action), report);
    }

    public SqlMemoryUsageView View { get; } = new();

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
                Start("維護", "正在依保留規則維護…", deletes: true, async progress =>
                {
                    var result = await SqlMemoryHost.Runtime.MaintainNowAsync(progress, _operation.Token);
                    return Deleted("維護完成", result);
                });
                break;
            case SqlMemoryUsageAction.Cleanup:
                if (SqlMemoryCleanupWindow.Show(_package) is not { } request) return;
                Start("清除", "正在清除紀錄…", deletes: true, async progress =>
                {
                    var result = await SqlMemoryHost.Runtime.CleanupAsync(request, progress, _operation.Token);
                    return Deleted("清除完成", result);
                });
                break;
            case SqlMemoryUsageAction.Compact:
                if (!SqlAssistConfirmationWindow.Confirm(Owner(), "壓縮 SQL Memory 資料庫", "重建資料庫檔案以縮小檔案？",
                    "不會刪除任何紀錄。重建期間需要與資料庫等量的暫存空間，新的擷取會等壓縮完成才寫入。", "壓縮")) return;
                Start("壓縮", "正在壓縮資料庫；可繼續編輯…", deletes: false, async _ =>
                {
                    // 縮小了多少由清理紀錄記下；狀態列只說結果，不為了算差值多讀一次完整用量。
                    var after = await SqlMemoryHost.Runtime.CompactAsync(_operation.Token);
                    return "壓縮完成：資料庫檔案現在是 " + SqlMemoryUsageSummary.Bytes(after.DatabaseFileBytes) + "。";
                });
                break;
            case SqlMemoryUsageAction.Backup:
                if (AskBackupPath() is not { } path) return;
                Start("備份", "正在備份資料庫…", deletes: false, async _ =>
                {
                    var length = await SqlMemoryHost.Runtime.BackupAsync(path, _operation.Token);
                    return "已備份到 " + path + "（" + SqlMemoryUsageSummary.Bytes(length) + "）。";
                });
                break;
            case SqlMemoryUsageAction.OpenFolder:
                SqlMemoryRecoveryService.OpenDatabaseFolder();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <param name="deletes">會刪除紀錄；結束後通知清單重讀，壓縮與備份不動紀錄就不讓清單白讀一次。</param>
    /// <param name="work">背景部分；回傳完成後寫在狀態列的一行結果。</param>
    private void Start(string verb, string busy, bool deletes, Func<IProgress<long>, Task<string>> work)
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
            try
            {
                await _gate.RunAsync(_operation.Token, _report, verb, async () =>
                {
                    var message = await work(progress);
                    return () => _report(message);
                });
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
        }, _report);
    }

    private static string Deleted(string title, SqlMemoryCleanupResult result) => result.DeletedRows == 0
        ? title + "：沒有需要回收的資料。"
        : title + "：刪除 " + SqlMemoryUsageSummary.Count(result.DeletedRows) + " 列" +
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
