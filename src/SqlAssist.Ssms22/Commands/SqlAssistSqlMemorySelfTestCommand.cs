using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.SqlMemory.Isolation;

namespace SqlAssist.Ssms22.Commands;

internal static class SqlAssistSqlMemorySelfTestCommand
{
    private static int _running;
    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    public static void Execute(SqlAssistPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        // 使用者主動觸發的命令自行顯示失敗，不交給 Guard 靜默吞掉。
        _ = ExecuteAsync(package);
    }

    private static async Task ExecuteAsync(SqlAssistPackage package)
    {
        string? runDirectory = null;
        string message;
        var icon = OLEMSGICON.OLEMSGICON_INFO;
        try
        {
            SqlAssistStatusBar.Show(package, "正在測試 SQL Memory 儲存；不會擷取目前查詢。");
            runDirectory = Path.Combine(Path.GetDirectoryName(SqlAssistDiagnostics.LogPath), "SqlMemorySelfTest", Guid.NewGuid().ToString("N"));
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 自我測試開始；版本 {SqlAssistPackage.PackageVersion}；目錄：{runDirectory}");
            // 宿主的 ApplicationBase 是目前 SSMS IDE 目錄，不寫死安裝版號或路徑。
            await SqlMemoryStorageSelfTest.RunAsync(runDirectory, AppDomain.CurrentDomain.BaseDirectory, package.DisposalToken);
            message = "SQL Memory 儲存自我測試通過。\n已驗證寫入、重送、重新開啟與隔離層卸載。\n未啟用 SQL 擷取；請再確認 SSMS 既有功能正常。";
        }
        catch (OperationCanceledException) when (package.DisposalToken.IsCancellationRequested)
        {
            SqlAssistDiagnostics.WriteAlways("SQL Memory 自我測試因套件卸載而停止。");
            Interlocked.Exchange(ref _running, 0);
            return;
        }
        catch (Exception error)
        {
            SqlAssistDiagnostics.WriteAlways($"SQL Memory 自我測試失敗：{error}");
            message = "SQL Memory 儲存自我測試失敗：\n" + error.Message + "\n未啟用 SQL 擷取；請提供診斷報告。";
            icon = OLEMSGICON.OLEMSGICON_CRITICAL;
        }

        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (runDirectory != null) message += "\n\n報告（若已成功建立）：\n" + Path.Combine(runDirectory, SqlMemoryStorageSelfTest.ReportFileName);
            SqlAssistDiagnostics.WriteAlways(message);
            SqlAssistStatusBar.Show(package, icon == OLEMSGICON.OLEMSGICON_INFO ? "SQL Memory 自我測試通過。" : "SQL Memory 自我測試失敗，請查看報告。");
            VsShellUtilities.ShowMessageBox(package, message, "SqlAssist — SQL Memory 自我測試", icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
        catch (OperationCanceledException) when (package.DisposalToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // 結果顯示也可能在殼層退場時失敗；不得留下未觀察的背景例外。
            SqlAssistDiagnostics.WriteAlways($"無法顯示 SQL Memory 自我測試結果：{error}");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
