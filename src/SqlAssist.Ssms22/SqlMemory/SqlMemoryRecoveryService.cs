using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 資料庫開不起來時的復原接線：封存舊檔案、要求宿主重新開一個乾淨的，以及開啟檔案位置。
/// </summary>
/// <remarks>
/// 搬檔案本身是純檔案系統邏輯，在 <see cref="SqlMemoryDatabaseArchive"/>，那裡跑得起單元測試；
/// 這一層只負責路徑、背景執行緒與重新套用設定。
/// </remarks>
internal static class SqlMemoryRecoveryService
{
    public static string DatabasePath => SqlMemoryHost.DatabasePath();

    /// <summary>還有檔案可以封存；主庫不在、只剩 WAL 也要走同一條路。</summary>
    public static bool CanRecover => SqlMemoryDatabaseArchive.Exists(DatabasePath);

    /// <summary>
    /// 把現有資料庫（含 wal／shm）整組更名封存，再要求宿主以全新 schema 重建。
    /// </summary>
    /// <returns>備份檔案的路徑；本來就沒有檔案可封存時是空字串。</returns>
    public static async Task<string> BackupAndRecreateAsync()
    {
        var databasePath = DatabasePath;
        var backupPath = await Task.Run(() => SqlMemoryDatabaseArchive.Archive(databasePath, DateTimeOffset.Now))
            .ConfigureAwait(false);

        await SqlMemoryHost.ReapplyAsync().ConfigureAwait(false);
        return backupPath;
    }

    /// <summary>在檔案總管中顯示資料庫檔案；檔案不在就開資料夾本身。</summary>
    public static void OpenDatabaseFolder()
    {
        var databasePath = DatabasePath;
        if (File.Exists(databasePath))
        {
            using var reveal = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{databasePath}\"")
            {
                UseShellExecute = true
            });
            return;
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("無法取得 SQL Memory 資料庫目錄。");

        // 重建之前資料夾可能還不存在；開一個空資料夾比按了沒反應清楚，備份與新資料庫也都落在這裡。
        Directory.CreateDirectory(directory);
        using var open = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
}
