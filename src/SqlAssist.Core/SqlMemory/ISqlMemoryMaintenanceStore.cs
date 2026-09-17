using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

/// <summary>維護與擷取分離；宿主逐批派送，不在編輯器熱路徑同步清理。</summary>
public interface ISqlMemoryMaintenanceStore
{
    Task<SqlMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken);
    Task<SqlMemoryMaintenanceResult> MaintainAsync(SqlMemoryMaintenanceRequest request, CancellationToken cancellationToken);

    /// <summary>讀出跨程序共用的維護輪次；null 表示還沒有任何帶 <see cref="SqlMemoryMaintenanceClaim"/> 的批次提交過。</summary>
    Task<SqlMemoryMaintenanceState?> ReadMaintenanceStateAsync(CancellationToken cancellationToken);

    /// <summary>實體整理與有界清理分開；可重複執行，被讀取者擋下時回報未截斷而不中斷他們。</summary>
    Task<SqlMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken);

    /// <summary>重建整個資料庫，時間隨資料量成長；只供設定頁的手動命令，不進排程。</summary>
    Task<SqlMemoryUsage> CompactAsync(CancellationToken cancellationToken);

    /// <summary>用量頁的完整快照；計數會掃描索引，只在使用者開啟或重新整理用量頁時讀，不進排程。</summary>
    Task<SqlMemoryUsageReport> ReadUsageReportAsync(CancellationToken cancellationToken);

    /// <summary>手動清理送出前的符合列數；唯讀，不取得寫鎖。</summary>
    Task<SqlMemoryCleanupEstimate> EstimateCleanupAsync(SqlMemoryCleanupRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 手動清理 History 類對象的一批：一個 IMMEDIATE 交易、最多 <paramref name="limit"/> 列，依 (時間, 鍵) 續讀。
    /// 刪除語意與逐筆刪除 History 相同；收藏版本不在這裡處理。
    /// </summary>
    Task<SqlMemoryCleanupBatch> CleanupHistoryAsync(SqlMemoryCleanupRequest request, string? cursor, int limit,
        CancellationToken cancellationToken);

    /// <summary>以線上備份寫出一份完整的資料庫到不存在的檔案；讀取與擷取照常，不改動原資料庫。</summary>
    Task<long> BackupAsync(string destinationPath, CancellationToken cancellationToken);
}

/// <summary>UTC 半開截止時間；null 停用該類保留清理，容量上限不授權刪除期限內資料。</summary>
[Serializable]
public sealed class SqlRetentionPolicy
{
    /// <summary>筆數配額保留最新 N 筆並與截止時間取聯集；受保護根仍不刪除。null 為不限。</summary>
    public SqlRetentionPolicy(DateTimeOffset? draftBefore, DateTimeOffset? executionBefore, long? maxContentBytes,
        int? maxExecutionEvents = null, int? maxAutoRevisionsPerSession = null, int? maxRevisionsPerFavorite = null,
        DateTimeOffset? recoveryBefore = null)
    {
        if (maxContentBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxContentBytes));
        if (maxExecutionEvents < 0) throw new ArgumentOutOfRangeException(nameof(maxExecutionEvents));
        if (maxAutoRevisionsPerSession < 0) throw new ArgumentOutOfRangeException(nameof(maxAutoRevisionsPerSession));
        if (maxRevisionsPerFavorite < 0) throw new ArgumentOutOfRangeException(nameof(maxRevisionsPerFavorite));
        DraftBefore = draftBefore?.ToUniversalTime();
        ExecutionBefore = executionBefore?.ToUniversalTime();
        MaxContentBytes = maxContentBytes;
        MaxExecutionEvents = maxExecutionEvents;
        MaxAutoRevisionsPerSession = maxAutoRevisionsPerSession;
        MaxRevisionsPerFavorite = maxRevisionsPerFavorite;
        RecoveryBefore = recoveryBefore?.ToUniversalTime();
    }

    public DateTimeOffset? DraftBefore { get; }
    public DateTimeOffset? ExecutionBefore { get; }
    public long? MaxContentBytes { get; }
    public int? MaxExecutionEvents { get; }
    public int? MaxAutoRevisionsPerSession { get; }

    /// <summary>每個收藏保留最新 N 個 SQL 編輯版本，含目前版本；只有此配額能回收它們。</summary>
    public int? MaxRevisionsPerFavorite { get; }

    /// <summary>
    /// 未存檔回復內容的截止時間；只有持有有效心跳租約的宿主才可授權回收。
    /// null 表示完全不憑年齡回收 Recovery。宿主沒有在續 Session 心跳就不該給值，
    /// 否則沒有租約的線上 Session 會被當成遺留資料，使用者還開著的未存檔內容就消失了。
    /// </summary>
    public DateTimeOffset? RecoveryBefore { get; }
}

/// <summary>
/// Indexed 只讀時間索引上可能過期或超額的列，孤立內容／連線由本批刪除列的引用驅動；
/// Full 另外逐鍵巡過全部版本、內容與連線，是索引路徑碰不到的孤立資料的安全網。
/// </summary>
public enum SqlMemoryMaintenanceScan { Indexed, Full }

[Serializable]
public sealed class SqlMemoryMaintenanceRequest
{
    public SqlMemoryMaintenanceRequest(SqlRetentionPolicy policy, int candidateLimit, string? cursor = null,
        SqlMemoryMaintenanceScan scan = SqlMemoryMaintenanceScan.Indexed, SqlMemoryMaintenanceClaim? claim = null)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (candidateLimit < 1 || candidateLimit > 500) throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        if (scan != SqlMemoryMaintenanceScan.Indexed && scan != SqlMemoryMaintenanceScan.Full)
            throw new ArgumentOutOfRangeException(nameof(scan));
        // 游標綁掃描方式；狀態表記的掃描方式與實際跑的不同，下一個程序接續時就會拿錯游標。
        if (claim != null && claim.Round.Scan != scan) throw new ArgumentException("輪次的掃描方式與這一批不同。", nameof(claim));
        CandidateLimit = candidateLimit;
        Cursor = cursor;
        Scan = scan;
        Claim = claim;
    }

    public SqlRetentionPolicy Policy { get; }
    public int CandidateLimit { get; }
    public string? Cursor { get; }
    public SqlMemoryMaintenanceScan Scan { get; }

    /// <summary>null 表示一次性的批次，不讀也不寫共用維護狀態。</summary>
    public SqlMemoryMaintenanceClaim? Claim { get; }
}

/// <summary>
/// 一輪維護的身分：同一份計畫指紋、起點、級數與心跳授權換算出來的政策逐位元組相同，
/// 換程序或重啟後接續同一輪時游標才對得上。
/// </summary>
[Serializable]
public sealed record SqlMemoryMaintenanceRound
{
    public SqlMemoryMaintenanceRound(string planFingerprint, DateTimeOffset startedAt, int level,
        bool reclaimsRecovery, SqlMemoryMaintenanceScan scan, int roundsSinceFullScan)
    {
        if (string.IsNullOrEmpty(planFingerprint)) throw new ArgumentException("缺少保留計畫指紋。", nameof(planFingerprint));
        if (level < 0) throw new ArgumentOutOfRangeException(nameof(level));
        if (scan != SqlMemoryMaintenanceScan.Indexed && scan != SqlMemoryMaintenanceScan.Full)
            throw new ArgumentOutOfRangeException(nameof(scan));
        if (roundsSinceFullScan < 0) throw new ArgumentOutOfRangeException(nameof(roundsSinceFullScan));
        PlanFingerprint = planFingerprint;
        StartedAt = startedAt.ToUniversalTime();
        Level = level;
        ReclaimsRecovery = reclaimsRecovery;
        Scan = scan;
        RoundsSinceFullScan = roundsSinceFullScan;
    }

    public string PlanFingerprint { get; }

    /// <summary>整條保留分級的換算基準；接續同一輪時不重算，輪次邊界才換成當下。</summary>
    public DateTimeOffset StartedAt { get; }

    public int Level { get; }

    /// <summary>這一輪是否帶回復內容的截止時間；只有開輪次時續著心跳的程序才能給 true。</summary>
    public bool ReclaimsRecovery { get; }

    public SqlMemoryMaintenanceScan Scan { get; }

    /// <summary>開這一輪之前已經連續完成幾輪索引巡查。</summary>
    public int RoundsSinceFullScan { get; }
}

/// <summary>最後一批提交時寫回的共用狀態；Cursor 為 null 表示那一輪已經巡完，結論還沒套用。</summary>
[Serializable]
public sealed record SqlMemoryMaintenanceState
{
    public SqlMemoryMaintenanceState(long version, SqlMemoryMaintenanceRound round, string? cursor,
        bool requiresAnotherPass, SqlMemoryCapacityStatus capacityStatus)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        Round = round ?? throw new ArgumentNullException(nameof(round));
        Cursor = cursor;
        RequiresAnotherPass = requiresAnotherPass;
        CapacityStatus = capacityStatus;
    }

    /// <summary>每次寫回加一；下一批以它做樂觀鎖，晚到的批次不會蓋掉別人推進過的游標。</summary>
    public long Version { get; }

    public SqlMemoryMaintenanceRound Round { get; }
    public string? Cursor { get; }
    public bool RequiresAnotherPass { get; }
    public SqlMemoryCapacityStatus CapacityStatus { get; }
}

/// <summary>
/// 把一批綁到共用的維護輪次。儲存層在同一個交易確認維護租約仍屬 <c>Owner</c>、狀態版本仍是
/// <c>ExpectedVersion</c>（沒有狀態列時為 0）才寫回游標；任一不符整批回復並以 Conflict 回報。
/// </summary>
[Serializable]
public sealed record SqlMemoryMaintenanceClaim
{
    public SqlMemoryMaintenanceClaim(SqlMemoryLeaseOwner owner, long expectedVersion, SqlMemoryMaintenanceRound round)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        ExpectedVersion = expectedVersion;
        Round = round ?? throw new ArgumentNullException(nameof(round));
    }

    public SqlMemoryLeaseOwner Owner { get; }
    public long ExpectedVersion { get; }
    public SqlMemoryMaintenanceRound Round { get; }
}

/// <summary>去重後 UTF-16 內容位元組不含索引／metadata；實體檔案是非原子的觀測值。</summary>
[Serializable]
public sealed record SqlMemoryUsage(long ContentBytes, long DatabaseFileBytes, long WalFileBytes);

public enum SqlMemoryCapacityStatus { WithinLimit, MoreWorkRequired, CannotReclaimWithinPolicy }

/// <summary>Truncated 為 false 表示仍有連線在讀舊快照，WAL 沒有歸零；重排下一輪即可，不是失敗。</summary>
[Serializable]
public sealed record SqlMemoryCheckpointResult(bool Truncated, SqlMemoryUsage Usage);

/// <summary>
/// Cursor 為 null 才完成一輪；有刪除時需再巡一輪處理新孤立父版本。ExaminedCandidates 含分組探測，
/// DeletedRows 含本批引用驅動回收的內容與連線。
/// </summary>
[Serializable]
public sealed record SqlMemoryMaintenanceResult(int ExaminedCandidates, int DeletedRows, string? Cursor,
    bool RequiresAnotherPass, SqlMemoryUsage Usage, SqlMemoryCapacityStatus CapacityStatus);
