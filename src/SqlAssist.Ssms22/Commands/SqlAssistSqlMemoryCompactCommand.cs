using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.SqlMemory;

namespace SqlAssist.Ssms22.Commands;

/// <summary>
/// 設定頁上的「立即整理資料庫檔案…」。
/// </summary>
/// <remarks>
/// 完整 <c>VACUUM</c> 重建整個資料庫，時間隨資料量成長，所以是使用者按的一次性命令
/// 而不是背景排程的一環。背景整理只做 WAL 截斷，那個便宜且可重複。
///
/// 整理前先排空本程序已接受的擷取；整理期間擷取照常排隊，等整理結束才寫入，
/// 佇列滿時照一般規則拒收並提示。文案只能承諾這些，不能說「期間寫入不受影響」。
///
/// 使用者主動觸發的命令自己顯示成敗，不交給 <see cref="SqlAssistPlatformGuard"/>
/// 靜默吞掉——按了沒反應與按了失敗是兩件不同的事。
/// </remarks>
internal static class SqlAssistSqlMemoryCompactCommand
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
            SqlAssistStatusBar.Show(package, "正在整理 SQL Memory 的資料庫檔案；可繼續編輯，新的紀錄會在整理完成後寫入。");
            var usage = await SqlMemoryHost.Runtime.CompactAsync(package.DisposalToken).ConfigureAwait(false);
            message = "SQL Memory 的資料庫已整理完成。\n" +
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
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 手動整理失敗：{error}");
            message = (error is SqlMemoryStorageException { IsTransient: true }
                ? "SQL Memory 的資料庫正被其他作業使用，這次沒有整理；請稍後再試。\n"
                : "無法整理 SQL Memory 的資料庫：\n") + error.Message;
            icon = OLEMSGICON.OLEMSGICON_WARNING;
        }

        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            SqlAssistStatusBar.Show(package, icon == OLEMSGICON.OLEMSGICON_INFO
                ? "SQL Memory 的資料庫已整理完成。"
                : "SQL Memory 的資料庫整理失敗。");
            VsShellUtilities.ShowMessageBox(package, message, "SqlAssist — SQL Memory", icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
        catch (OperationCanceledException) when (package.DisposalToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // 結果顯示也可能在殼層退場時失敗；不得留下未觀察的背景例外。
            SqlAssistDiagnostics.WriteAlways($"無法顯示 SQL Memory 整理結果：{error}");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
}
