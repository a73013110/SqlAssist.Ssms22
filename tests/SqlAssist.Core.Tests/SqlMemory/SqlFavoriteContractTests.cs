using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlFavoriteContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 8, 0, 0, TimeSpan.FromHours(8));

    private static SqlFavorite Favorite => new(Guid.NewGuid(), "讀者", null, Guid.NewGuid(), null, null);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void PagesAreBounded(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new SqlFavoriteRequest(size));

    [Fact]
    public void SaveRejectsInvalidIdentityNameDescriptionVersionAndSql()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlFavoriteSave(null!, null, Now));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite with { FavoriteId = Guid.Empty }, null, Now));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite with { CurrentRevisionId = Guid.Empty }, null, Now));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite with { Name = " " }, null, Now));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite with { Name = new string('字', 201) }, null, Now));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite with { Description = new string('字', 2001) }, null, Now));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite, Guid.Empty, Now));
        // 有帶 SQL 就是要建版本；空字串不是「沒改 SQL」，必須明確傳 null。
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite, null, Now, ""));
        Assert.Throws<ArgumentException>(() => new SqlFavoriteSave(Favorite with { Server = new string('S', SqlFavoriteSave.MaxTagLength + 1) }, null, Now));
        var valid = Favorite with { Name = new string('字', 200), Description = new string('字', 2000) };
        var save = new SqlFavoriteSave(valid, null, Now, "SELECT 1;");
        Assert.Equal(valid, save.Favorite);
        Assert.Equal("SELECT 1;", save.Sql);
        Assert.Equal(TimeSpan.Zero, save.SavedAt.Offset);
    }

    [Fact]
    public void TagsAreTrimmedAndBlankMeansAny()
    {
        var save = new SqlFavoriteSave(Favorite with { Server = "  LibraryServer ", Database = " ", Description = "" }, null, Now);
        Assert.Equal("LibraryServer", save.Favorite.Server);
        Assert.Null(save.Favorite.Database);
        Assert.Null(save.Favorite.Description);

        // 篩選與標註同一份正規化：只標資料庫也是合法的，不需要先有伺服器。
        var request = new SqlFavoriteRequest(10, " ", " Library ");
        Assert.Null(request.Server);
        Assert.Equal("Library", request.Database);
    }

    [Fact]
    public void SearchIsOptionalAndIndependentOfTags()
    {
        Assert.Null(new SqlFavoriteRequest(10).Search);
        Assert.Null(new SqlFavoriteRequest(10, search: "").Search);
        // 空白是合法的字面搜尋；只有 null 與空字串代表不篩選。
        Assert.Equal(" ", new SqlFavoriteRequest(10, search: " ").Search);
        var request = new SqlFavoriteRequest(10, "LibraryServer", search: "Lib_Reader");
        Assert.Equal("Lib_Reader", request.Search);
        Assert.Equal("LibraryServer", request.Server);
        Assert.Null(request.Database);
    }
}
