using System;
using System.Collections.Generic;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.Core.Tests.SqlMemory.SqlMemoryTestData;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlHistoryContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void PageSizeIsAlwaysBounded(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new SqlHistoryRequest(size));

    [Fact]
    public void PagingKeepsFiltersAndOwnsItsItems()
    {
        var items = new List<int> { 1 };
        var page = new SqlMemoryPage<int>(items, "opaque");
        items.Add(2);
        Assert.Single(page.Items);
        Assert.Equal("opaque", page.NextCursor);
        Assert.False(page.IsSearchPartial);
        var partial = new SqlMemoryPage<int>(Array.Empty<int>(), "opaque", Start);
        Assert.True(partial.IsSearchPartial);
        Assert.Equal(Start, partial.SearchedThrough);
        Assert.Throws<ArgumentNullException>(() => new SqlMemoryPage<int>(items, null!, Start));
        var request = new SqlHistoryRequest(50, SqlHistoryFilter.Executions, "Loan", "LibraryServer", "Library", Start, Start.AddDays(1), "opaque");
        Assert.Equal("Library", request.Database);
        Assert.Equal("opaque", request.Cursor);
        Assert.Throws<ArgumentException>(() => new SqlHistoryRequest(50, since: Start.AddDays(1), until: Start));
    }

    [Fact]
    public void HistoryItemDefaultsToASingleEventWithoutFirstExecution()
    {
        var draft = new SqlHistoryItem(Guid.NewGuid(), Guid.NewGuid(), null, "id", Start, SqlHistoryFilter.Drafts, "Library.sql",
            "SELECT * FROM Loan;", null);
        Assert.Equal(1, draft.ExecutionCount);
        Assert.Null(draft.FirstExecutedAt);
        var merged = draft with { Kind = SqlHistoryFilter.Executions, ExecutionCount = 3, FirstExecutedAt = Start.AddMinutes(-5) };
        Assert.NotEqual(draft with { Kind = SqlHistoryFilter.Executions }, merged);
    }

    [Fact]
    public void CaptureRejectsInvalidIdentitySequenceAndSelection()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Capture(0));
        Assert.Throws<ArgumentException>(() => Capture(selection: "SELECT 1"));
        Assert.Throws<ArgumentException>(() => Capture(session: Session with { DocumentId = Guid.NewGuid() }));
        Assert.Throws<ArgumentException>(() => Capture(session: Session with { ClosedAt = Start }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Capture(kind: (SqlCaptureKind)100));
    }

    [Fact]
    public void PolicyRejectsNonpositiveSamplingInterval() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlCapturePolicy(true, true, TimeSpan.Zero, true, true));
}
