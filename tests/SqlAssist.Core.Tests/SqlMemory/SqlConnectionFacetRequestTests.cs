using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlConnectionFacetRequestTests
{
    [Fact]
    public void FacetRequestRejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlConnectionFacetRequest(false, false, sort: (SqlConnectionFacetSort)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlConnectionFacetRequest(false, false, offset: -1));
    }
}
