using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryMaintenancePlannerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static QueryMemoryMaintenancePolicy Level(int days, int? quota, long? capacity = 1024) =>
        new(Start.AddDays(-days), Start.AddDays(-days), capacity, quota, quota, quota);

    private static QueryMemoryMaintenanceResult Result(string? cursor, QueryMemoryCapacityStatus status,
        bool requiresAnotherPass = false) =>
        new(1, 0, cursor, requiresAnotherPass, new QueryMemoryUsage(0, 0, 0), status);

    [Fact]
    public void LadderRequiresADailyLevelAndRejectsMissingPolicies()
    {
        Assert.Throws<ArgumentNullException>(() => new QueryMemoryRetentionLadder(null!));
        Assert.Throws<ArgumentException>(() => new QueryMemoryRetentionLadder());
        Assert.Throws<ArgumentNullException>(() => new QueryMemoryRetentionLadder(Level(30, 100), null!));
        var ladder = new QueryMemoryRetentionLadder(Level(30, 100));
        Assert.Equal(1, ladder.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => ladder[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ladder[-1]);
    }

    [Fact]
    public void LadderAcceptsTighteningIncludingDisabledCleanupBecomingBounded()
    {
        var ladder = new QueryMemoryRetentionLadder(
            new QueryMemoryMaintenancePolicy(null, null, 1024),
            Level(30, 100), Level(7, 10), Level(7, 10));
        Assert.Equal(4, ladder.Count);
        Assert.Equal(Start.AddDays(-7), ladder[2].DraftBefore);
        Assert.Equal(10, ladder[3].MaxRevisionsPerFavoriteQuery);
    }

    [Theory]
    [InlineData(60, 100, 1024L)]
    [InlineData(7, 200, 1024L)]
    [InlineData(7, 100, 2048L)]
    public void LadderRejectsALevelThatLoosensRetentionOrChangesTheCapacityTrigger(int days, int? quota, long? capacity)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new QueryMemoryRetentionLadder(Level(30, 100), Level(days, quota, capacity)));
        Assert.Equal("levels", error.ParamName);
    }

    [Fact]
    public void LadderRejectsRetentionThatFallsBackToUnlimited()
    {
        Assert.Throws<ArgumentException>(() => new QueryMemoryRetentionLadder(Level(30, 100),
            new QueryMemoryMaintenancePolicy(null, Start.AddDays(-30), 1024, 100, 100, 100)));
        Assert.Throws<ArgumentException>(() => new QueryMemoryRetentionLadder(Level(30, 100), Level(30, null)));
    }

    [Fact]
    public void UnfinishedRoundKeepsTheCursorAndTheLevel()
    {
        var planner = new QueryMemoryMaintenancePlanner(new QueryMemoryRetentionLadder(Level(30, 100), Level(7, 10)));
        Assert.Null(planner.NextRequest(50).Cursor);
        Assert.False(planner.PendingWork);
        planner.Observe(Result("c1", QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        Assert.Equal(0, planner.Level);
        Assert.True(planner.PendingWork);
        var request = planner.NextRequest(50);
        Assert.Equal("c1", request.Cursor);
        Assert.Same(planner.Policy, request.Policy);
        Assert.Equal(Start.AddDays(-30), request.Policy.DraftBefore);
    }

    [Fact]
    public void OnlyACompletedRoundThatCannotReclaimEscalatesAndTheCursorDoesNotCrossLevels()
    {
        var planner = new QueryMemoryMaintenancePlanner(new QueryMemoryRetentionLadder(Level(30, 100), Level(7, 10)));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.MoreWorkRequired, requiresAnotherPass: true));
        // 有進展就在同一級再巡一輪；升級只在這一級真的回收不到東西時發生。
        Assert.Equal(0, planner.Level);
        Assert.True(planner.PendingWork);
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        Assert.Equal(1, planner.Level);
        Assert.True(planner.PendingWork);
        Assert.Null(planner.NextRequest(50).Cursor);
        Assert.Equal(Start.AddDays(-7), planner.Policy.DraftBefore);
    }

    [Fact]
    public void EscalationStopsAtTheTightestLevelAndDoesNotLoopPendingWork()
    {
        var planner = new QueryMemoryMaintenancePlanner(new QueryMemoryRetentionLadder(Level(30, 100), Level(7, 10)));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        Assert.Equal(1, planner.Level);
        // 最緊的一級也回收不到就是政策擋住了容量，不該一直排下一批空轉。
        Assert.False(planner.PendingWork);
    }

    [Fact]
    public void ReturningWithinLimitGoesBackToDailyRetentionAndResetClearsPressure()
    {
        var planner = new QueryMemoryMaintenancePlanner(
            new QueryMemoryRetentionLadder(Level(30, 100), Level(14, 50), Level(7, 10)));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        Assert.Equal(2, planner.Level);
        planner.Observe(Result(null, QueryMemoryCapacityStatus.WithinLimit));
        Assert.Equal(0, planner.Level);
        Assert.False(planner.PendingWork);
        planner.Observe(Result("c1", QueryMemoryCapacityStatus.WithinLimit));
        planner.Reset();
        Assert.Null(planner.Cursor);
        Assert.Equal(0, planner.Level);
        Assert.False(planner.PendingWork);
        Assert.Throws<ArgumentNullException>(() => planner.Observe(null!));
    }

    [Fact]
    public void RebuildSwapsTheLadderButKeepsThePressureLevel()
    {
        var planner = new QueryMemoryMaintenancePlanner(
            new QueryMemoryRetentionLadder(Level(30, 100), Level(14, 50), Level(7, 10)));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        planner.Observe(Result("c1", QueryMemoryCapacityStatus.MoreWorkRequired));

        planner.Rebuild(new QueryMemoryRetentionLadder(Level(20, 100), Level(10, 50), Level(5, 10)));

        Assert.Equal(2, planner.Level);
        Assert.Equal(Start.AddDays(-5), planner.Policy.DraftBefore);
        // 新政策不能接舊游標。
        Assert.Null(planner.NextRequest(50).Cursor);
        Assert.Throws<ArgumentNullException>(() => planner.Rebuild(null!));
    }

    [Fact]
    public void RebuildWithFewerLevelsStaysAtTheTightestInsteadOfFallingBackToDaily()
    {
        var planner = new QueryMemoryMaintenancePlanner(
            new QueryMemoryRetentionLadder(Level(30, 100), Level(14, 50), Level(7, 10)));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        planner.Observe(Result(null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));

        planner.Rebuild(new QueryMemoryRetentionLadder(Level(30, 100), Level(10, 50)));

        Assert.Equal(1, planner.Level);
        Assert.Equal(Start.AddDays(-10), planner.Policy.DraftBefore);
    }

    [Fact]
    public void WithoutACapacityLimitTheLadderNeverEscalates()
    {
        var planner = new QueryMemoryMaintenancePlanner(new QueryMemoryRetentionLadder(
            Level(30, 100, null), Level(7, 10, null)));
        for (var round = 0; round < 3; round++) planner.Observe(Result(null, QueryMemoryCapacityStatus.WithinLimit));
        Assert.Equal(0, planner.Level);
        Assert.Throws<ArgumentNullException>(() => new QueryMemoryMaintenancePlanner(null!));
    }
}
