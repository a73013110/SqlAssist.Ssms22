using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class FavoriteQueryContractTests
{
    private static FavoriteQuery Query => new(Guid.NewGuid(), "讀者", null, Guid.NewGuid(), FavoriteQueryScope.Global, null);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void PagesAreBounded(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new FavoriteQueryRequest(size));

    [Fact]
    public void WriteRejectsInvalidIdentityNameDescriptionAndVersion()
    {
        Assert.Throws<ArgumentNullException>(() => new FavoriteQueryWrite(null!));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query with { FavoriteQueryId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query with { CurrentRevisionId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query with { Name = " " }));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query with { Name = new string('字', 201) }));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query with { Description = new string('字', 2001) }));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query, Guid.Empty));
        var valid = Query with { Name = new string('字', 200), Description = new string('字', 2000) };
        Assert.Equal(valid, new FavoriteQueryWrite(valid).Query);
    }

    [Theory]
    [InlineData(FavoriteQueryScope.Global, "LibraryServer", null)]
    [InlineData(FavoriteQueryScope.Global, null, "Library")]
    [InlineData(FavoriteQueryScope.Server, null, null)]
    [InlineData(FavoriteQueryScope.Server, " ", null)]
    [InlineData(FavoriteQueryScope.Server, "LibraryServer", "Library")]
    [InlineData(FavoriteQueryScope.Database, "LibraryServer", null)]
    [InlineData(FavoriteQueryScope.Database, "LibraryServer", " ")]
    public void ScopeCannotSilentlyBroaden(FavoriteQueryScope scope, string? server, string? database) =>
        Assert.Throws<ArgumentException>(() => new FavoriteQueryRequest(10, scope, server, database));

    [Fact]
    public void SearchIsOptionalAndDoesNotWidenScope()
    {
        Assert.Null(new FavoriteQueryRequest(10).Search);
        Assert.Null(new FavoriteQueryRequest(10, search: "").Search);
        // 空白是合法的字面搜尋；只有 null 與空字串代表不篩選。
        Assert.Equal(" ", new FavoriteQueryRequest(10, search: " ").Search);
        var request = new FavoriteQueryRequest(10, FavoriteQueryScope.Server, "LibraryServer", search: "Lib_Reader");
        Assert.Equal("Lib_Reader", request.Search);
        Assert.Equal(FavoriteQueryScope.Server, request.Scope);
        Assert.Throws<ArgumentException>(() => new FavoriteQueryRequest(10, FavoriteQueryScope.Global, "LibraryServer", search: "Lib_Reader"));
    }

    [Fact]
    public void EditRequiresIdentityExistingVersionAndSql()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new FavoriteQueryEdit(Guid.Empty, version, Guid.NewGuid(), "", DateTimeOffset.UtcNow));
        // 改 SQL 只能更新既有收藏；沒有讀到版本就不得寫入。
        Assert.Throws<ArgumentException>(() => new FavoriteQueryEdit(id, Guid.Empty, Guid.NewGuid(), "", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryEdit(id, version, Guid.Empty, "", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentNullException>(() => new FavoriteQueryEdit(id, version, Guid.NewGuid(), null!, DateTimeOffset.UtcNow));
        var edit = new FavoriteQueryEdit(id, version, Guid.NewGuid(), "", new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal("", edit.Sql);
        Assert.Equal(TimeSpan.Zero, edit.EditedAt.Offset);
    }

    [Fact]
    public void ScopeValidationIsSharedByRequestsAndWrites()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FavoriteQueryRequest(10, (FavoriteQueryScope)100));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query with { Connection = new QueryConnectionContext("LibraryServer", "Library") }));
        Assert.Throws<ArgumentException>(() => new FavoriteQueryWrite(Query with { Scope = FavoriteQueryScope.Database }));
        Assert.Null(new FavoriteQueryRequest(10, FavoriteQueryScope.Server, "LibraryServer", "").Database);
        var valid = Query with { Scope = FavoriteQueryScope.Database, Connection = new QueryConnectionContext("LibraryServer", "Library") };
        Assert.Equal(valid, new FavoriteQueryWrite(valid).Query);
    }
}
