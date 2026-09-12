using System;

namespace SqlAssist.Core.QueryMemory;

/// <summary>
/// 決定下一批維護什麼時候跑：啟動後延遲首跑、之後固定間隔，宿主閒置可以提前但有最小間隔。
/// 一次只該跑一個有界 <c>MaintainAsync</c>；還有工作只是把下一輪排近一點，不在同一次工作裡排空。
/// 跑哪一級保留由 <see cref="QueryMemoryMaintenancePlanner"/> 決定，誰能跑由維護租約決定。
/// </summary>
public sealed class QueryMemoryMaintenanceSchedule
{
    private readonly DateTimeOffset _notBefore;
    private DateTimeOffset? _lastRunAt;

    public QueryMemoryMaintenanceSchedule(DateTimeOffset startedAt, TimeSpan startupDelay, TimeSpan interval,
        TimeSpan pendingDelay, TimeSpan idleMinimumGap)
    {
        if (startupDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startupDelay));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        // 還有工作時排得比固定間隔更近才有意義，但仍是「下一輪」而不是同一次工作內的迴圈。
        if (pendingDelay <= TimeSpan.Zero || pendingDelay > interval) throw new ArgumentOutOfRangeException(nameof(pendingDelay));
        if (idleMinimumGap < TimeSpan.Zero || idleMinimumGap > interval) throw new ArgumentOutOfRangeException(nameof(idleMinimumGap));
        StartupDelay = startupDelay;
        Interval = interval;
        PendingDelay = pendingDelay;
        IdleMinimumGap = idleMinimumGap;
        _notBefore = startedAt + startupDelay;
        NextDueAt = _notBefore;
    }

    public TimeSpan StartupDelay { get; }
    public TimeSpan Interval { get; }
    public TimeSpan PendingDelay { get; }

    /// <summary>閒置提前的最小間隔；沒有它，連續閒置會讓維護變成忙碌迴圈。</summary>
    public TimeSpan IdleMinimumGap { get; }

    public DateTimeOffset NextDueAt { get; private set; }

    /// <summary>宿主閒置只能把下一輪提前，不能提前到啟動延遲之內，那段時間要留給 SSMS 自己開起來。</summary>
    public bool ShouldRun(DateTimeOffset now, bool hostIdle)
    {
        if (now < _notBefore) return false;
        if (now >= NextDueAt) return true;
        return hostIdle && (!_lastRunAt.HasValue || now - _lastRunAt.Value >= IdleMinimumGap);
    }

    /// <summary>每跑完一批就回報一次；pendingWork 取自 <see cref="QueryMemoryMaintenancePlanner.PendingWork"/>。</summary>
    public void Ran(DateTimeOffset now, bool pendingWork)
    {
        _lastRunAt = now;
        NextDueAt = now + (pendingWork ? PendingDelay : Interval);
    }
}
