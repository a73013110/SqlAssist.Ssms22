using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class SavedQueryContractTests
{
    private static SavedQuery Query => new(Guid.NewGuid(), "讀者", null, Guid.NewGuid(), SavedQueryScope.Global, null, false);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void PagesAreBounded(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new SavedQueryRequest(size));

    [Fact]
    public void WriteRejectsInvalidIdentityNameDescriptionAndVersion()
    {
        Assert.Throws<ArgumentNullException>(() => new SavedQueryWrite(null!));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query with { SavedQueryId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query with { CurrentRevisionId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query with { Name = " " }));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query with { Name = new string('字', 201) }));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query with { Description = new string('字', 2001) }));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query, Guid.Empty));
        var valid = Query with { Name = new string('字', 200), Description = new string('字', 2000) };
        Assert.Equal(valid, new SavedQueryWrite(valid).Query);
    }

    [Theory]
    [InlineData(SavedQueryScope.Global, "LibraryServer", null)]
    [InlineData(SavedQueryScope.Global, null, "Library")]
    [InlineData(SavedQueryScope.Server, null, null)]
    [InlineData(SavedQueryScope.Server, " ", null)]
    [InlineData(SavedQueryScope.Server, "LibraryServer", "Library")]
    [InlineData(SavedQueryScope.Database, "LibraryServer", null)]
    [InlineData(SavedQueryScope.Database, "LibraryServer", " ")]
    public void ScopeCannotSilentlyBroaden(SavedQueryScope scope, string? server, string? database) =>
        Assert.Throws<ArgumentException>(() => new SavedQueryRequest(10, scope, server, database));

    [Fact]
    public void ScopeValidationIsSharedByRequestsAndWrites()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SavedQueryRequest(10, (SavedQueryScope)100));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query with { Connection = new QueryConnectionContext("LibraryServer", "Library") }));
        Assert.Throws<ArgumentException>(() => new SavedQueryWrite(Query with { Scope = SavedQueryScope.Database }));
        Assert.Null(new SavedQueryRequest(10, SavedQueryScope.Server, "LibraryServer", "").Database);
        var valid = Query with { Scope = SavedQueryScope.Database, Connection = new QueryConnectionContext("LibraryServer", "Library") };
        Assert.Equal(valid, new SavedQueryWrite(valid).Query);
    }
}
