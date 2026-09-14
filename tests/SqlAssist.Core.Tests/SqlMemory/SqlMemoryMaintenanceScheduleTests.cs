using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryMaintenanceScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static SqlMemoryMaintenanceSchedule Schedule() => new(Start, TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

    [Fact]
    public void NothingRunsDuringTheStartupDelayNotEvenWhenTheHostIsAlreadyIdle()
    {
        var schedule = Schedule();
        Assert.Equal(Start.AddMinutes(2), schedule.NextDueAt);
        Assert.False(schedule.ShouldRun(Start, hostIdle: true));
        Assert.False(schedule.ShouldRun(Start.AddMinutes(1).AddSeconds(59), hostIdle: true));
        // 啟動延遲是留給 SSMS 自己開起來的，閒置也不能把維護提前到那段時間裡。
        Assert.True(schedule.ShouldRun(Start.AddMinutes(2), hostIdle: false));
    }

    [Fact]
    public void AFinishedRoundWaitsTheFullIntervalAndPendingWorkOnlyScheduleTheNextRoundSooner()
    {
        var schedule = Schedule();
        schedule.Ran(Start.AddMinutes(2), pendingWork: false);
        Assert.Equal(Start.AddMinutes(32), schedule.NextDueAt);
        Assert.False(schedule.ShouldRun(Start.AddMinutes(31), hostIdle: false));
        Assert.True(schedule.ShouldRun(Start.AddMinutes(32), hostIdle: false));
        // 還有工作只是把下一輪排近一點，不是在同一次工作裡把整個游標排空。
        schedule.Ran(Start.AddMinutes(32), pendingWork: true);
        Assert.Equal(Start.AddMinutes(33), schedule.NextDueAt);
        Assert.True(schedule.ShouldRun(Start.AddMinutes(33), hostIdle: false));
    }

    [Fact]
    public void IdleBringsTheNextRoundForwardButNeverBelowTheMinimumGap()
    {
        var schedule = Schedule();
        schedule.Ran(Start.AddMinutes(2), pendingWork: false);
        Assert.False(schedule.ShouldRun(Start.AddMinutes(6), hostIdle: true));
        Assert.True(schedule.ShouldRun(Start.AddMinutes(7), hostIdle: true));
        // 沒有最小間隔的話，連續閒置會讓維護變成忙碌迴圈。
        Assert.False(schedule.ShouldRun(Start.AddMinutes(7), hostIdle: false));
        schedule.Ran(Start.AddMinutes(7), pendingWork: true);
        Assert.False(schedule.ShouldRun(Start.AddMinutes(7).AddSeconds(30), hostIdle: true));
        Assert.True(schedule.ShouldRun(Start.AddMinutes(8), hostIdle: false));
    }

    [Fact]
    public void TheFirstRoundMayAlsoBeBroughtForwardOnlyByTheDueTimeNotByIdleBeforeIt()
    {
        var schedule = new SqlMemoryMaintenanceSchedule(Start, TimeSpan.Zero, TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        // 沒有啟動延遲時第一輪立刻到期；閒置判斷用不上，也不需要先跑過一次才算得出間隔。
        Assert.True(schedule.ShouldRun(Start, hostIdle: false));
        schedule.Ran(Start, pendingWork: false);
        Assert.False(schedule.ShouldRun(Start.AddMinutes(4), hostIdle: true));
        Assert.True(schedule.ShouldRun(Start.AddMinutes(5), hostIdle: true));
    }

    /// <summary>改設定不是重新啟動：啟動延遲仍以 SSMS 開起來那一刻計算，不會每改一次就再等一次。</summary>
    [Fact]
    public void ReconfiguringBeforeTheFirstRunKeepsTheOriginalStartupDelay()
    {
        var schedule = Schedule();
        schedule.Reconfigure(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3), pendingWork: true);

        Assert.Equal(Start.AddMinutes(2), schedule.NextDueAt);
        Assert.False(schedule.ShouldRun(Start.AddMinutes(1), hostIdle: true));
        Assert.True(schedule.ShouldRun(Start.AddMinutes(2), hostIdle: false));
        Assert.Equal(TimeSpan.FromMinutes(10), schedule.Interval);
    }

    [Fact]
    public void ReconfiguringAfterARunMeasuresTheNewCadenceFromThatRun()
    {
        var schedule = Schedule();
        schedule.Ran(Start.AddMinutes(2), pendingWork: false);

        // 間隔改短：從上一次執行起算，不必等舊的三十分鐘。
        schedule.Reconfigure(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), pendingWork: false);
        Assert.Equal(Start.AddMinutes(12), schedule.NextDueAt);

        // 保留計畫換了就是新的一輪，排在「還有工作」的距離。
        schedule.Reconfigure(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), pendingWork: true);
        Assert.Equal(Start.AddMinutes(3), schedule.NextDueAt);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            schedule.Reconfigure(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(5), false));
    }

    [Theory]
    [InlineData(-1, 30, 1, 5)]
    [InlineData(2, 0, 1, 5)]
    [InlineData(2, 30, 0, 5)]
    [InlineData(2, 30, 31, 5)]
    [InlineData(2, 30, 1, -1)]
    [InlineData(2, 30, 1, 31)]
    public void CadenceIsBoundedSoMaintenanceCannotBecomeABusyLoop(int startup, int interval, int pending, int idleGap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlMemoryMaintenanceSchedule(Start,
            TimeSpan.FromMinutes(startup), TimeSpan.FromMinutes(interval),
            TimeSpan.FromMinutes(pending), TimeSpan.FromMinutes(idleGap)));
    }
}
