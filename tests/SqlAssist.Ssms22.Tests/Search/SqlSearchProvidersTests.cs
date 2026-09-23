using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Search;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

/// <summary>
/// 「範圍換了沒有」只由 provider 回答：連線事件每批 F5 都會來一次，答錯的代價是每次都重搜，
/// 或換了台卻留著上一台的結果。
/// </summary>
public sealed class SqlSearchProvidersTests
{
    private static readonly SqlSearchOrigin First = new("LIBSQL01");
    private static readonly SqlSearchOrigin Second = new("LIBSQL02");

    [Fact]
    public void 同一個目錄重設一次不算換範圍()
    {
        var providers = new SqlSearchProviders();

        Assert.True(providers.UseCatalog(SqlSearchTestCatalogs.Create("Library"), First));
        // 註冊表為同一個鍵換過一份目錄也不算：比的是快取鍵，不是參考。
        Assert.False(providers.UseCatalog(SqlSearchTestCatalogs.Create("Library"), new SqlSearchOrigin("LIBSQL01")));
    }

    [Fact]
    public void 換資料庫或換伺服器都算換範圍()
    {
        var providers = new SqlSearchProviders();
        providers.UseCatalog(SqlSearchTestCatalogs.Create("Library"), First);

        Assert.True(providers.UseCatalog(SqlSearchTestCatalogs.Create("LibReporting"), First));
        Assert.True(providers.UseCatalog(SqlSearchTestCatalogs.Create("LibReporting", server: "LIBSQL02"), Second));
    }

    [Fact]
    public void 斷線與接回都算換範圍_一直沒有連線不算()
    {
        var providers = new SqlSearchProviders();

        Assert.False(providers.UseCatalog(null, null));
        Assert.True(providers.UseCatalog(SqlSearchTestCatalogs.Create("Library"), First));
        Assert.True(providers.HasConnection);

        // 說不出是哪一台等於沒有連線，見 SqlSearchCatalogs.ResolveOrigin。
        Assert.True(providers.UseCatalog(SqlSearchTestCatalogs.Create("Library"), null));
        Assert.False(providers.HasConnection);
        Assert.False(providers.UseCatalog(null, First));
    }
}
