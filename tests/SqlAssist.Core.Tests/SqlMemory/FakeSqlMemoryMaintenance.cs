using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Core.Tests.SqlMemory;

/// <summary>
/// 維護與租約兩個契約的記錄式假實作；帶 Claim 的批次照儲存層契約寫回狀態，
/// 多個 runner 共用同一個實例就等於共用同一個資料庫。真正的交易行為由 SQLite 整合測試涵蓋。
/// </summary>
internal sealed class FakeSqlMemoryMaintenance : ISqlMemoryMaintenanceStore, ISqlMemoryLeaseStore
{
    private readonly Queue<Func<SqlMemoryMaintenanceRequest, SqlMemoryMaintenanceResult>> _results = new();

    public List<SqlMemoryMaintenanceRequest> Requests { get; } = new();

    public List<string> Released { get; } = new();

    public List<SqlMemoryLease> Expired { get; } = new();

    public int Checkpoints { get; private set; }

    public int Opens { get; private set; }

    public int Renews { get; private set; }

    /// <summary>每次續約帶進來的識別碼；契約無狀態，心跳必須自己帶上持有的租約。</summary>
    public List<string> RenewedLeaseIds { get; } = new();

    /// <summary>每次讀過期租約時排除的識別碼。</summary>
    public List<string?> ExcludedLeaseIds { get; } = new();

    public bool MaintenanceLeaseAvailable { get; set; } = true;

    public int MaintenanceLeaseReleases { get; private set; }

    public bool RenewSucceeds { get; set; } = true;

    public SqlMemoryMaintenanceState? State { get; set; }

    public void Enqueue(SqlMemoryMaintenanceResult result) => _results.Enqueue(_ => result);

    /// <summary>下一批以指定例外失敗，什麼都不寫回；模擬整批回復。</summary>
    public void EnqueueFailure(SqlMemoryStorageErrorKind kind) =>
        _results.Enqueue(_ => throw new SqlMemoryStorageException(kind, "測試：" + kind));

    public Task<SqlMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Usages.Count > 0 ? Usages.Dequeue() : new SqlMemoryUsage(0, 0, 0));

    public Task<SqlMemoryMaintenanceResult> MaintainAsync(SqlMemoryMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var result = _results.Count > 0
            ? _results.Dequeue()(request)
            : new SqlMemoryMaintenanceResult(0, 0, null, false, new SqlMemoryUsage(0, 0, 0),
                SqlMemoryCapacityStatus.WithinLimit);
        if (request.Claim is { } claim)
        {
            if (claim.ExpectedVersion != (State?.Version ?? 0))
                throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Conflict, "測試：狀態版本不符");
            State = new SqlMemoryMaintenanceState(claim.ExpectedVersion + 1, claim.Round, result.Cursor,
                result.RequiresAnotherPass, result.CapacityStatus);
        }
        return Task.FromResult(result);
    }

    public Task<SqlMemoryMaintenanceState?> ReadMaintenanceStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(State);

    public Task<SqlMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken)
    {
        Checkpoints++;
        return Task.FromResult(new SqlMemoryCheckpointResult(true, new SqlMemoryUsage(0, 0, 0)));
    }

    public Task<SqlMemoryUsage> CompactAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SqlMemoryUsage(0, 0, 0));

    /// <summary>用量讀取依序回傳；空了就回零。清理前後各讀一次，測試用它驗證釋出容量。</summary>
    public Queue<SqlMemoryUsage> Usages { get; } = new();

    public List<(SqlMemoryCleanupRequest Request, string? Cursor, int Limit)> CleanupRequests { get; } = new();

    public Queue<SqlMemoryCleanupBatch> CleanupBatches { get; } = new();

    public Task<SqlMemoryUsageReport> ReadUsageReportAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SqlMemoryUsageReport(new SqlMemoryUsage(0, 0, 0), 0,
            new SqlMemoryUsageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Array.Empty<SqlMemoryUsageShare>(), null, null));

    public Task<SqlMemoryCleanupEstimate> EstimateCleanupAsync(SqlMemoryCleanupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SqlMemoryCleanupEstimate(0, 0, 0, 0));

    public Task<SqlMemoryCleanupBatch> CleanupHistoryAsync(SqlMemoryCleanupRequest request, string? cursor, int limit,
        CancellationToken cancellationToken)
    {
        CleanupRequests.Add((request, cursor, limit));
        return Task.FromResult(CleanupBatches.Count > 0 ? CleanupBatches.Dequeue() : new SqlMemoryCleanupBatch(0, 0, 0, null));
    }

    public Task<long> BackupAsync(string destinationPath, CancellationToken cancellationToken) => Task.FromResult(0L);

    public Task<string> OpenLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Opens++;
        return Task.FromResult("lease-" + Opens);
    }

    public Task<bool> RenewLeaseAsync(string leaseId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Renews++;
        RenewedLeaseIds.Add(leaseId);
        return Task.FromResult(RenewSucceeds);
    }

    public Task<IReadOnlyList<SqlMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit,
        string? excludedLeaseId, CancellationToken cancellationToken)
    {
        ExcludedLeaseIds.Add(excludedLeaseId);
        return Task.FromResult<IReadOnlyList<SqlMemoryLease>>(Expired.FindAll(lease => lease.LeaseId != excludedLeaseId));
    }

    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken)
    {
        Released.AddRange(leaseIds);
        return Task.FromResult(leaseIds.Count);
    }

    public Task<bool> TryAcquireMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now,
        DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Task.FromResult(MaintenanceLeaseAvailable);

    public Task<bool> ReleaseMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, CancellationToken cancellationToken)
    {
        MaintenanceLeaseReleases++;
        return Task.FromResult(true);
    }
}
