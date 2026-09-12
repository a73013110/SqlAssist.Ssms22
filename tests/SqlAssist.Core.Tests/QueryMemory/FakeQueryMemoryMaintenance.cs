using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Core.Tests.QueryMemory;

/// <summary>維護與租約兩個契約的記錄式假實作；真正的交易行為由 SQLite 整合測試涵蓋。</summary>
internal sealed class FakeQueryMemoryMaintenance : IQueryMemoryMaintenanceRepository, IQueryMemoryLeaseRepository
{
    private readonly Queue<QueryMemoryMaintenanceResult> _results = new();

    public List<QueryMemoryMaintenanceRequest> Requests { get; } = new();

    public List<string> Released { get; } = new();

    public List<QueryMemoryLease> Expired { get; } = new();

    public int Checkpoints { get; private set; }

    public int Opens { get; private set; }

    public int Renews { get; private set; }

    public bool MaintenanceLeaseAvailable { get; set; } = true;

    public bool RenewSucceeds { get; set; } = true;

    public void Enqueue(QueryMemoryMaintenanceResult result) => _results.Enqueue(result);

    public Task<QueryMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new QueryMemoryUsage(0, 0, 0));

    public Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_results.Count > 0
            ? _results.Dequeue()
            : new QueryMemoryMaintenanceResult(0, 0, null, false, new QueryMemoryUsage(0, 0, 0),
                QueryMemoryCapacityStatus.WithinLimit));
    }

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

    public Task<bool> RenewLeaseAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Renews++;
        return Task.FromResult(RenewSucceeds);
    }

    public Task<IReadOnlyList<QueryMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QueryMemoryLease>>(Expired);

    public Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken)
    {
        Released.AddRange(leaseIds);
        return Task.FromResult(leaseIds.Count);
    }

    public Task<bool> TryAcquireMaintenanceLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now,
        DateTimeOffset expiredBefore, CancellationToken cancellationToken) =>
        Task.FromResult(MaintenanceLeaseAvailable);
}
