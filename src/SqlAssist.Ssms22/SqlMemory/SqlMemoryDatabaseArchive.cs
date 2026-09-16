using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 把一個 SQLite 資料庫的三個檔案（主庫、<c>-wal</c>、<c>-shm</c>）當成一組更名封存。
/// </summary>
/// <remarks>
/// 只用 <see cref="File.Move"/>：同一個磁碟區上搬的是目錄項目，不複製內容，
/// 空間不足也不會留下半份資料。
///
/// 三個檔案必須一起走。只搬主庫，新建立的乾淨資料庫下一次開啟就會套上舊的 WAL，
/// 等於把損毀原封不動帶進新檔案；主庫已經不在、只剩 <c>-wal</c> 或 <c>-shm</c> 的殘局
/// 同樣要封存，否則「重建」之後第一次開檔又壞一次。
///
/// 中途失敗一律把已經搬走的搬回原位：一半在備份名稱、一半在原名稱的資料夾，
/// 使用者無從判斷哪一份才是現行資料庫。
/// </remarks>
internal static class SqlMemoryDatabaseArchive
{
    /// <summary>WAL 模式下實際用到的三個檔案；順序就是搬移順序，主庫先走。</summary>
    private static readonly string[] Suffixes = { "", "-wal", "-shm" };

    /// <summary>還有東西可以封存；只剩 WAL 或 SHM 也算。</summary>
    public static bool Exists(string databasePath) =>
        !string.IsNullOrWhiteSpace(databasePath) && AnyExists(databasePath);

    /// <summary>
    /// 把現有的資料庫檔案整組更名為 <c>&lt;名稱&gt;.bak.&lt;時間戳&gt;.db</c>。
    /// </summary>
    /// <param name="now">備份名稱的時間戳來源；同一秒內的第二次備份以流水號區隔。</param>
    /// <returns>備份主庫的完整路徑；本來就沒有檔案可封存時是空字串。</returns>
    /// <exception cref="InvalidOperationException">檔案被鎖定或權限不足；訊息已是可以直接顯示的一句話。</exception>
    public static string Archive(string databasePath, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("資料庫路徑不可為空。", nameof(databasePath));

        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("資料庫路徑必須包含目錄。", nameof(databasePath));

        Directory.CreateDirectory(directory);
        if (!AnyExists(databasePath)) return string.Empty;

        var backupPath = NextBackupPath(directory!, databasePath, now);
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var suffix in Suffixes)
            {
                var from = databasePath + suffix;
                if (!File.Exists(from)) continue;
                File.Move(from, backupPath + suffix);
                moved.Add((from, backupPath + suffix));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(Explain(error, RollBack(moved)), error);
        }

        return backupPath;
    }

    private static bool AnyExists(string path)
    {
        foreach (var suffix in Suffixes)
            if (File.Exists(path + suffix)) return true;
        return false;
    }

    /// <remarks>時間戳用不變文化：曆法跟著使用者走的話，佛曆或民國年會讓備份排序看起來像另一份資料。</remarks>
    private static string NextBackupPath(string directory, string databasePath, DateTimeOffset now)
    {
        var baseName = Path.GetFileNameWithoutExtension(databasePath);
        var extension = Path.GetExtension(databasePath);
        var stamp = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{baseName}.bak.{stamp}{extension}");
        for (var counter = 1; AnyExists(candidate); counter++)
            candidate = Path.Combine(directory, $"{baseName}.bak.{stamp}_{counter}{extension}");
        return candidate;
    }

    /// <summary>把已經搬走的搬回原位。</summary>
    /// <returns>全部回到原名稱為 <c>true</c>。</returns>
    /// <remarks>
    /// 這裡吞掉回復本身的失敗：真正要讓使用者看到的是第一個錯誤（檔案被誰鎖住），
    /// 拿回復的錯誤蓋掉它只會讓原因消失。回復不完全的事實由訊息補一句，不靜靜略過。
    /// </remarks>
    private static bool RollBack(List<(string From, string To)> moved)
    {
        var restored = true;
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            try { File.Move(moved[i].To, moved[i].From); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { restored = false; }
        }

        return restored;
    }

    private static string Explain(Exception error, bool restored)
    {
        var reason = error is UnauthorizedAccessException
            ? "存取 SQL Memory 資料庫目錄遭拒，未完成備份。請確認資料夾權限後再試一次。"
            : "無法搬移 SQL Memory 資料庫檔案；檔案可能正被其他 SSMS 執行個體鎖定。請關閉其他 SSMS 後再試一次。";
        return restored ? reason : reason + "\n有檔案已更名且無法搬回，請從資料夾確認哪一份是現行資料庫。";
    }
}
