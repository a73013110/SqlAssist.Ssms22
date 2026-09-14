using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryMaintenanceContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void WorkBudgetIsBounded(int limit) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new SqlMemoryMaintenanceRequest(new SqlRetentionPolicy(null, null, null), limit));

    [Fact]
    public void PolicyRejectsNegativeCapacityAndNormalizesTimeWithoutDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlRetentionPolicy(null, null, -1));
        Assert.Throws<ArgumentNullException>(() => new SqlMemoryMaintenanceRequest(null!, 1));
        var time = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.FromHours(8));
        var policy = new SqlRetentionPolicy(time, null, 0);
        Assert.Equal(TimeSpan.Zero, policy.DraftBefore?.Offset);
        Assert.Equal(time.ToUniversalTime(), policy.DraftBefore);
        Assert.Null(policy.ExecutionBefore);
        Assert.Equal(0, policy.MaxContentBytes);
        Assert.Null(policy.MaxExecutionEvents);
        Assert.Null(policy.MaxAutoRevisionsPerSession);
        Assert.Null(policy.MaxRevisionsPerFavorite);
    }

    [Fact]
    public void CountQuotasAreOptionalAndNonNegativeWithoutImplyingACapacityLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlRetentionPolicy(null, null, null, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlRetentionPolicy(null, null, null, null, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlRetentionPolicy(null, null, null, null, null, -1));
        var policy = new SqlRetentionPolicy(null, null, null, 0, 50, 10);
        Assert.Equal(0, policy.MaxExecutionEvents);
        Assert.Equal(50, policy.MaxAutoRevisionsPerSession);
        Assert.Equal(10, policy.MaxRevisionsPerFavorite);
        Assert.Null(policy.MaxContentBytes);
        Assert.Null(policy.ExecutionBefore);
    }
}
