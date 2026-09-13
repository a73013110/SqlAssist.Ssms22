using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

/// <summary>維護與擷取分離；宿主逐批派送，不在編輯器熱路徑同步清理。</summary>
public interface IQueryMemoryMaintenanceRepository
{
    Task<QueryMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken);
    Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request, CancellationToken cancellationToken);

    /// <summary>實體整理與有界清理分開；可重複執行，被讀取者擋下時回報未截斷而不中斷他們。</summary>
    Task<QueryMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken);

    /// <summary>重建整個資料庫，時間隨資料量成長；只供設定頁的手動命令，不進排程。</summary>
    Task<QueryMemoryUsage> CompactAsync(CancellationToken cancellationToken);
}

/// <summary>UTC 半開截止時間；null 停用該類保留清理，容量上限不授權刪除期限內資料。</summary>
[Serializable]
public sealed class QueryMemoryMaintenancePolicy
{
    /// <summary>筆數配額保留最新 N 筆並與截止時間取聯集；受保護根仍不刪除。null 為不限。</summary>
    public QueryMemoryMaintenancePolicy(DateTimeOffset? draftBefore, DateTimeOffset? executionBefore, long? maxContentBytes,
        int? maxExecutionEvents = null, int? maxAutoRevisionsPerSession = null, int? maxRevisionsPerFavoriteQuery = null,
        DateTimeOffset? recoveryBefore = null)
    {
        if (maxContentBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxContentBytes));
        if (maxExecutionEvents < 0) throw new ArgumentOutOfRangeException(nameof(maxExecutionEvents));
        if (maxAutoRevisionsPerSession < 0) throw new ArgumentOutOfRangeException(nameof(maxAutoRevisionsPerSession));
        if (maxRevisionsPerFavoriteQuery < 0) throw new ArgumentOutOfRangeException(nameof(maxRevisionsPerFavoriteQuery));
        DraftBefore = draftBefore?.ToUniversalTime();
        ExecutionBefore = executionBefore?.ToUniversalTime();
        MaxContentBytes = maxContentBytes;
        MaxExecutionEvents = maxExecutionEvents;
        MaxAutoRevisionsPerSession = maxAutoRevisionsPerSession;
        MaxRevisionsPerFavoriteQuery = maxRevisionsPerFavoriteQuery;
        RecoveryBefore = recoveryBefore?.ToUniversalTime();
    }

    public DateTimeOffset? DraftBefore { get; }
    public DateTimeOffset? ExecutionBefore { get; }
    public long? MaxContentBytes { get; }
    public int? MaxExecutionEvents { get; }
    public int? MaxAutoRevisionsPerSession { get; }

    /// <summary>每個收藏保留最新 N 個 SQL 編輯版本，含目前版本；只有此配額能回收它們。</summary>
    public int? MaxRevisionsPerFavoriteQuery { get; }

    /// <summary>
    /// 未存檔草稿的截止時間；只有持有有效心跳租約的宿主才可授權回收。
    /// null 表示完全不憑年齡回收 Recovery。宿主沒有在續 Session 心跳就不該給值，
    /// 否則沒有租約的線上 Session 會被當成遺留資料，使用者還開著的未存檔內容就消失了。
    /// </summary>
    public DateTimeOffset? RecoveryBefore { get; }
}

[Serializable]
public sealed class QueryMemoryMaintenanceRequest
{
    public QueryMemoryMaintenanceRequest(QueryMemoryMaintenancePolicy policy, int candidateLimit, string? cursor = null)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (candidateLimit < 1 || candidateLimit > 500) throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        CandidateLimit = candidateLimit;
        Cursor = cursor;
    }

    public QueryMemoryMaintenancePolicy Policy { get; }
    public int CandidateLimit { get; }
    public string? Cursor { get; }
}

/// <summary>去重後 UTF-16 內容位元組不含索引／metadata；實體檔案是非原子的觀測值。</summary>
[Serializable]
public sealed record QueryMemoryUsage(long ContentBytes, long DatabaseFileBytes, long WalFileBytes);

public enum QueryMemoryCapacityStatus { WithinLimit, MoreWorkRequired, CannotReclaimWithinPolicy }

/// <summary>Truncated 為 false 表示仍有連線在讀舊快照，WAL 沒有歸零；重排下一輪即可，不是失敗。</summary>
[Serializable]
public sealed record QueryMemoryCheckpointResult(bool Truncated, QueryMemoryUsage Usage);

/// <summary>Cursor 為 null 才完成一輪；有刪除時需再巡一輪處理新孤立父版本。</summary>
[Serializable]
public sealed record QueryMemoryMaintenanceResult(int ExaminedCandidates, int DeletedRows, string? Cursor,
    bool RequiresAnotherPass, QueryMemoryUsage Usage, QueryMemoryCapacityStatus CapacityStatus);
