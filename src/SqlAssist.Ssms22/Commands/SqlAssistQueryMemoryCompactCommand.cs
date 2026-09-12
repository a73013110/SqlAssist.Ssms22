using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Ssms22.QueryMemory;

namespace SqlAssist.Ssms22.Commands;

/// <summary>
/// 設定頁上的「立即整理資料庫檔案…」。
/// </summary>
/// <remarks>
/// 完整 <c>VACUUM</c> 重建整個資料庫，時間隨資料量成長，所以是使用者按的一次性命令
/// 而不是背景排程的一環。背景整理只做 WAL 截斷，那個便宜且可重複。
///
/// 使用者主動觸發的命令自己顯示成敗，不交給 <see cref="SqlAssistPlatformGuard"/>
/// 靜默吞掉——按了沒反應與按了失敗是兩件不同的事。
/// </remarks>
internal static class SqlAssistQueryMemoryCompactCommand
{
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    public static void Execute(SqlAssistPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = ExecuteAsync(package);
    }

    private static async Task ExecuteAsync(SqlAssistPackage package)
    {
        string message;
        var icon = OLEMSGICON.OLEMSGICON_INFO;

        try
        {
            SqlAssistStatusBar.Show(package, "正在整理查詢記憶的資料庫檔案；期間仍可繼續編輯。");
            var usage = await QueryMemoryHost.CompactAsync(package.DisposalToken).ConfigureAwait(false);
            message = "查詢記憶的資料庫已整理完成。\n" +
                $"資料庫檔案：{Megabytes(usage.DatabaseFileBytes)}\n" +
                $"查詢內容：{Megabytes(usage.ContentBytes)}\n" +
                "整理只回收已刪除資料佔用的空間，不會刪掉任何還留著的查詢。";
        }
        catch (OperationCanceledException) when (package.DisposalToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _running, 0);
            return;
        }
        catch (Exception error)
        {
            SqlAssistDiagnostics.WriteAlways($"查詢記憶手動整理失敗：{error}");
            message = "無法整理查詢記憶的資料庫：\n" + error.Message;
            icon = OLEMSGICON.OLEMSGICON_WARNING;
        }

        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            SqlAssistStatusBar.Show(package, icon == OLEMSGICON.OLEMSGICON_INFO
                ? "查詢記憶的資料庫已整理完成。"
                : "查詢記憶的資料庫整理失敗。");
            VsShellUtilities.ShowMessageBox(package, message, "SqlAssist — 查詢記憶", icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
        catch (OperationCanceledException) when (package.DisposalToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // 結果顯示也可能在殼層退場時失敗；不得留下未觀察的背景例外。
            SqlAssistDiagnostics.WriteAlways($"無法顯示查詢記憶整理結果：{error}");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
}
