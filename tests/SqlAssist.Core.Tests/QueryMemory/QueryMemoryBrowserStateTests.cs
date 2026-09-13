using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryBrowserStateTests
{
    [Fact]
    public void NewFilterRejectsLatePageAndPreservesNewRequest()
    {
        var state = new QueryMemoryBrowserState<string>();
        var first = state.Reset(); Assert.True(state.Begin(first));
        var second = state.Reset(); Assert.True(state.Begin(second));
        Assert.False(state.Accept(first, new[] { "舊 SQL" }, "舊游標"));
        state.Fail(first);
        Assert.True(state.Loading);
        Assert.True(state.Accept(second, new[] { "新 SQL" }, "新游標"));
        Assert.Equal(new[] { "新 SQL" }, state.Items);
        Assert.Equal("新游標", state.Cursor);
    }

    [Fact]
    public void DuplicateLoadIsRejectedAndNextPageAppends()
    {
        var state = new QueryMemoryBrowserState<int>();
        var generation = state.Reset(); Assert.True(state.Begin(generation));
        Assert.False(state.Begin(generation));
        Assert.True(state.Accept(generation, new[] { 1, 2 }, "opaque"));
        Assert.True(state.Begin(generation));
        Assert.True(state.Accept(generation, new[] { 3 }, null));
        Assert.Equal(new[] { 1, 2, 3 }, state.Items);
        Assert.Null(state.Cursor);
    }

    [Fact]
    public void ResetOnCloseInvalidatesPendingRequest()
    {
        var state = new QueryMemoryBrowserState<string>();
        var generation = state.Reset(); state.Begin(generation); state.Reset();
        Assert.False(state.Accept(generation, new[] { "SELECT 1" }, "cursor"));
        Assert.Empty(state.Items); Assert.Null(state.Cursor); Assert.False(state.Loading);
    }

    [Fact]
    public void FailureAllowsRetryWithoutLosingPreviousCursor()
    {
        var state = new QueryMemoryBrowserState<int>();
        var generation = state.Reset(); state.Begin(generation); state.Accept(generation, new[] { 1 }, "cursor");
        state.Begin(generation); state.Fail(generation);
        Assert.Equal("cursor", state.Cursor); Assert.Single(state.Items); Assert.True(state.Begin(generation));
    }
}
