using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core.Notifications;
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
/// 進度與成功走通知卡片：整理可能跑很久，而使用者多半已經回去編輯 SQL，
/// 這時彈出訊息框只是打斷他。失敗仍用訊息框——那一句要讀完才知道下一步是稍後再試
/// 還是先處理占用資料庫的程序，而卡片會自己消失。
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
        try
        {
            string? failure;
            using (var notification = NotificationCenter.Default.Begin(NotificationCatalog.CompactingSqlMemory,
                       NotificationKind.SqlMemory, NotificationOrigin.User, NotificationLevel.Info))
            {
                failure = await CompactAsync(package, notification).ConfigureAwait(false);
            }

            // 成功不跳訊息框：卡片已經寫了「已整理 SQL Memory」與整理後的檔案大小。
            if (failure is null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            VsShellUtilities.ShowMessageBox(package, failure, "SqlAssist — SQL Memory", OLEMSGICON.OLEMSGICON_WARNING,
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

    /// <returns>要顯示在訊息框裡的失敗說明；成功與取消為 null。</returns>
    private static async Task<string?> CompactAsync(SqlAssistPackage package, NotificationScope notification)
    {
        try
        {
            var usage = await SqlMemoryHost.Runtime.CompactAsync(package.DisposalToken).ConfigureAwait(false);
            notification.Report("資料庫檔案 " + SqlMemoryUsageSummary.Bytes(usage.DatabaseFileBytes) +
                " · 查詢內容 " + SqlMemoryUsageSummary.Bytes(usage.ContentBytes));
            return null;
        }
        catch (OperationCanceledException) when (package.DisposalToken.IsCancellationRequested)
        {
            notification.Cancel();
            return null;
        }
        catch (Exception error)
        {
            notification.Fail();
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 手動整理失敗：{error}");
            return (error is SqlMemoryStorageException { IsTransient: true }
                ? "SQL Memory 的資料庫正被其他作業使用，這次沒有整理；請稍後再試。\n"
                : "無法整理 SQL Memory 的資料庫：\n") + error.Message;
        }
    }
}
