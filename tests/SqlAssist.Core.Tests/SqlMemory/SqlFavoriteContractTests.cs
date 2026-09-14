using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlFavoriteContractTests
{
    private static SqlFavorite Favorite => new(Guid.NewGuid(), "讀者", null, Guid.NewGuid(), SqlFavoriteScope.Global, null);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void PagesAreBounded(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new SqlFavoriteRequest(size));

    [Fact]
    public void WriteRejectsInvalidIdentityNameDescriptionAndVersion()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlFavoriteWrite(null!));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite with { FavoriteId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite with { CurrentRevisionId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite with { Name = " " }));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite with { Name = new string('字', 201) }));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite with { Description = new string('字', 2001) }));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite, Guid.Empty));
        var valid = Favorite with { Name = new string('字', 200), Description = new string('字', 2000) };
        Assert.Equal(valid, new SqlFavoriteWrite(valid).Favorite);
    }

    [Theory]
    [InlineData(SqlFavoriteScope.Global, "LibraryServer", null)]
    [InlineData(SqlFavoriteScope.Global, null, "Library")]
    [InlineData(SqlFavoriteScope.Server, null, null)]
    [InlineData(SqlFavoriteScope.Server, " ", null)]
    [InlineData(SqlFavoriteScope.Server, "LibraryServer", "Library")]
    [InlineData(SqlFavoriteScope.Database, "LibraryServer", null)]
    [InlineData(SqlFavoriteScope.Database, "LibraryServer", " ")]
    public void ScopeCannotSilentlyBroaden(SqlFavoriteScope scope, string? server, string? database) =>
        Assert.Throws<ArgumentException>(() => new SqlFavoriteRequest(10, scope, server, database));

    [Fact]
    public void SearchIsOptionalAndDoesNotWidenScope()
    {
        Assert.Null(new SqlFavoriteRequest(10).Search);
        Assert.Null(new SqlFavoriteRequest(10, search: "").Search);
        // 空白是合法的字面搜尋；只有 null 與空字串代表不篩選。
        Assert.Equal(" ", new SqlFavoriteRequest(10, search: " ").Search);
        var request = new SqlFavoriteRequest(10, SqlFavoriteScope.Server, "LibraryServer", search: "Lib_Reader");
        Assert.Equal("Lib_Reader", request.Search);
        Assert.Equal(SqlFavoriteScope.Server, request.Scope);
        Assert.Throws<ArgumentException>(() => new SqlFavoriteRequest(10, SqlFavoriteScope.Global, "LibraryServer", search: "Lib_Reader"));
    }

    [Fact]
    public void EditRequiresIdentityExistingVersionAndSql()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSqlEdit(Guid.Empty, version, Guid.NewGuid(), "", DateTimeOffset.UtcNow));
        // 改 SQL 只能更新既有收藏；沒有讀到版本就不得寫入。
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSqlEdit(id, Guid.Empty, Guid.NewGuid(), "", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSqlEdit(id, version, Guid.Empty, "", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentNullException>(() => new SqlFavoriteSqlEdit(id, version, Guid.NewGuid(), null!, DateTimeOffset.UtcNow));
        var edit = new SqlFavoriteSqlEdit(id, version, Guid.NewGuid(), "", new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal("", edit.Sql);
        Assert.Equal(TimeSpan.Zero, edit.EditedAt.Offset);
    }

    [Fact]
    public void ScopeValidationIsSharedByRequestsAndWrites()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlFavoriteRequest(10, (SqlFavoriteScope)100));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite with { Connection = new SqlConnectionLabel("LibraryServer", "Library") }));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteWrite(Favorite with { Scope = SqlFavoriteScope.Database }));
        Assert.Null(new SqlFavoriteRequest(10, SqlFavoriteScope.Server, "LibraryServer", "").Database);
        var valid = Favorite with { Scope = SqlFavoriteScope.Database, Connection = new SqlConnectionLabel("LibraryServer", "Library") };
        Assert.Equal(valid, new SqlFavoriteWrite(valid).Favorite);
    }
}
