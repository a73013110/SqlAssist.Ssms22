using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlConnectionFacetRequestTests
{
    [Fact]
    public void FacetRequestRejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlConnectionFacetRequest(false, (SqlFavoriteScope)99, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, false, sort: (SqlConnectionFacetSort)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, false, offset: -1));
    }
}
