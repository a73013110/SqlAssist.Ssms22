using System;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 用量頁的一次快照：容量、各類筆數、伺服器分布與時間範圍，在同一個讀取交易內取得。
/// </summary>
/// <remarks>
/// 只放計數與位元組，不放分類容量：內容去重後被 History、版本與收藏共用，
/// 各類容量相加會超過總量，畫面上的比例就是錯的。
/// </remarks>
[Serializable]
public sealed record SqlMemoryUsageReport
{
    public SqlMemoryUsageReport(SqlMemoryUsage usage, long freeBytes, SqlMemoryUsageCounts counts,
        SqlMemoryUsageShare[] servers, DateTimeOffset? oldestAt, DateTimeOffset? newestAt)
    {
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        if (freeBytes < 0) throw new ArgumentOutOfRangeException(nameof(freeBytes));
        FreeBytes = freeBytes;
        Counts = counts ?? throw new ArgumentNullException(nameof(counts));
        Servers = servers ?? throw new ArgumentNullException(nameof(servers));
        OldestAt = oldestAt;
        NewestAt = newestAt;
    }

    public SqlMemoryUsage Usage { get; }

    /// <summary>資料庫檔案內已釋出、可由壓縮歸還檔案系統的頁面位元組；刪除資料不會自動縮檔。</summary>
    public long FreeBytes { get; }

    public SqlMemoryUsageCounts Counts { get; }

    /// <summary>History 依伺服器分組、筆數由多到少的前幾名；沒有連線的列不計入。</summary>
    public SqlMemoryUsageShare[] Servers { get; }

    /// <summary>History 最早與最新一列的時間；沒有 History 時為 null。</summary>
    public DateTimeOffset? OldestAt { get; }

    public DateTimeOffset? NewestAt { get; }
}

/// <param name="ExecutionEntries">History 上的執行列；連續相同的執行合併為一列。</param>
/// <param name="ExecutionEvents">逐次保存的執行事件，執行筆數配額以它計算。</param>
/// <param name="Drafts">History 上的草稿列，含未存檔回復內容的投影。</param>
/// <param name="RecoveryItems">未存檔回復內容。</param>
/// <param name="OpenRecoveryItems">仍有程序心跳租約、視窗可能還開著的回復內容；手動清理不會動它們。</param>
/// <param name="LargestFavoriteRevisions">單一收藏最多保存幾個版本，對照每收藏版本配額。</param>
[Serializable]
public sealed record SqlMemoryUsageCounts(long ExecutionEntries, long ExecutionEvents, long Drafts, long RecoveryItems,
    long OpenRecoveryItems, long Sessions, long Favorites, long FavoriteRevisions, long LargestFavoriteRevisions, long Contents);

[Serializable]
public sealed record SqlMemoryUsageShare(string Name, long Count);
