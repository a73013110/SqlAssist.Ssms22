using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Core.Tests.QueryMemory;

/// <summary>
/// 維護與租約兩個契約的記錄式假實作；帶 Claim 的批次照儲存層契約寫回狀態，
/// 多個 runner 共用同一個實例就等於共用同一個資料庫。真正的交易行為由 SQLite 整合測試涵蓋。
/// </summary>
internal sealed class FakeQueryMemoryMaintenance : IQueryMemoryMaintenanceRepository, IQueryMemoryLeaseRepository
{
    private readonly Queue<Func<QueryMemoryMaintenanceRequest, QueryMemoryMaintenanceResult>> _results = new();

    public List<QueryMemoryMaintenanceRequest> Requests { get; } = new();

    public List<string> Released { get; } = new();

    public List<QueryMemoryLease> Expired { get; } = new();

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

    public QueryMemoryMaintenanceState? State { get; set; }

    public void Enqueue(QueryMemoryMaintenanceResult result) => _results.Enqueue(_ => result);

    /// <summary>下一批以指定例外失敗，什麼都不寫回；模擬整批回復。</summary>
    public void EnqueueFailure(QueryMemoryStorageErrorKind kind) =>
        _results.Enqueue(_ => throw new QueryMemoryStorageException(kind, "測試：" + kind));

    public Task<QueryMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new QueryMemoryUsage(0, 0, 0));

    public Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var result = _results.Count > 0
            ? _results.Dequeue()(request)
            : new QueryMemoryMaintenanceResult(0, 0, null, false, new QueryMemoryUsage(0, 0, 0),
                QueryMemoryCapacityStatus.WithinLimit);
        if (request.Claim is { } claim)
        {
            if (claim.ExpectedVersion != (State?.Version ?? 0))
                throw new QueryMemoryStorageException(QueryMemoryStorageErrorKind.Conflict, "測試：狀態版本不符");
            State = new QueryMemoryMaintenanceState(claim.ExpectedVersion + 1, claim.Round, result.Cursor,
                result.RequiresAnotherPass, result.CapacityStatus);
        }
        return Task.FromResult(result);
    }

    public Task<QueryMemoryMaintenanceState?> ReadMaintenanceStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(State);

    public Task<QueryMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken)
    {
        Checkpoints++;
        return Task.FromResult(new QueryMemoryCheckpointResult(true, new QueryMemoryUsage(0, 0, 0)));
    }

    public Task<QueryMemoryUsage> CompactAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new QueryMemoryUsage(0, 0, 0));

    public Task<string> OpenLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now,
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

    public Task<IReadOnlyList<QueryMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit,
        string? excludedLeaseId, CancellationToken cancellationToken)
    {
        ExcludedLeaseIds.Add(excludedLeaseId);
        return Task.FromResult<IReadOnlyList<QueryMemoryLease>>(Expired.FindAll(lease => lease.LeaseId != excludedLeaseId));
    }

    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken)
    {
        Released.AddRange(leaseIds);
        return Task.FromResult(leaseIds.Count);
    }

    public Task<bool> TryAcquireMaintenanceLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now,
        DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Task.FromResult(MaintenanceLeaseAvailable);

    public Task<bool> ReleaseMaintenanceLeaseAsync(QueryMemoryLeaseOwner owner, CancellationToken cancellationToken)
    {
        MaintenanceLeaseReleases++;
        return Task.FromResult(true);
    }
}
