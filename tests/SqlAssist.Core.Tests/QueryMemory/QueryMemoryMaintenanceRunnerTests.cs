using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryMaintenanceRunnerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly QueryMemoryLeaseOwner Owner = new("LIBRARYPC", 4242, Start.AddHours(-1));

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(60);

    private static QueryMemoryMaintenanceResult Result(int deleted, string? cursor,
        QueryMemoryCapacityStatus status = QueryMemoryCapacityStatus.WithinLimit, bool anotherPass = false) =>
        new(1, deleted, cursor, anotherPass, new QueryMemoryUsage(0, 0, 0), status);

    private static QueryMemoryRetentionPlan Plan(int draftDays = 30) => new(TimeSpan.FromDays(draftDays),
        TimeSpan.FromDays(180), TimeSpan.FromDays(7), 1024, 100, 100, 100);

    private static QueryMemoryMaintenanceRunner Runner(FakeQueryMemoryMaintenance repository,
        TimeSpan? startupDelay = null, QueryMemoryLeaseOwner? owner = null, params int[] liveProcesses)
    {
        var live = new HashSet<int>(liveProcesses);
        var reaper = new QueryMemoryLeaseReaper("LIBRARYPC", candidate => live.Contains(candidate.ProcessId));
        var schedule = new QueryMemoryMaintenanceSchedule(Start, startupDelay ?? TimeSpan.Zero, Interval,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        return new QueryMemoryMaintenanceRunner(repository, repository, reaper, owner ?? Owner, schedule, Plan(),
            TimeSpan.FromMinutes(10));
    }

    private static Task<QueryMemoryMaintenanceTick> Run(QueryMemoryMaintenanceRunner runner, DateTimeOffset now,
        bool idle = false, bool heartbeat = true) =>
        runner.RunOnceAsync(now, idle, heartbeat, CancellationToken.None);

    [Fact]
    public async Task NothingRunsBeforeTheStartupDelayHasPassed()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var runner = Runner(repository, TimeSpan.FromMinutes(3));

        Assert.Equal(QueryMemoryMaintenanceOutcome.NotDue, (await Run(runner, Start.AddMinutes(2))).Outcome);
        Assert.Empty(repository.Requests);

        Assert.Equal(QueryMemoryMaintenanceOutcome.Maintained, (await Run(runner, Start.AddMinutes(3))).Outcome);
    }

    /// <summary>維護租約在別的程序手上就整輪讓開；重疊維護的正確性靠交易，不靠搶。</summary>
    [Fact]
    public async Task AnotherProcessHoldingTheMaintenanceLeaseSkipsTheWholeRound()
    {
        var repository = new FakeQueryMemoryMaintenance { MaintenanceLeaseAvailable = false };
        var runner = Runner(repository);

        var tick = await Run(runner, Start);

        Assert.Equal(QueryMemoryMaintenanceOutcome.LeaseHeldElsewhere, tick.Outcome);
        Assert.Empty(repository.Requests);
        // 下一輪照排，不是原地重試。
        Assert.Equal(QueryMemoryMaintenanceOutcome.NotDue, (await Run(runner, Start.AddMinutes(1))).Outcome);
    }

    /// <summary>還在執行的本機程序永遠不釋放，否則使用者正在編輯的未存檔草稿會消失。</summary>
    [Fact]
    public async Task OnlyLeasesTheProbeConfirmsAreGoneGetReleased()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Expired.Add(new QueryMemoryLease("alive",
            new QueryMemoryLeaseOwner("LIBRARYPC", 4242, Start), Start));
        repository.Expired.Add(new QueryMemoryLease("dead",
            new QueryMemoryLeaseOwner("LIBRARYPC", 4243, Start), Start));
        var runner = Runner(repository, null, null, 4242);

        var tick = await Run(runner, Start);

        Assert.Equal(new[] { "dead" }, repository.Released);
        Assert.Equal(1, tick.ReleasedLeases);
    }

    /// <summary>每一批都綁著維護租約的擁有者與讀到的狀態版本；儲存層靠這兩個拒絕晚到的批次。</summary>
    [Fact]
    public async Task EveryBatchClaimsTheSharedStateItReadWithItsOwnLease()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, "batch-2"));
        var runner = Runner(repository);

        await Run(runner, Start);
        await Run(runner, Start.AddMinutes(5));

        Assert.Equal(Owner, repository.Requests[0].Claim?.Owner);
        Assert.Equal(0, repository.Requests[0].Claim?.ExpectedVersion);
        Assert.Equal(1, repository.Requests[1].Claim?.ExpectedVersion);
        Assert.Equal(2, repository.State?.Version);
    }

    /// <summary>游標綁政策，所以一輪之內的每一批都必須由同一個輪次換算出同一份保留設定。</summary>
    [Fact]
    public async Task ThePolicyStaysIdenticalWhileACursorIsStillOpen()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, "batch-2"));
        repository.Enqueue(Result(0, null));
        var runner = Runner(repository);

        await Run(runner, Start);
        await Run(runner, Start.AddMinutes(5));

        Assert.Equal(2, repository.Requests.Count);
        Assert.Equal(repository.Requests[0].Claim?.Round, repository.Requests[1].Claim?.Round);
        Assert.Equal(repository.Requests[0].Policy.DraftBefore, repository.Requests[1].Policy.DraftBefore);
        Assert.Equal(repository.Requests[0].Policy.RecoveryBefore, repository.Requests[1].Policy.RecoveryBefore);
        Assert.Equal("batch-2", repository.Requests[1].Cursor);
    }

    /// <summary>重啟 SSMS 或換另一個程序拿到租約，都從狀態表的同一個游標與壓力級接續。</summary>
    [Fact]
    public async Task ANewProcessResumesTheSharedCursorRoundAndPressureLevel()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        repository.Enqueue(Result(2, "batch-2", QueryMemoryCapacityStatus.MoreWorkRequired));
        var first = Runner(repository);
        await Run(first, Start);
        await Run(first, Start.AddMinutes(5));
        await Run(first, Start.AddMinutes(10));
        Assert.Equal(1, first.Level);

        var restarted = Runner(repository, owner: Owner with { ProcessId = 5151 });
        await Run(restarted, Start.AddDays(1));

        var resumed = repository.Requests[3];
        Assert.Equal("batch-2", resumed.Cursor);
        Assert.Equal(repository.Requests[2].Claim?.Round, resumed.Claim?.Round);
        Assert.Equal(1, resumed.Claim?.Round.Level);
        // 輪次起點沿用第一個程序開輪次的時刻，政策因此與游標對得上，而不是以重啟那一刻重算。
        Assert.Equal(Start.AddMinutes(10).AddDays(-15), resumed.Policy.DraftBefore);
    }

    /// <summary>巡完一輪，下一輪就重算截止時間；否則設定值會停在啟用那一刻。</summary>
    [Fact]
    public async Task DeadlinesAreRecomputedOnceTheRoundIsFinished()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var runner = Runner(repository);

        await Run(runner, Start);
        await Run(runner, Start.AddHours(1));

        Assert.Equal(Start.AddDays(-30), repository.Requests[0].Policy.DraftBefore);
        Assert.Equal(Start.AddHours(1).AddDays(-30), repository.Requests[1].Policy.DraftBefore);
    }

    /// <summary>
    /// 索引巡查一輪回收不到，先完整掃描一輪；完整掃描也回收不到，才承認這一級的保留擋住了容量。
    /// </summary>
    [Fact]
    public async Task PressureEscalatesOnlyAfterAFullScanAlsoReclaimsNothing()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, "batch-2", QueryMemoryCapacityStatus.MoreWorkRequired));
        repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        var runner = Runner(repository);

        await Run(runner, Start);
        await Run(runner, Start.AddMinutes(5));
        Assert.Equal(0, runner.Level);
        Assert.True(runner.PendingWork);

        var confirmation = await Run(runner, Start.AddMinutes(10));
        Assert.Equal(QueryMemoryMaintenanceScan.Full, confirmation.Scan);
        Assert.Equal(QueryMemoryMaintenanceScan.Full, repository.Requests[2].Scan);
        Assert.Equal(1, runner.Level);

        // 升級之後以這一批的時間重算收緊那一級，回到索引巡查。
        var tightened = await Run(runner, Start.AddMinutes(15));
        Assert.Equal(QueryMemoryMaintenanceScan.Indexed, tightened.Scan);
        Assert.Equal(Start.AddMinutes(15).AddDays(-15), repository.Requests[3].Policy.DraftBefore);
    }

    /// <summary>
    /// 停在最緊的一級而且一直回收不到東西時，截止時間仍要跟著時間往前推。
    /// </summary>
    /// <remarks>
    /// 分級若凍結在升級那一刻，之後寫入的資料永遠不會比截止時間舊，按期限清理就完全停住，
    /// 只有重啟 SSMS 或改設定才會恢復。
    /// </remarks>
    [Fact]
    public async Task SustainedPressureAtTheTightestLevelStillAdvancesTheDeadlines()
    {
        var repository = new FakeQueryMemoryMaintenance();
        for (var round = 0; round < 6; round++)
            repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        var runner = Runner(repository);

        for (var batch = 0; batch < 4; batch++) await Run(runner, Start.AddMinutes(5 * batch));
        Assert.Equal(2, runner.Level);
        // 最緊的一級也回收不到就是政策擋住了容量，不該一直排下一批空轉。
        await Run(runner, Start.AddDays(3));
        Assert.False(runner.PendingWork);
        await Run(runner, Start.AddDays(6));

        Assert.Equal(2, runner.Level);
        Assert.Equal(Start.AddDays(3).AddDays(-7.5), repository.Requests[4].Policy.DraftBefore);
        Assert.Equal(Start.AddDays(6).AddDays(-7.5), repository.Requests[5].Policy.DraftBefore);
        Assert.Equal(Start.AddDays(6).AddDays(-45), repository.Requests[5].Policy.ExecutionBefore);
        Assert.Equal(Start.AddDays(6).AddDays(-1.75), repository.Requests[5].Policy.RecoveryBefore);
    }

    /// <summary>
    /// 收緊那一級每輪都還有進展時同樣留在原級，但每一輪都以當下重算；一輪之內仍沿用同一份政策。
    /// </summary>
    [Fact]
    public async Task SustainedProgressAtATightenedLevelAdvancesTheDeadlinesBetweenRounds()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        repository.Enqueue(Result(4, null, QueryMemoryCapacityStatus.MoreWorkRequired, anotherPass: true));
        repository.Enqueue(Result(4, "batch-2", QueryMemoryCapacityStatus.MoreWorkRequired));
        repository.Enqueue(Result(4, null, QueryMemoryCapacityStatus.MoreWorkRequired, anotherPass: true));
        repository.Enqueue(Result(4, null, QueryMemoryCapacityStatus.MoreWorkRequired, anotherPass: true));
        var runner = Runner(repository);

        await Run(runner, Start);
        await Run(runner, Start.AddMinutes(5));
        Assert.Equal(1, runner.Level);

        await Run(runner, Start.AddMinutes(10));
        await Run(runner, Start.AddDays(2));
        await Run(runner, Start.AddDays(2).AddMinutes(5));
        await Run(runner, Start.AddDays(4));

        Assert.Equal(1, runner.Level);
        Assert.Equal(Start.AddMinutes(10).AddDays(-15), repository.Requests[2].Policy.DraftBefore);
        Assert.Equal(Start.AddDays(2).AddDays(-15), repository.Requests[3].Policy.DraftBefore);
        // 游標還開著的那一批不重算，否則游標指紋對不上政策會被儲存層拒絕。
        Assert.Equal(repository.Requests[3].Policy.DraftBefore, repository.Requests[4].Policy.DraftBefore);
        Assert.Equal("batch-2", repository.Requests[4].Cursor);
        Assert.Equal(Start.AddDays(4).AddDays(-15), repository.Requests[5].Policy.DraftBefore);
        Assert.Null(repository.Requests[5].Cursor);
    }

    /// <summary>索引巡查碰不到的孤立資料靠低頻的完整掃描回收，不必等到容量超限。</summary>
    [Fact]
    public async Task AFullScanRunsAfterTheConfiguredNumberOfIndexedRounds()
    {
        var repository = new FakeQueryMemoryMaintenance
        {
            State = new QueryMemoryMaintenanceState(7, new QueryMemoryMaintenanceRound(Plan().Fingerprint, Start, 0, true,
                QueryMemoryMaintenanceScan.Indexed, QueryMemoryMaintenancePlanner.FullScanEveryRounds - 1), null, false,
                QueryMemoryCapacityStatus.WithinLimit),
        };
        var runner = Runner(repository);

        Assert.Equal(QueryMemoryMaintenanceScan.Full, (await Run(runner, Start.AddHours(1))).Scan);
        Assert.Equal(QueryMemoryMaintenanceScan.Indexed, (await Run(runner, Start.AddHours(2))).Scan);
        Assert.Equal(0, repository.Requests[1].Claim?.Round.RoundsSinceFullScan);
    }

    /// <summary>別的程序在讀狀態之後先推進了輪次：這一批整個回復，不蓋掉對方的游標。</summary>
    [Fact]
    public async Task AConflictingBatchYieldsWithoutAdvancingTheSharedState()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.EnqueueFailure(QueryMemoryStorageErrorKind.Conflict);
        var runner = Runner(repository);

        var tick = await Run(runner, Start);

        Assert.Equal(QueryMemoryMaintenanceOutcome.LeaseHeldElsewhere, tick.Outcome);
        Assert.Null(repository.State);
        Assert.Equal(QueryMemoryMaintenanceOutcome.NotDue, (await Run(runner, Start.AddMinutes(1))).Outcome);
    }

    /// <summary>狀態表裡的游標對不上重算的政策時，從當下重開同一級，而不是每一輪都卡在同一個錯誤。</summary>
    [Fact]
    public async Task AStalePersistedCursorRestartsTheRoundInsteadOfFailingForever()
    {
        var repository = new FakeQueryMemoryMaintenance
        {
            State = new QueryMemoryMaintenanceState(3, new QueryMemoryMaintenanceRound(Plan().Fingerprint, Start, 1, true,
                QueryMemoryMaintenanceScan.Indexed, 0), "stale", false, QueryMemoryCapacityStatus.MoreWorkRequired),
        };
        repository.EnqueueFailure(QueryMemoryStorageErrorKind.InvalidCursor);
        var runner = Runner(repository);

        var tick = await Run(runner, Start.AddHours(1));

        Assert.Equal(QueryMemoryMaintenanceOutcome.Maintained, tick.Outcome);
        Assert.Equal("stale", repository.Requests[0].Cursor);
        Assert.Null(repository.Requests[1].Cursor);
        Assert.Equal(1, repository.Requests[1].Claim?.Round.Level);
        Assert.Equal(Start.AddHours(1), repository.Requests[1].Claim?.Round.StartedAt);
        Assert.Equal(4, repository.State?.Version);
    }

    /// <summary>
    /// 改設定只換保留計畫與游標，不重算啟動延遲；計畫指紋沒變就連游標都照常接續。
    /// </summary>
    [Fact]
    public async Task ReconfiguringKeepsTheStartupDelayAndOnlyRestartsTheRoundWhenThePlanChanged()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, "batch-2"));
        repository.Enqueue(Result(0, "batch-3"));
        var runner = Runner(repository, TimeSpan.FromMinutes(3));

        runner.Reconfigure(Plan(), Interval, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        Assert.Equal(QueryMemoryMaintenanceOutcome.NotDue, (await Run(runner, Start.AddMinutes(2))).Outcome);
        await Run(runner, Start.AddMinutes(3));

        // 與保留無關的設定改動：同一份計畫，游標照常接續。
        runner.Reconfigure(Plan(), Interval, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        await Run(runner, Start.AddMinutes(8));
        Assert.Equal("batch-2", repository.Requests[1].Cursor);

        runner.Reconfigure(Plan(draftDays: 10), Interval, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        await Run(runner, Start.AddMinutes(13));
        Assert.Null(repository.Requests[2].Cursor);
        Assert.Equal(Start.AddMinutes(13).AddDays(-10), repository.Requests[2].Policy.DraftBefore);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            runner.Reconfigure(Plan(), Interval, TimeSpan.Zero, TimeSpan.FromMinutes(15)));
    }

    /// <summary>沒有刪除就不必截斷 WAL：那只是多一次寫入。</summary>
    [Fact]
    public async Task WalIsTruncatedOncePerRoundAndOnlyWhenSomethingWasDeleted()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, "batch-2"));
        repository.Enqueue(Result(3, null));
        repository.Enqueue(Result(0, null));
        var runner = Runner(repository);

        Assert.False((await Run(runner, Start)).Checkpointed);
        Assert.True((await Run(runner, Start.AddMinutes(5))).Checkpointed);
        Assert.False((await Run(runner, Start.AddHours(2))).Checkpointed);
        Assert.Equal(1, repository.Checkpoints);
    }

    /// <summary>
    /// 心跳一斷就立刻換掉整條分級，即使正巡到一半。
    /// </summary>
    /// <remarks>
    /// 多巡一輪的成本，遠小於讓沒有租約的線上 Session 的未存檔草稿被當成遺留資料回收。
    /// </remarks>
    [Fact]
    public async Task LosingTheHeartbeatDropsTheUnsavedDraftDeadlineImmediately()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, "batch-2"));
        var runner = Runner(repository);

        await Run(runner, Start);
        Assert.Equal(Start.AddDays(-7), repository.Requests[0].Policy.RecoveryBefore);

        await Run(runner, Start.AddMinutes(5), heartbeat: false);
        Assert.Null(repository.Requests[1].Policy.RecoveryBefore);
        // 換了政策就不能沿用舊游標，那會被儲存層拒絕。
        Assert.Null(repository.Requests[1].Cursor);
    }

    [Fact]
    public async Task IdleBringsTheNextRoundForwardButNotIntoABusyLoop()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var runner = Runner(repository);
        await Run(runner, Start);

        Assert.Equal(QueryMemoryMaintenanceOutcome.NotDue, (await Run(runner, Start.AddMinutes(14), idle: true)).Outcome);
        Assert.Equal(QueryMemoryMaintenanceOutcome.Maintained, (await Run(runner, Start.AddMinutes(15), idle: true)).Outcome);
    }

    /// <summary>正常卸載交回維護租約，之後即使計時器晚到也不再跑批次、不把租約續回來。</summary>
    [Fact]
    public async Task StoppingReleasesTheMaintenanceLeaseAndNoLaterBatchReacquiresIt()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var runner = Runner(repository);
        await Run(runner, Start);

        Assert.True(await runner.StopAsync(CancellationToken.None));
        Assert.Equal(QueryMemoryMaintenanceOutcome.NotDue, (await Run(runner, Start.AddDays(1))).Outcome);
        Assert.Equal(1, repository.MaintenanceLeaseReleases);
        Assert.Single(repository.Requests);
    }

    [Fact]
    public void TheRunnerRefusesArgumentsThatWouldMakeItUnbounded()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var reaper = new QueryMemoryLeaseReaper("LIBRARYPC", _ => true);
        var schedule = new QueryMemoryMaintenanceSchedule(Start, TimeSpan.Zero, Interval,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        var plan = new QueryMemoryRetentionPlan(TimeSpan.FromDays(30), TimeSpan.FromDays(180),
            null, null, null, null, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryMaintenanceRunner(repository, repository,
            reaper, Owner, schedule, plan, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryMaintenanceRunner(repository, repository,
            reaper, Owner, schedule, plan, TimeSpan.FromMinutes(10), candidateLimit: 501));
        Assert.Throws<ArgumentNullException>(() => new QueryMemoryMaintenanceRunner(null!, repository,
            reaper, Owner, schedule, plan, TimeSpan.FromMinutes(10)));
    }
}
