using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class PagedLoadStateTests
{
    [Fact]
    public void NewFilterRejectsLatePageAndPreservesNewRequest()
    {
        var state = new PagedLoadState();
        var first = state.Reset(); Assert.True(state.Begin(first));
        var second = state.Reset(); Assert.True(state.Begin(second));
        Assert.False(state.Accept(first, "舊游標"));
        state.Fail(first);
        Assert.True(state.Loading);
        Assert.True(state.Accept(second, "新游標"));
        Assert.Equal("新游標", state.Cursor);
    }

    [Fact]
    public void DuplicateLoadIsRejectedAndNextPageAdvancesCursor()
    {
        var state = new PagedLoadState();
        var generation = state.Reset(); Assert.True(state.Begin(generation));
        Assert.False(state.Begin(generation));
        Assert.True(state.Accept(generation, "opaque"));
        Assert.True(state.Begin(generation));
        Assert.True(state.Accept(generation, null));
        Assert.Null(state.Cursor);
    }

    [Fact]
    public void ResetOnCloseInvalidatesPendingRequest()
    {
        var state = new PagedLoadState();
        var generation = state.Reset(); state.Begin(generation); state.Reset();
        Assert.False(state.Accept(generation, "cursor"));
        Assert.Null(state.Cursor); Assert.False(state.Loading);
    }

    [Fact]
    public void FailureAllowsRetryWithoutLosingPreviousCursor()
    {
        var state = new PagedLoadState();
        var generation = state.Reset(); state.Begin(generation); state.Accept(generation, "cursor");
        state.Begin(generation); state.Fail(generation);
        Assert.Equal("cursor", state.Cursor); Assert.True(state.Begin(generation));
    }
}
