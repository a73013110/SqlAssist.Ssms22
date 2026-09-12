using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

/// <summary>維護與擷取分離；宿主逐批派送，不在編輯器熱路徑同步清理。</summary>
public interface IQueryMemoryMaintenanceRepository
{
    Task<QueryMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken);
    Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request, CancellationToken cancellationToken);
}

/// <summary>UTC 半開截止時間；null 停用該類保留清理，容量上限不授權刪除期限內資料。</summary>
[Serializable]
public sealed class QueryMemoryMaintenancePolicy
{
    public QueryMemoryMaintenancePolicy(DateTimeOffset? draftBefore, DateTimeOffset? executionBefore, long? maxContentBytes)
    {
        if (maxContentBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxContentBytes));
        DraftBefore = draftBefore?.ToUniversalTime();
        ExecutionBefore = executionBefore?.ToUniversalTime();
        MaxContentBytes = maxContentBytes;
    }

    public DateTimeOffset? DraftBefore { get; }
    public DateTimeOffset? ExecutionBefore { get; }
    public long? MaxContentBytes { get; }
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

/// <summary>Cursor 為 null 才完成一輪；有刪除時需再巡一輪處理新孤立父版本。</summary>
[Serializable]
public sealed record QueryMemoryMaintenanceResult(int ExaminedCandidates, int DeletedRows, string? Cursor,
    bool RequiresAnotherPass, QueryMemoryUsage Usage, QueryMemoryCapacityStatus CapacityStatus);
