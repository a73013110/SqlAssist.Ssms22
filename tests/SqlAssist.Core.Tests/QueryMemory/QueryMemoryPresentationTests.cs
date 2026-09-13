using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryPresentationTests
{
    [Theory]
    [InlineData(0, "剛剛")]
    [InlineData(59, "剛剛")]
    [InlineData(60, "1 分鐘前")]
    [InlineData(3599, "59 分鐘前")]
    [InlineData(3600, "1 小時前")]
    [InlineData(86400, "昨天")]
    [InlineData(259200, "3 天前")]
    public void RelativeTimeUsesStableBoundaries(int seconds, string expected)
    {
        var now = new DateTimeOffset(new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Local));
        var text = QueryMemoryPresentation.RelativeTime(now.AddSeconds(-seconds), now);
        Assert.StartsWith(expected, text);
        Assert.Contains(now.AddSeconds(-seconds).ToLocalTime().ToString("HH:mm"), text);
    }

    [Fact]
    public void OldTimestampRetainsYearAndFutureClockSkewDoesNotShowNegativeMinutes()
    {
        var now = new DateTimeOffset(new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Local));
        Assert.Equal("2025/09/13 14:00", QueryMemoryPresentation.RelativeTime(now.AddYears(-1), now));
        Assert.StartsWith("剛剛", QueryMemoryPresentation.RelativeTime(now.AddMinutes(2), now));
    }

    [Fact]
    public void FacetRequestRejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryConnectionFacetRequest(false, (FavoriteQueryScope)99, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false, sort: (QueryConnectionSort)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false, offset: -1));
    }
}
