using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

public enum QueryMemoryMaintenanceOutcome
{
    /// <summary>還沒到排程時間，這一次什麼都沒做。</summary>
    NotDue,

    /// <summary>維護租約在別的程序手上；同一台機器多開 SSMS 時多數輪次都是這個。</summary>
    LeaseHeldElsewhere,

    Maintained,
}

/// <summary>一次有界維護的結果；<c>Result</c> 只有 <see cref="QueryMemoryMaintenanceOutcome.Maintained"/> 才有值。</summary>
public sealed record QueryMemoryMaintenanceTick(QueryMemoryMaintenanceOutcome Outcome, int Level,
    int ReleasedLeases, QueryMemoryMaintenanceResult? Result, bool Checkpointed);

/// <summary>
/// 把排程、保留分級、租約回收與一次有界 <c>MaintainAsync</c> 串起來。
/// </summary>
/// <remarks>
/// 一次只跑一批：<c>RequiresAnotherPass</c> 與未巡完的游標只把下一輪排近一點，
/// 不在同一次工作裡 drain——那會讓維護在使用者打字的時候占住整條 I/O。
///
/// 保留分級綁著建立它的那一刻。游標綁定政策，所以只有在「巡完一輪而且沒有壓力」時
/// 才重算截止時間；壓力期間沿用同一條分級，讓升級與游標對得起來。
/// </remarks>
public sealed class QueryMemoryMaintenanceRunner
{
    private readonly IQueryMemoryMaintenanceRepository _maintenance;
    private readonly IQueryMemoryLeaseRepository _leases;
    private readonly QueryMemoryLeaseReaper _reaper;
    private readonly QueryMemoryLeaseOwner _owner;
    private readonly QueryMemoryMaintenanceSchedule _schedule;
    private readonly QueryMemoryRetentionPlan _plan;
    private readonly TimeSpan _leaseExpiry;
    private readonly int _candidateLimit;
    private readonly int _leaseBatchLimit;

    private QueryMemoryMaintenancePlanner _planner;
    private bool _ladderReclaimsUnsavedDrafts;
    private int _deletedThisRound;

    public QueryMemoryMaintenanceRunner(IQueryMemoryMaintenanceRepository maintenance,
        IQueryMemoryLeaseRepository leases, QueryMemoryLeaseReaper reaper, QueryMemoryLeaseOwner owner,
        QueryMemoryMaintenanceSchedule schedule, QueryMemoryRetentionPlan plan, TimeSpan leaseExpiry,
        int candidateLimit = 200, int leaseBatchLimit = 20)
    {
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _reaper = reaper ?? throw new ArgumentNullException(nameof(reaper));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (leaseExpiry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseExpiry));
        if (candidateLimit < 1 || candidateLimit > 500) throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        if (leaseBatchLimit < 1) throw new ArgumentOutOfRangeException(nameof(leaseBatchLimit));
        _leaseExpiry = leaseExpiry;
        _candidateLimit = candidateLimit;
        _leaseBatchLimit = leaseBatchLimit;
        // 還沒確認心跳之前一律不憑年齡回收未存檔草稿；第一次 RunOnceAsync 會重算。
        _planner = new QueryMemoryMaintenancePlanner(plan.BuildLadder(DateTimeOffset.UtcNow, false));
        _ladderReclaimsUnsavedDrafts = false;
    }

    public DateTimeOffset NextDueAt => _schedule.NextDueAt;

    public int Level => _planner.Level;

    public bool PendingWork => _planner.PendingWork;

    /// <param name="sessionHeartbeatActive">
    /// 本程序此刻是否真的持有 Session 心跳租約。false 時整輪都不憑年齡回收未存檔草稿。
    /// </param>
    public async Task<QueryMemoryMaintenanceTick> RunOnceAsync(DateTimeOffset now, bool hostIdle,
        bool sessionHeartbeatActive, CancellationToken cancellationToken)
    {
        if (!_schedule.ShouldRun(now, hostIdle))
            return new QueryMemoryMaintenanceTick(QueryMemoryMaintenanceOutcome.NotDue, _planner.Level, 0, null, false);

        var expiredBefore = now - _leaseExpiry;
        if (!await _leases.TryAcquireMaintenanceLeaseAsync(_owner, now, expiredBefore, cancellationToken).ConfigureAwait(false))
        {
            // 別人正在維護就不必也不該重算；下一輪照常排。
            _schedule.Ran(now, _planner.PendingWork);
            return new QueryMemoryMaintenanceTick(QueryMemoryMaintenanceOutcome.LeaseHeldElsewhere, _planner.Level, 0, null, false);
        }

        var released = await ReleaseDeadLeasesAsync(expiredBefore, cancellationToken).ConfigureAwait(false);
        RefreshLadder(now, sessionHeartbeatActive);

        var result = await _maintenance
            .MaintainAsync(_planner.NextRequest(_candidateLimit), cancellationToken)
            .ConfigureAwait(false);
        _deletedThisRound += result.DeletedRows;
        _planner.Observe(result);

        // 巡完一輪而且真的刪掉東西，才值得把 WAL 截斷一次；沒有刪除時截斷只是多一次寫入。
        var checkpointed = false;
        if (result.Cursor == null)
        {
            if (_deletedThisRound > 0)
            {
                await _maintenance.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                checkpointed = true;
            }

            _deletedThisRound = 0;
        }

        _schedule.Ran(now, _planner.PendingWork);
        return new QueryMemoryMaintenanceTick(QueryMemoryMaintenanceOutcome.Maintained, _planner.Level, released, result, checkpointed);
    }

    /// <summary>設定改變時整個重來；舊游標綁在舊政策上，沿用會被儲存層拒絕。</summary>
    public void Reset(DateTimeOffset now, bool sessionHeartbeatActive)
    {
        _planner = new QueryMemoryMaintenancePlanner(_plan.BuildLadder(now, sessionHeartbeatActive));
        _ladderReclaimsUnsavedDrafts = sessionHeartbeatActive;
        _deletedThisRound = 0;
    }

    private async Task<int> ReleaseDeadLeasesAsync(DateTimeOffset expiredBefore, CancellationToken cancellationToken)
    {
        var expired = await _leases.ReadExpiredLeasesAsync(expiredBefore, _leaseBatchLimit, cancellationToken).ConfigureAwait(false);
        if (expired.Count == 0) return 0;
        var reclaimable = _reaper.Reclaimable(expired);
        if (reclaimable.Count == 0) return 0;
        return await _leases.ReleaseLeasesAsync(reclaimable, expiredBefore, cancellationToken).ConfigureAwait(false);
    }

    private void RefreshLadder(DateTimeOffset now, bool sessionHeartbeatActive)
    {
        // 心跳斷掉時立刻換掉整條分級，即使正巡到一半：多巡一輪的成本，
        // 遠小於讓沒有租約的線上 Session 的未存檔草稿被當成遺留資料。
        if (_ladderReclaimsUnsavedDrafts && !sessionHeartbeatActive)
        {
            Reset(now, false);
            return;
        }

        if (_planner.Cursor != null || _planner.Level != 0) return;
        Reset(now, sessionHeartbeatActive);
    }
}
