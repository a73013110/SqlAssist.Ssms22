using System;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryLeaseHeartbeatTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly QueryMemoryLeaseOwner Owner = new("LIBRARYPC", 4242, Now.AddHours(-1));

    private static QueryMemoryLeaseHeartbeat Heartbeat(FakeQueryMemoryMaintenance repository) =>
        new(repository, Owner, TimeSpan.FromMinutes(1));

    [Fact]
    public async Task TheFirstBeatOpensALeaseAndLaterBeatsOnlyRenewIt()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var heartbeat = Heartbeat(repository);

        Assert.Equal("lease-1", await heartbeat.BeatAsync(Now, CancellationToken.None));
        Assert.Equal("lease-1", await heartbeat.BeatAsync(Now.AddMinutes(1), CancellationToken.None));

        Assert.Equal(1, repository.Opens);
        Assert.Equal(new[] { "lease-1" }, repository.RenewedLeaseIds);
        Assert.Equal(0, heartbeat.ReopenCount);
    }

    /// <summary>續約回報 false 是租約已被回收的唯一訊號；呼叫端必須重新開啟才算持有。</summary>
    [Fact]
    public async Task ARenewThatFailsMeansTheLeaseRowIsGoneAndANewOneMustBeOpened()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var heartbeat = Heartbeat(repository);
        await heartbeat.BeatAsync(Now, CancellationToken.None);

        repository.RenewSucceeds = false;
        Assert.Equal("lease-2", await heartbeat.BeatAsync(Now.AddMinutes(1), CancellationToken.None));
        Assert.Equal(2, repository.Opens);
        Assert.Equal(1, heartbeat.ReopenCount);
    }

    [Fact]
    public async Task TheHeartbeatIsDueImmediatelyAndThenOncePerInterval()
    {
        var repository = new FakeQueryMemoryMaintenance();
        var heartbeat = Heartbeat(repository);

        Assert.True(heartbeat.IsDue(Now));
        await heartbeat.BeatAsync(Now, CancellationToken.None);

        Assert.False(heartbeat.IsDue(Now.AddSeconds(59)));
        Assert.True(heartbeat.IsDue(Now.AddSeconds(60)));
    }

    [Fact]
    public void TheIntervalMustBePositiveAndTheRepositoryAndOwnerRequired()
    {
        var repository = new FakeQueryMemoryMaintenance();

        Assert.Throws<ArgumentNullException>(() => new QueryMemoryLeaseHeartbeat(null!, Owner, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentNullException>(() => new QueryMemoryLeaseHeartbeat(repository, null!, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryLeaseHeartbeat(repository, Owner, TimeSpan.Zero));
    }
}
