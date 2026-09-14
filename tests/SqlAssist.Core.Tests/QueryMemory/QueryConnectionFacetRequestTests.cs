using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryConnectionFacetRequestTests
{
    [Fact]
    public void FacetRequestRejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryConnectionFacetRequest(false, (FavoriteQueryScope)99, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false, sort: (QueryConnectionSort)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false, offset: -1));
    }
}
