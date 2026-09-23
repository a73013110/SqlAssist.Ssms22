using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Search;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

/// <summary>
/// 一筆結果點下去時，伺服器照那一筆自己的，而且找不到就說找不到。
/// </summary>
/// <remarks>
/// 跨伺服器的同名物件、同號 <c>object_id</c> 毫無關係，而跳錯台在畫面上看起來完全正常——
/// 這是 SQL Search 最貴的一種錯，所以「不准頂替」寫成測試而不是只寫在註解裡。
/// </remarks>
public sealed class SqlSearchCatalogsTests
{
    private static readonly SsmsObjectExplorerServer Primary = Server("LIBSQL01");
    private static readonly SsmsObjectExplorerServer Archive = Server("LIBSQL02");

    [Theory]
    [InlineData("LIBSQL01", "LIBSQL01", true)]
    [InlineData("LIBSQL01", "libsql01", true)]
    [InlineData("LIBSQL01", "LIBSQL02", false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    [InlineData("LIBSQL01", "", false)]
    [InlineData("", "LIBSQL01", false)]
    public void 伺服器名稱只有一種比法(string? serverName, string? otherServerName, bool expected)
    {
        Assert.Equal(expected, SqlSearchCatalogs.IsSameServer(serverName, otherServerName));

        // 樹上那一台的版本走同一條規則；兩處各寫一次的症狀是下拉說同一台、導航說不同台。
        Assert.Equal(expected, SqlSearchCatalogs.IsSameServer(Server(serverName ?? ""), otherServerName));
    }

    [Fact]
    public void 在樹上找的是這一筆那一台()
    {
        var found = SqlSearchCatalogs.FindOnTree(null, new[] { Primary, Archive }, new SqlSearchOrigin("libsql02"));

        Assert.Same(Archive, found);
    }

    /// <summary>
    /// 範圍換到別台之後，舊列仍然指向上一台；指名的那一台<b>不</b>代答。
    /// </summary>
    [Fact]
    public void 樹上沒有那一台時不拿指名的那一台頂替()
    {
        var found = SqlSearchCatalogs.FindOnTree(Archive, new[] { Archive }, new SqlSearchOrigin("LIBSQL01"));

        Assert.Null(found);
    }

    /// <summary>同一台在樹上有兩條連線時，用使用者挑的那一條。</summary>
    [Fact]
    public void 同一台有兩條連線時用指名的那一條()
    {
        var named = new SsmsObjectExplorerServer("LIBSQL01（唯讀）", "LIBSQL01", "Server[@Name='LIBSQL01']#2");

        var found = SqlSearchCatalogs.FindOnTree(named, new[] { Primary, named }, new SqlSearchOrigin("LIBSQL01"));

        Assert.Same(named, found);
    }

    [Fact]
    public void 問不到物件總管時仍然認得指名的那一台()
    {
        Assert.Same(Primary, SqlSearchCatalogs.FindOnTree(Primary, null, new SqlSearchOrigin("LIBSQL01")));
        Assert.Null(SqlSearchCatalogs.FindOnTree(Primary, null, new SqlSearchOrigin("LIBSQL02")));
    }

    /// <summary>拒絕的那一句說得出是哪一台，而且下一步是重新搜尋。</summary>
    [Fact]
    public void 範圍換過時說出那一台並請使用者重搜()
    {
        var notice = SqlSearchCatalogs.ElsewhereNotice(new SqlSearchOrigin("LIBSQL02"));

        Assert.Contains("LIBSQL02", notice);
        Assert.Contains("重新搜尋", notice);
    }

    private static SsmsObjectExplorerServer Server(string serverName) =>
        new(serverName, serverName, "Server[@Name='" + serverName + "']");
}
