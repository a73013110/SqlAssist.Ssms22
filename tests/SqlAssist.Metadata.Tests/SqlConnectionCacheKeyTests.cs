using Xunit;

namespace SqlAssist.Metadata.Tests;

public sealed class SqlConnectionCacheKeyTests
{
    private const string SqlAuthConnection =
        "Data Source=localhost;Initial Catalog=Sales;User ID=sa;Password=P@ssw0rd!";

    [Fact]
    public void 不含密碼()
    {
        var key = SqlConnectionCacheKey.Create(SqlAuthConnection, "Sales");

        Assert.DoesNotContain("p@ssw0rd", key, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", key, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 同一連線但密碼不同時視為同一個鍵()
    {
        var first = SqlConnectionCacheKey.Create(SqlAuthConnection, "Sales");
        var second = SqlConnectionCacheKey.Create(
            "Data Source=localhost;Initial Catalog=Sales;User ID=sa;Password=changed",
            "Sales");

        Assert.Equal(first, second);
    }

    [Fact]
    public void 鍵值順序與大小寫不影響結果()
    {
        var first = SqlConnectionCacheKey.Create(
            "Data Source=localhost;Initial Catalog=Sales;Integrated Security=True",
            "Sales");
        var second = SqlConnectionCacheKey.Create(
            "INTEGRATED SECURITY=true;INITIAL CATALOG=sales;DATA SOURCE=LOCALHOST",
            "sales");

        Assert.Equal(first, second);
    }

    [Fact]
    public void 不同伺服器產生不同的鍵()
    {
        var first = SqlConnectionCacheKey.Create("Data Source=serverA", "Sales");
        var second = SqlConnectionCacheKey.Create("Data Source=serverB", "Sales");

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// 這是先前以連線物件識別雜湊當鍵時的實際風險：同一條連線切換資料庫之後
    /// 仍然命中同一份快取，等於把別的資料庫的物件餵給目前的查詢視窗。
    /// </summary>
    [Fact]
    public void 同一連線但資料庫不同時產生不同的鍵()
    {
        var first = SqlConnectionCacheKey.Create("Data Source=localhost", "Sales");
        var second = SqlConnectionCacheKey.Create("Data Source=localhost", "Inventory");

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("這不是合法的連線字串 === ;;;")]
    public void 連線字串異常時仍可產生鍵(string? connectionString)
    {
        Assert.NotNull(SqlConnectionCacheKey.Create(connectionString, "Sales"));
    }
}
