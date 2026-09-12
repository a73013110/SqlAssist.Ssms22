using System;
using System.Collections.Generic;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryLeaseReaperTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static QueryMemoryLease Lease(string id, string machine, int process, DateTimeOffset? started = null) =>
        new(id, new QueryMemoryLeaseOwner(machine, process, started ?? Started), Started);

    private static QueryMemoryLeaseReaper Reaper(params (int Process, DateTimeOffset Started)[] alive)
    {
        var live = new Dictionary<int, DateTimeOffset>();
        foreach (var process in alive) live.Add(process.Process, process.Started);
        return new QueryMemoryLeaseReaper("LIBRARYPC",
            process => live.TryGetValue(process, out var started) ? started : null);
    }

    [Fact]
    public void AnotherMachineIsReclaimedOnExpiryAloneBecauseItsProcessesCannotBeChecked()
    {
        var reaper = Reaper((4242, Started));
        // 這台機器查不到別台的程序；那裡的心跳過期就是全部證據，不能因為查不到而永遠不回收。
        Assert.Equal(new[] { "other" }, reaper.Reclaimable(new[] { Lease("other", "BRANCHPC", 4242) }));
    }

    [Fact]
    public void TheSameTripleStillRunningIsNeverReclaimedEvenWhenTheHeartbeatExpired()
    {
        var reaper = Reaper((4242, Started));
        Assert.Empty(reaper.Reclaimable(new[] { Lease("mine", "LIBRARYPC", 4242) }));
        // 機器名稱不分大小寫；Windows 回報的大小寫不該讓還開著的 SSMS 被當成遺留資料。
        Assert.Empty(reaper.Reclaimable(new[] { Lease("mine", "librarypc", 4242) }));
    }

    [Fact]
    public void AGoneProcessOrAReusedPidIsReclaimed()
    {
        var reaper = Reaper((4242, Started));
        Assert.Equal(new[] { "gone" }, reaper.Reclaimable(new[] { Lease("gone", "LIBRARYPC", 5555) }));
        // PID 會被重用；啟動時間對不上就是另一個程序，原本那個已經不在了。
        Assert.Equal(new[] { "reused" },
            reaper.Reclaimable(new[] { Lease("reused", "LIBRARYPC", 4242, Started.AddMinutes(-30)) }));
    }

    [Fact]
    public void StartTimeIsComparedWithATolerancePickedForTwoSeparateObservations()
    {
        var reaper = Reaper((4242, Started.AddMilliseconds(900)));
        Assert.Empty(reaper.Reclaimable(new[] { Lease("mine", "LIBRARYPC", 4242) }));
        Assert.Equal(new[] { "mine" }, Reaper((4242, Started.AddSeconds(5)))
            .Reclaimable(new[] { Lease("mine", "LIBRARYPC", 4242) }));
    }

    [Fact]
    public void MixedListsKeepOrderAndInvalidInputIsRejected()
    {
        var reaper = Reaper((4242, Started));
        Assert.Equal(new[] { "a", "c" }, reaper.Reclaimable(new[]
        {
            Lease("a", "BRANCHPC", 1), Lease("b", "LIBRARYPC", 4242), Lease("c", "LIBRARYPC", 1),
        }));
        Assert.Empty(reaper.Reclaimable(Array.Empty<QueryMemoryLease>()));
        Assert.Throws<ArgumentNullException>(() => reaper.Reclaimable(null!));
        Assert.Throws<ArgumentException>(() => reaper.Reclaimable(new QueryMemoryLease[] { null! }));
        Assert.Throws<ArgumentException>(() => new QueryMemoryLeaseReaper(" ", _ => null));
        Assert.Throws<ArgumentNullException>(() => new QueryMemoryLeaseReaper("LIBRARYPC", null!));
    }
}
