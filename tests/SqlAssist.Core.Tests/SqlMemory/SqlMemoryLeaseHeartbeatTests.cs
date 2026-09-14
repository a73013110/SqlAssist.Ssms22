using System;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryLeaseHeartbeatTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly SqlMemoryLeaseOwner Owner = new("LIBRARYPC", 4242, Now.AddHours(-1));

    private static SqlMemoryLeaseHeartbeat Heartbeat(FakeSqlMemoryMaintenance store) =>
        new(store, Owner, TimeSpan.FromMinutes(1));

    [Fact]
    public async Task TheFirstBeatOpensALeaseAndLaterBeatsOnlyRenewIt()
    {
        var store = new FakeSqlMemoryMaintenance();
        var heartbeat = Heartbeat(store);

        Assert.Equal("lease-1", await heartbeat.BeatAsync(Now, CancellationToken.None));
        Assert.Equal("lease-1", await heartbeat.BeatAsync(Now.AddMinutes(1), CancellationToken.None));

        Assert.Equal(1, store.Opens);
        Assert.Equal(new[] { "lease-1" }, store.RenewedLeaseIds);
        Assert.Equal(0, heartbeat.ReopenCount);
    }

    /// <summary>續約回報 false 是租約已被回收的唯一訊號；呼叫端必須重新開啟才算持有。</summary>
    [Fact]
    public async Task ARenewThatFailsMeansTheLeaseRowIsGoneAndANewOneMustBeOpened()
    {
        var store = new FakeSqlMemoryMaintenance();
        var heartbeat = Heartbeat(store);
        await heartbeat.BeatAsync(Now, CancellationToken.None);

        store.RenewSucceeds = false;
        Assert.Equal("lease-2", await heartbeat.BeatAsync(Now.AddMinutes(1), CancellationToken.None));
        Assert.Equal(2, store.Opens);
        Assert.Equal(1, heartbeat.ReopenCount);
    }

    [Fact]
    public async Task TheHeartbeatIsDueImmediatelyAndThenOncePerInterval()
    {
        var store = new FakeSqlMemoryMaintenance();
        var heartbeat = Heartbeat(store);

        Assert.True(heartbeat.IsDue(Now));
        await heartbeat.BeatAsync(Now, CancellationToken.None);

        Assert.False(heartbeat.IsDue(Now.AddSeconds(59)));
        Assert.True(heartbeat.IsDue(Now.AddSeconds(60)));
    }

    [Fact]
    public void TheIntervalMustBePositiveAndTheStoreAndOwnerRequired()
    {
        var store = new FakeSqlMemoryMaintenance();

        Assert.Throws<ArgumentNullException>(() => new SqlMemoryLeaseHeartbeat(null!, Owner, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentNullException>(() => new SqlMemoryLeaseHeartbeat(store, null!, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlMemoryLeaseHeartbeat(store, Owner, TimeSpan.Zero));
    }
}
