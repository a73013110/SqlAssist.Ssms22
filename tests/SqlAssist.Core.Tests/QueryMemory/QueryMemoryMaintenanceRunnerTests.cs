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

    private static QueryMemoryMaintenanceRunner Runner(FakeQueryMemoryMaintenance repository,
        TimeSpan? startupDelay = null, params int[] liveProcesses)
    {
        var live = new HashSet<int>(liveProcesses);
        var reaper = new QueryMemoryLeaseReaper("LIBRARYPC", owner => live.Contains(owner.ProcessId));
        var schedule = new QueryMemoryMaintenanceSchedule(Start, startupDelay ?? TimeSpan.Zero, Interval,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        var plan = new QueryMemoryRetentionPlan(TimeSpan.FromDays(30), TimeSpan.FromDays(180),
            TimeSpan.FromDays(7), 1024, 100, 100, 100);
        return new QueryMemoryMaintenanceRunner(repository, repository, reaper, Owner, schedule, plan,
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
        var runner = Runner(repository, null, 4242);

        var tick = await Run(runner, Start);

        Assert.Equal(new[] { "dead" }, repository.Released);
        Assert.Equal(1, tick.ReleasedLeases);
    }

    /// <summary>游標綁政策，所以一輪之內的每一批都必須拿到同一份保留設定。</summary>
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
        Assert.Same(repository.Requests[0].Policy, repository.Requests[1].Policy);
        Assert.Equal("batch-2", repository.Requests[1].Cursor);
    }

    /// <summary>巡完一輪而且沒有壓力，下一輪才重算截止時間；否則設定值會停在啟用那一刻。</summary>
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

    /// <summary>完整巡過一輪仍然回收不到東西，才承認這一級的保留擋住了容量。</summary>
    [Fact]
    public async Task PressureEscalatesOnlyAfterAFullPassReclaimsNothing()
    {
        var repository = new FakeQueryMemoryMaintenance();
        repository.Enqueue(Result(0, "batch-2", QueryMemoryCapacityStatus.MoreWorkRequired));
        repository.Enqueue(Result(0, null, QueryMemoryCapacityStatus.CannotReclaimWithinPolicy));
        var runner = Runner(repository);

        await Run(runner, Start);
        Assert.Equal(0, runner.Level);

        await Run(runner, Start.AddMinutes(5));
        Assert.Equal(1, runner.Level);

        // 升級之後沿用收緊那一級的分級，不回頭重算日常保留。
        await Run(runner, Start.AddMinutes(10));
        Assert.Equal(Start.AddDays(-15), repository.Requests[2].Policy.DraftBefore);
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
