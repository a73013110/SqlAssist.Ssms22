using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

public enum QueryMemoryMaintenanceOutcome
{
    /// <summary>還沒到排程時間，或維護已停止，這一次什麼都沒做。</summary>
    NotDue,

    /// <summary>
    /// 維護租約在別的程序手上，或這一批提交前租約易手、共用狀態被別人推進而整批回復；
    /// 同一台機器多開 SSMS 時多數輪次都是這個。
    /// </summary>
    LeaseHeldElsewhere,

    Maintained,
}

/// <summary>一次有界維護的結果；<c>Result</c> 與 <c>Scan</c> 只有 <see cref="QueryMemoryMaintenanceOutcome.Maintained"/> 才有值。</summary>
public sealed record QueryMemoryMaintenanceTick(QueryMemoryMaintenanceOutcome Outcome, int Level,
    int ReleasedLeases, QueryMemoryMaintenanceResult? Result, bool Checkpointed, QueryMemoryMaintenanceScan? Scan = null);

/// <summary>
/// 把排程、共用維護狀態、租約回收與一次有界 <c>MaintainAsync</c> 串起來。
/// </summary>
/// <remarks>
/// 一次只跑一批：<c>RequiresAnotherPass</c> 與未巡完的游標只把下一輪排近一點，
/// 不在同一次工作裡 drain——那會讓維護在使用者打字的時候占住整條 I/O。
///
/// 進度不放在這個物件裡：每一批先讀儲存層的狀態表，再以 <see cref="QueryMemoryMaintenanceClaim"/>
/// 在同一個交易寫回，重啟 SSMS 或換另一個程序拿到維護租約都從同一個游標接續。
/// </remarks>
public sealed class QueryMemoryMaintenanceRunner
{
    private readonly IQueryMemoryMaintenanceRepository _maintenance;
    private readonly IQueryMemoryLeaseRepository _leases;
    private readonly QueryMemoryLeaseReaper _reaper;
    private readonly QueryMemoryLeaseOwner _owner;
    private readonly QueryMemoryMaintenanceSchedule _schedule;
    private readonly TimeSpan _leaseExpiry;
    private readonly int _candidateLimit;
    private readonly int _leaseBatchLimit;

    // 批次與停止互斥：停止要等進行中的一批離開，否則那一批可能在交回租約之後又把它續回來。
    private readonly SemaphoreSlim _gate = new(1, 1);

    private QueryMemoryMaintenancePlanner _planner;
    private Reconfiguration? _reconfiguration;
    private int _level;
    private bool _pendingWork;
    private int _deletedThisRound;
    private bool _stopped;

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
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (leaseExpiry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseExpiry));
        if (candidateLimit < 1 || candidateLimit > 500) throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        if (leaseBatchLimit < 1) throw new ArgumentOutOfRangeException(nameof(leaseBatchLimit));
        _leaseExpiry = leaseExpiry;
        _candidateLimit = candidateLimit;
        _leaseBatchLimit = leaseBatchLimit;
        _planner = new QueryMemoryMaintenancePlanner(plan);
    }

    public DateTimeOffset NextDueAt => _schedule.NextDueAt;

    /// <summary>本程序最後一批之後，下一批會用的分級；其他程序推進的結果要到下一批讀狀態才看得到。</summary>
    public int Level => Volatile.Read(ref _level);

    public bool PendingWork => _pendingWork;

    /// <summary>
    /// 換上新的保留計畫與間隔，下一次 <see cref="RunOnceAsync"/> 開始時才生效；不重設啟動延遲。
    /// </summary>
    /// <remarks>
    /// 計畫指紋改變時，下一批讀到的共用輪次對不上而從日常級重開，游標跟著作廢；
    /// 指紋相同（只改了與保留無關的設定）時照常接續原本的游標。
    /// </remarks>
    public void Reconfigure(QueryMemoryRetentionPlan plan, TimeSpan interval, TimeSpan pendingDelay, TimeSpan idleMinimumGap)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        // 在呼叫端的執行緒就驗證，壞設定不會拖到背景那一批才失敗。
        QueryMemoryMaintenanceSchedule.Validate(interval, pendingDelay, idleMinimumGap);
        Volatile.Write(ref _reconfiguration, new Reconfiguration(plan, interval, pendingDelay, idleMinimumGap));
    }

    /// <param name="sessionHeartbeatActive">
    /// 本程序此刻是否真的持有 Session 心跳租約。false 時整輪都不憑年齡回收未存檔草稿。
    /// </param>
    public async Task<QueryMemoryMaintenanceTick> RunOnceAsync(DateTimeOffset now, bool hostIdle,
        bool sessionHeartbeatActive, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return Skipped(QueryMemoryMaintenanceOutcome.NotDue, 0);
            ApplyReconfiguration();
            if (!_schedule.ShouldRun(now, hostIdle)) return Skipped(QueryMemoryMaintenanceOutcome.NotDue, 0);

            var expiredBefore = now - _leaseExpiry;
            if (!await _leases.TryAcquireMaintenanceLeaseAsync(_owner, now, expiredBefore, cancellationToken).ConfigureAwait(false))
            {
                // 別人正在維護就不必也不該重算；下一輪照常排。
                _schedule.Ran(now, _pendingWork);
                return Skipped(QueryMemoryMaintenanceOutcome.LeaseHeldElsewhere, 0);
            }

            var released = await ReleaseDeadLeasesAsync(expiredBefore, cancellationToken).ConfigureAwait(false);
            var state = await _maintenance.ReadMaintenanceStateAsync(cancellationToken).ConfigureAwait(false);
            var version = state?.Version ?? 0;
            var batch = _planner.Next(state, now, sessionHeartbeatActive);

            QueryMemoryMaintenanceResult result;
            try
            {
                try
                {
                    result = await MaintainAsync(batch, version, cancellationToken).ConfigureAwait(false);
                }
                catch (QueryMemoryStorageException error) when (error.Kind == QueryMemoryStorageErrorKind.InvalidCursor && batch.Cursor != null)
                {
                    // 指紋相同卻對不上游標，代表換算方式在版本之間變了；不重開的話每一輪都會卡在同一個游標。
                    batch = _planner.Restart(batch.Round, now, sessionHeartbeatActive);
                    result = await MaintainAsync(batch, version, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (QueryMemoryStorageException error) when (error.Kind == QueryMemoryStorageErrorKind.Conflict)
            {
                // 這一批整個回復了；下一輪重讀狀態就能接上別人推進過的游標。
                _schedule.Ran(now, _pendingWork);
                return Skipped(QueryMemoryMaintenanceOutcome.LeaseHeldElsewhere, released);
            }

            var outlook = _planner.Observe(new QueryMemoryMaintenanceState(version + 1, batch.Round, result.Cursor,
                result.RequiresAnotherPass, result.CapacityStatus));
            Volatile.Write(ref _level, outlook.Level);
            _pendingWork = outlook.PendingWork;
            _deletedThisRound += result.DeletedRows;

            // 巡完一輪而且真的刪掉東西，才值得把 WAL 截斷一次；沒有刪除時截斷只是多一次寫入。
            // 計數只算本程序看到的批次，跨程序接續的輪次可能少截斷一次；截斷只是實體整理，不是保證。
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

            _schedule.Ran(now, _pendingWork);
            return new QueryMemoryMaintenanceTick(QueryMemoryMaintenanceOutcome.Maintained, outlook.Level, released, result,
                checkpointed, batch.Round.Scan);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 停止排程並交回維護租約；等進行中的一批離開才交回。之後的 <see cref="RunOnceAsync"/> 一律 NotDue。
    /// </summary>
    /// <returns>本程序確實持有並交回了維護租約。</returns>
    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopped = true;
            return await _leases.ReleaseMaintenanceLeaseAsync(_owner, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceBatch batch, long version,
        CancellationToken cancellationToken) => _maintenance.MaintainAsync(new QueryMemoryMaintenanceRequest(batch.Policy,
            _candidateLimit, batch.Cursor, batch.Round.Scan, new QueryMemoryMaintenanceClaim(_owner, version, batch.Round)),
        cancellationToken);

    private QueryMemoryMaintenanceTick Skipped(QueryMemoryMaintenanceOutcome outcome, int released) =>
        new(outcome, Volatile.Read(ref _level), released, null, false);

    private void ApplyReconfiguration()
    {
        var change = Interlocked.Exchange(ref _reconfiguration, null);
        if (change == null) return;
        var planChanged = change.Plan.Fingerprint != _planner.Plan.Fingerprint;
        _schedule.Reconfigure(change.Interval, change.PendingDelay, change.IdleMinimumGap, planChanged);
        _planner = new QueryMemoryMaintenancePlanner(change.Plan);
        if (!planChanged) return;
        Volatile.Write(ref _level, 0);
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

    private sealed record Reconfiguration(QueryMemoryRetentionPlan Plan, TimeSpan Interval, TimeSpan PendingDelay,
        TimeSpan IdleMinimumGap);
}
