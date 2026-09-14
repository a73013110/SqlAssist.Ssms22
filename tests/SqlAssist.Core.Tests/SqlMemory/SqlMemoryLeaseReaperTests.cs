using System;
using System.Collections.Generic;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryLeaseReaperTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static SqlMemoryLease Lease(string id, string machine, int process, DateTimeOffset? started = null) =>
        new(id, new SqlMemoryLeaseOwner(machine, process, started ?? Started), Started);

    private static SqlMemoryLeaseReaper Reaper(params (int Process, DateTimeOffset Started)[] alive)
    {
        var live = new Dictionary<int, DateTimeOffset>();
        foreach (var process in alive) live.Add(process.Process, process.Started);
        return new SqlMemoryLeaseReaper("LIBRARYPC", owner => live.TryGetValue(owner.ProcessId, out var started)
            && SqlMemoryLeaseReaper.IsSameProcess(owner, started));
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
        var owner = new SqlMemoryLeaseOwner("LIBRARYPC", 4242, Started);
        Assert.True(SqlMemoryLeaseReaper.IsSameProcess(owner, Started.AddMilliseconds(900)));
        Assert.True(SqlMemoryLeaseReaper.IsSameProcess(owner, Started.AddMilliseconds(-900)));
        Assert.False(SqlMemoryLeaseReaper.IsSameProcess(owner, Started.AddSeconds(5)));
        Assert.Throws<ArgumentNullException>(() => SqlMemoryLeaseReaper.IsSameProcess(null!, Started));
        var reaper = Reaper((4242, Started.AddMilliseconds(900)));
        Assert.Empty(reaper.Reclaimable(new[] { Lease("mine", "LIBRARYPC", 4242) }));
        Assert.Equal(new[] { "mine" }, Reaper((4242, Started.AddSeconds(5)))
            .Reclaimable(new[] { Lease("mine", "LIBRARYPC", 4242) }));
    }

    [Fact]
    public void AnOwnerThatCannotBeCheckedIsTreatedAsRunningSoUnsavedWorkIsNeverGuessedAway()
    {
        // 存取被拒之類的情況問不出答案；探測要回報 true，這裡就不該把它放進可回收清單。
        var reaper = new SqlMemoryLeaseReaper("LIBRARYPC", _ => true);
        Assert.Empty(reaper.Reclaimable(new[] { Lease("unknown", "LIBRARYPC", 4242) }));
        Assert.Equal(new[] { "elsewhere" }, reaper.Reclaimable(new[] { Lease("elsewhere", "BRANCHPC", 4242) }));
    }

    [Fact]
    public void MixedListsKeepOrderAndInvalidInputIsRejected()
    {
        var reaper = Reaper((4242, Started));
        Assert.Equal(new[] { "a", "c" }, reaper.Reclaimable(new[]
        {
            Lease("a", "BRANCHPC", 1), Lease("b", "LIBRARYPC", 4242), Lease("c", "LIBRARYPC", 1),
        }));
        Assert.Empty(reaper.Reclaimable(Array.Empty<SqlMemoryLease>()));
        Assert.Throws<ArgumentNullException>(() => reaper.Reclaimable(null!));
        Assert.Throws<ArgumentException>(() => reaper.Reclaimable(new SqlMemoryLease[] { null! }));
        Assert.Throws<ArgumentException>(() => new SqlMemoryLeaseReaper(" ", _ => false));
        Assert.Throws<ArgumentNullException>(() => new SqlMemoryLeaseReaper("LIBRARYPC", null!));
    }
}
