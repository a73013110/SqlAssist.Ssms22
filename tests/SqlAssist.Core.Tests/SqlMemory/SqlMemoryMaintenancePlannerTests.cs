using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryMaintenancePlannerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly SqlRetentionSettings Plan = new(TimeSpan.FromDays(30), TimeSpan.FromDays(180),
        TimeSpan.FromDays(7), 1024, 100, 100, 100);

    private static SqlRetentionPolicy Level(int days, int? quota, long? capacity = 1024) =>
        new(Start.AddDays(-days), Start.AddDays(-days), capacity, quota, quota, quota);

    private static SqlMemoryMaintenanceState State(int level, string? cursor, SqlMemoryCapacityStatus status,
        SqlMemoryMaintenanceScan scan = SqlMemoryMaintenanceScan.Indexed, bool anotherPass = false,
        int sinceFullScan = 0, bool reclaims = true, string? fingerprint = null) =>
        new(5, new SqlMemoryMaintenanceRound(fingerprint ?? Plan.Fingerprint, Start, level, reclaims, scan, sinceFullScan),
            cursor, anotherPass, status);

    [Fact]
    public void LadderRequiresADailyLevelAndRejectsMissingPolicies()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlRetentionLadder(null!));
        Assert.Throws<ArgumentException>(() => new SqlRetentionLadder());
        Assert.Throws<ArgumentNullException>(() => new SqlRetentionLadder(Level(30, 100), null!));
        var ladder = new SqlRetentionLadder(Level(30, 100));
        Assert.Equal(1, ladder.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => ladder[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ladder[-1]);
    }

    [Fact]
    public void LadderAcceptsTighteningIncludingDisabledCleanupBecomingBounded()
    {
        var ladder = new SqlRetentionLadder(
            new SqlRetentionPolicy(null, null, 1024),
            Level(30, 100), Level(7, 10), Level(7, 10));
        Assert.Equal(4, ladder.Count);
        Assert.Equal(Start.AddDays(-7), ladder[2].DraftBefore);
        Assert.Equal(10, ladder[3].MaxRevisionsPerFavorite);
    }

    [Theory]
    [InlineData(60, 100, 1024L)]
    [InlineData(7, 200, 1024L)]
    [InlineData(7, 100, 2048L)]
    public void LadderRejectsALevelThatLoosensRetentionOrChangesTheCapacityTrigger(int days, int? quota, long? capacity)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new SqlRetentionLadder(Level(30, 100), Level(days, quota, capacity)));
        Assert.Equal("levels", error.ParamName);
    }

    [Fact]
    public void LadderRejectsRetentionThatFallsBackToUnlimited()
    {
        Assert.Throws<ArgumentException>(() => new SqlRetentionLadder(Level(30, 100),
            new SqlRetentionPolicy(null, Start.AddDays(-30), 1024, 100, 100, 100)));
        Assert.Throws<ArgumentException>(() => new SqlRetentionLadder(Level(30, 100), Level(30, null)));
    }

    [Fact]
    public void WithoutSharedStateTheFirstRoundStartsNowAtDailyRetention()
    {
        var batch = new SqlMemoryMaintenancePlanner(Plan).Next(null, Start, reclaimRecovery: true);

        Assert.Null(batch.Cursor);
        Assert.Equal(0, batch.Round.Level);
        Assert.Equal(SqlMemoryMaintenanceScan.Indexed, batch.Round.Scan);
        Assert.Equal(Start, batch.Round.StartedAt);
        Assert.Equal(Plan.Fingerprint, batch.Round.PlanFingerprint);
        Assert.Equal(Start.AddDays(-30), batch.Policy.DraftBefore);
        Assert.Equal(Start.AddDays(-7), batch.Policy.RecoveryBefore);
        Assert.Throws<ArgumentNullException>(() => new SqlMemoryMaintenancePlanner(null!));
    }

    /// <summary>接續同一輪時以輪次起點換算政策，不管現在過了多久；否則游標指紋對不上。</summary>
    [Fact]
    public void AnOpenRoundKeepsItsStartCursorAndLevelNoMatterWhoResumesIt()
    {
        var planner = new SqlMemoryMaintenancePlanner(Plan);
        var batch = planner.Next(State(2, "c1", SqlMemoryCapacityStatus.MoreWorkRequired), Start.AddDays(3), true);

        Assert.Equal("c1", batch.Cursor);
        Assert.Equal(Start, batch.Round.StartedAt);
        Assert.Equal(2, batch.Round.Level);
        Assert.Equal(Start.AddDays(-7.5), batch.Policy.DraftBefore);
        Assert.True(planner.Observe(State(2, "c2", SqlMemoryCapacityStatus.MoreWorkRequired)).PendingWork);
    }

    /// <summary>時鐘被往回調之後，不能沿用換算在未來的截止時間把期限內的資料刪掉。</summary>
    [Fact]
    public void ARoundThatStartedInTheFutureIsRestartedAtTheSameLevel()
    {
        var batch = new SqlMemoryMaintenancePlanner(Plan).Next(
            State(1, "c1", SqlMemoryCapacityStatus.MoreWorkRequired), Start.AddDays(-2), true);

        Assert.Null(batch.Cursor);
        Assert.Equal(1, batch.Round.Level);
        Assert.Equal(Start.AddDays(-2), batch.Round.StartedAt);
        Assert.Equal(Start.AddDays(-17), batch.Policy.DraftBefore);
    }

    /// <summary>設定改過就不沿用舊輪次：舊的壓力級與游標都是依舊設定推出來的。</summary>
    [Fact]
    public void AChangedPlanStartsANewDailyRoundButKeepsTheFullScanCadence()
    {
        var batch = new SqlMemoryMaintenancePlanner(Plan).Next(
            State(2, "c1", SqlMemoryCapacityStatus.MoreWorkRequired, sinceFullScan: 9, fingerprint: "retention1|older"),
            Start.AddHours(1), true);

        Assert.Null(batch.Cursor);
        Assert.Equal(0, batch.Round.Level);
        Assert.Equal(Start.AddHours(1), batch.Round.StartedAt);
        Assert.Equal(9, batch.Round.RoundsSinceFullScan);
    }

    /// <summary>
    /// 心跳斷掉立刻放棄帶未存檔草稿期限的輪次；反過來心跳剛回來則等輪次邊界才加上期限。
    /// </summary>
    [Fact]
    public void OnlyLosingTheHeartbeatAbandonsAnOpenRound()
    {
        var planner = new SqlMemoryMaintenancePlanner(Plan);
        var lost = planner.Next(State(1, "c1", SqlMemoryCapacityStatus.MoreWorkRequired), Start.AddMinutes(5), false);
        Assert.Null(lost.Cursor);
        Assert.Equal(0, lost.Round.Level);
        Assert.Null(lost.Policy.RecoveryBefore);

        var regained = planner.Next(State(1, "c1", SqlMemoryCapacityStatus.MoreWorkRequired, reclaims: false),
            Start.AddMinutes(5), true);
        Assert.Equal("c1", regained.Cursor);
        Assert.Null(regained.Policy.RecoveryBefore);
    }

    [Fact]
    public void ReturningWithinLimitGoesBackToDailyRetentionFromAnyLevel()
    {
        var planner = new SqlMemoryMaintenancePlanner(Plan);
        var state = State(2, null, SqlMemoryCapacityStatus.WithinLimit);

        Assert.Equal(new SqlMemoryMaintenanceOutlook(0, false), planner.Observe(state));
        var batch = planner.Next(state, Start.AddHours(1), true);
        Assert.Equal(0, batch.Round.Level);
        Assert.Equal(Start.AddHours(1), batch.Round.StartedAt);
        Assert.Equal(1, batch.Round.RoundsSinceFullScan);
    }

    /// <summary>索引巡查一輪回收不到時先完整掃描同一級；完整掃描也回收不到才升級。</summary>
    [Fact]
    public void EscalationNeedsAFullScanThatAlsoCannotReclaim()
    {
        var planner = new SqlMemoryMaintenancePlanner(Plan);
        var indexed = State(0, null, SqlMemoryCapacityStatus.CannotReclaimWithinPolicy);
        Assert.Equal(new SqlMemoryMaintenanceOutlook(0, true), planner.Observe(indexed));
        var confirm = planner.Next(indexed, Start.AddMinutes(5), true);
        Assert.Equal(SqlMemoryMaintenanceScan.Full, confirm.Round.Scan);
        Assert.Equal(0, confirm.Round.Level);

        var full = State(0, null, SqlMemoryCapacityStatus.CannotReclaimWithinPolicy, SqlMemoryMaintenanceScan.Full);
        Assert.Equal(new SqlMemoryMaintenanceOutlook(1, true), planner.Observe(full));
        var tightened = planner.Next(full, Start.AddMinutes(10), true);
        Assert.Equal(1, tightened.Round.Level);
        Assert.Equal(SqlMemoryMaintenanceScan.Indexed, tightened.Round.Scan);
        Assert.Equal(0, tightened.Round.RoundsSinceFullScan);
        Assert.Equal(Start.AddMinutes(10).AddDays(-15), tightened.Policy.DraftBefore);
    }

    /// <summary>有進展就在同一級再巡一輪；最緊的一級也回收不到則不再排近下一批空轉。</summary>
    [Fact]
    public void ProgressRepeatsTheLevelAndTheTightestLevelDoesNotLoopPendingWork()
    {
        var planner = new SqlMemoryMaintenancePlanner(Plan);
        Assert.Equal(new SqlMemoryMaintenanceOutlook(1, true),
            planner.Observe(State(1, null, SqlMemoryCapacityStatus.MoreWorkRequired, anotherPass: true)));

        var tightest = State(2, null, SqlMemoryCapacityStatus.CannotReclaimWithinPolicy, SqlMemoryMaintenanceScan.Full);
        Assert.Equal(new SqlMemoryMaintenanceOutlook(2, false), planner.Observe(tightest));
        Assert.Equal(SqlMemoryMaintenanceScan.Indexed, planner.Next(tightest, Start.AddHours(1), true).Round.Scan);
    }

    [Fact]
    public void AFullScanIsScheduledAfterTheConfiguredNumberOfIndexedRounds()
    {
        var planner = new SqlMemoryMaintenancePlanner(Plan);
        var due = State(0, null, SqlMemoryCapacityStatus.WithinLimit,
            sinceFullScan: SqlMemoryMaintenancePlanner.FullScanEveryRounds - 1);

        var batch = planner.Next(due, Start.AddHours(1), true);

        Assert.Equal(SqlMemoryMaintenanceScan.Full, batch.Round.Scan);
        // 例行完整掃描不急，照固定間隔排；只有升級前的確認掃描才排近。
        Assert.False(planner.Observe(due).PendingWork);
    }

    [Fact]
    public void RestartKeepsTheLevelAndScanButDropsTheCursor()
    {
        var planner = new SqlMemoryMaintenancePlanner(Plan);
        var round = new SqlMemoryMaintenanceRound(Plan.Fingerprint, Start, 1, true, SqlMemoryMaintenanceScan.Full, 3);

        var batch = planner.Restart(round, Start.AddHours(2), true);

        Assert.Null(batch.Cursor);
        Assert.Equal(1, batch.Round.Level);
        Assert.Equal(SqlMemoryMaintenanceScan.Full, batch.Round.Scan);
        Assert.Equal(Start.AddHours(2), batch.Round.StartedAt);
        Assert.Throws<ArgumentNullException>(() => planner.Restart(null!, Start, true));
        Assert.Throws<ArgumentNullException>(() => planner.Observe(null!));
    }

    [Fact]
    public void WithoutACapacityLimitTheLadderNeverEscalates()
    {
        var planner = new SqlMemoryMaintenancePlanner(new SqlRetentionSettings(TimeSpan.FromDays(30),
            TimeSpan.FromDays(180), null, null, 100, 100, 100));
        SqlMemoryMaintenanceState? state = null;
        for (var round = 0; round < 3; round++)
        {
            var batch = planner.Next(state, Start.AddHours(round), true);
            state = new SqlMemoryMaintenanceState(round + 1, batch.Round, null, false, SqlMemoryCapacityStatus.WithinLimit);
        }
        Assert.Equal(0, state!.Round.Level);
    }
}
