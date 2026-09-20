using System;

namespace SqlAssist.Core.SqlMemory;

/// <summary>手動清理的對象；可以複選。</summary>
[Flags]
public enum SqlMemoryCleanupTargets
{
    None = 0,

    /// <summary>History 上的執行列與它底下的執行事件。</summary>
    Executions = 1,

    /// <summary>History 上已有版本的草稿列。</summary>
    Drafts = 2,

    /// <summary>沒有程序心跳租約的未存檔回復內容；租約還在代表視窗可能還開著，一律不動。</summary>
    ClosedRecovery = 4,

    /// <summary>每個收藏只留最新 N 個版本；目前版本是保護根，永遠保留。</summary>
    FavoriteRevisions = 8,
}

/// <summary>
/// 使用者從用量頁送出的一次清理：History 類對象依期間與連線篩選，收藏版本依保留數。
/// </summary>
/// <remarks>
/// 刪除語意與使用者逐筆刪除 History 相同，保護根與引用清單和維護共用；
/// 收藏版本走一次性的維護批次，不寫共用維護輪次。
/// </remarks>
[Serializable]
public sealed class SqlMemoryCleanupRequest
{
    public SqlMemoryCleanupRequest(SqlMemoryCleanupTargets targets, DateTimeOffset? before = null, string? server = null,
        string? database = null, int keepFavoriteRevisions = 10, bool onlyBlank = false)
    {
        const SqlMemoryCleanupTargets all = SqlMemoryCleanupTargets.Executions | SqlMemoryCleanupTargets.Drafts |
            SqlMemoryCleanupTargets.ClosedRecovery | SqlMemoryCleanupTargets.FavoriteRevisions;
        if (targets == SqlMemoryCleanupTargets.None || (targets & ~all) != 0) throw new ArgumentOutOfRangeException(nameof(targets));
        // 收藏清成沒有 SQL 不是清理；目前版本另受保護，但至少留一個讓語意與畫面一致。
        if (keepFavoriteRevisions < 1) throw new ArgumentOutOfRangeException(nameof(keepFavoriteRevisions));
        Targets = targets;
        Before = before?.ToUniversalTime();
        Server = Normalize(server);
        Database = Normalize(database);
        KeepFavoriteRevisions = keepFavoriteRevisions;
        OnlyBlank = onlyBlank;
    }

    public SqlMemoryCleanupTargets Targets { get; }

    /// <summary>只清早於這個 UTC 時間的 History 列；null 表示不限期間。收藏版本不看這個值。</summary>
    public DateTimeOffset? Before { get; }

    /// <summary>精確比對 History 的伺服器；null 表示所有連線。</summary>
    public string? Server { get; }

    public string? Database { get; }

    public int KeepFavoriteRevisions { get; }

    /// <summary>
    /// 只清空白的 SQL：內容是空的或全部都是空白字元。收藏版本不看這個值。
    /// </summary>
    /// <remarks>
    /// 與勾選的對象是<b>且</b>的關係，不是第五種對象：空白的草稿同時也是一筆草稿，
    /// 做成對象的話它會在兩處各算一次，而試算的總數就不是實際要刪的筆數。
    ///
    /// 留這個開關是因為擷取端已經不再產生空白列（<see cref="SqlContent.IsBlank"/>），
    /// 但在那之前留下來的還在清單上——而「清掉草稿」會連有內容的草稿一起帶走，
    /// 那不是使用者要的。
    /// </remarks>
    public bool OnlyBlank { get; }

    public bool Includes(SqlMemoryCleanupTargets target) => (Targets & target) == target;

    /// <summary>History 類對象；收藏版本不在 History 上，另走維護批次。</summary>
    public bool TouchesHistory => (Targets & (SqlMemoryCleanupTargets.Executions | SqlMemoryCleanupTargets.Drafts |
        SqlMemoryCleanupTargets.ClosedRecovery)) != 0;

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
}

/// <summary>
/// 送出前的試算：符合條件的列數。是上限而不是保證——共用內容與保護根在刪除時才逐筆重查，
/// 所以實際釋出的容量無法事先算準。
/// </summary>
[Serializable]
public sealed record SqlMemoryCleanupEstimate(long ExecutionEntries, long Drafts, long RecoveryItems, long FavoriteRevisions)
{
    public long Total => ExecutionEntries + Drafts + RecoveryItems + FavoriteRevisions;
}

/// <summary>一個有界交易的 History 清理結果；Cursor 為 null 表示已巡完。</summary>
[Serializable]
public sealed record SqlMemoryCleanupBatch(int Examined, int DeletedEntries, int DeletedRows, string? Cursor);

/// <summary>整次清理的結果；容量是清理後的觀測值，檔案要壓縮後才會變小。</summary>
public sealed record SqlMemoryCleanupResult(long DeletedEntries, long DeletedRows, long ReleasedContentBytes, SqlMemoryUsage Usage);
