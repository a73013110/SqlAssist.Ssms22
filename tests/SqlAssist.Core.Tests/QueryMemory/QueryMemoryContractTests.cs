using System;
using System.Collections.Generic;
using SqlAssist.Core.QueryMemory;
using Xunit;
using static SqlAssist.Core.Tests.QueryMemory.QueryMemoryTestData;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void PageSizeIsAlwaysBounded(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new QueryHistoryRequest(size));

    [Fact]
    public void PagingKeepsFiltersAndOwnsItsItems()
    {
        var items = new List<int> { 1 };
        var page = new QueryMemoryPage<int>(items, "opaque");
        items.Add(2);
        Assert.Single(page.Items);
        Assert.Equal("opaque", page.NextCursor);
        var request = new QueryHistoryRequest(50, QueryHistoryKind.Executed, "Loan", "LibraryServer", "Library", Start, Start.AddDays(1), "opaque");
        Assert.Equal("Library", request.Database);
        Assert.Equal("opaque", request.Cursor);
        Assert.Throws<ArgumentException>(() => new QueryHistoryRequest(50, since: Start.AddDays(1), until: Start));
    }

    [Fact]
    public void CaptureRejectsInvalidIdentitySequenceAndSelection()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Capture(0));
        Assert.Throws<ArgumentException>(() => Capture(selection: "SELECT 1"));
        Assert.Throws<ArgumentException>(() => Capture(session: Session with { DocumentId = Guid.NewGuid() }));
        Assert.Throws<ArgumentException>(() => Capture(session: Session with { ClosedAt = Start }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Capture(kind: (QueryCaptureKind)100));
    }

    [Fact]
    public void PolicyRejectsNonpositiveSamplingInterval() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryPolicy(true, true, TimeSpan.Zero, true, true));
}
