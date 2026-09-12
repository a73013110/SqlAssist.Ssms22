using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryMaintenanceContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void WorkBudgetIsBounded(int limit) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new QueryMemoryMaintenanceRequest(new QueryMemoryMaintenancePolicy(null, null, null), limit));

    [Fact]
    public void PolicyRejectsNegativeCapacityAndNormalizesTimeWithoutDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryMaintenancePolicy(null, null, -1));
        Assert.Throws<ArgumentNullException>(() => new QueryMemoryMaintenanceRequest(null!, 1));
        var time = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.FromHours(8));
        var policy = new QueryMemoryMaintenancePolicy(time, null, 0);
        Assert.Equal(TimeSpan.Zero, policy.DraftBefore?.Offset);
        Assert.Equal(time.ToUniversalTime(), policy.DraftBefore);
        Assert.Null(policy.ExecutionBefore);
        Assert.Equal(0, policy.MaxContentBytes);
        Assert.Null(policy.MaxExecutionEvents);
        Assert.Null(policy.MaxAutoRevisionsPerSession);
    }

    [Fact]
    public void CountQuotasAreOptionalAndNonNegativeWithoutImplyingACapacityLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryMaintenancePolicy(null, null, null, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryMaintenancePolicy(null, null, null, null, -1));
        var policy = new QueryMemoryMaintenancePolicy(null, null, null, 0, 50);
        Assert.Equal(0, policy.MaxExecutionEvents);
        Assert.Equal(50, policy.MaxAutoRevisionsPerSession);
        Assert.Null(policy.MaxContentBytes);
        Assert.Null(policy.ExecutionBefore);
    }
}
