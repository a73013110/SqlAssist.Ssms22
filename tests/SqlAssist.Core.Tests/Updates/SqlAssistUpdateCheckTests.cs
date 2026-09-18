using SqlAssist.Core.Updates;
using Xunit;

namespace SqlAssist.Core.Tests.Updates;

/// <summary>
/// 發行 tag 與目前版本的比對。
/// </summary>
/// <remarks>
/// 三種結論各自要說出不同的話：猜錯的版本比對在使用者手上會變成「每次都說有新版」
/// 或「永遠沒有新版」，而兩者都不會有任何錯誤訊息。
/// </remarks>
public sealed class SqlAssistUpdateCheckTests
{
    [Theory]
    [InlineData("v0.16.3", "0.16.3")]
    [InlineData("0.16.3", "0.16.3")]
    [InlineData("V0.16", "0.16")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("nightly", "")]
    [InlineData("v0.16.3-beta", "")]
    public void 只認得版本形狀的tag(string? tag, string expected) =>
        Assert.Equal(expected, SqlAssistUpdateCheck.ParseTag(tag));

    /// <summary>逐字比對會把 0.15.10 讀成比 0.15.9 舊；版本要按段比。</summary>
    [Theory]
    [InlineData("0.15.9", "v0.15.10", SqlAssistUpdateStatus.UpdateAvailable)]
    [InlineData("0.15.10", "v0.15.9", SqlAssistUpdateStatus.UpToDate)]
    [InlineData("0.15.7", "v0.15.7", SqlAssistUpdateStatus.UpToDate)]
    [InlineData("0.15.7", "v0.16.0", SqlAssistUpdateStatus.UpdateAvailable)]
    [InlineData("未知", "v0.16.0", SqlAssistUpdateStatus.Unknown)]
    [InlineData("0.15.7", "nightly", SqlAssistUpdateStatus.Unknown)]
    public void 比對的是版本不是字串(string current, string tag, SqlAssistUpdateStatus expected) =>
        Assert.Equal(expected, SqlAssistUpdateCheck.Compare(current, tag).Status);

    [Fact]
    public void 有新版時說得出版本號與目前這一版()
    {
        var result = SqlAssistUpdateCheck.Compare("0.15.7", "v0.16.0");

        Assert.Equal(SqlAssistUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.16.0", result.LatestVersion);
        Assert.Contains("0.16.0", result.Message);
        Assert.Contains("0.15.7", result.Message);
        Assert.True(result.ShouldAnnounce);
    }

    /// <summary>已是最新與查不到都不值得打斷使用者；啟動時的自動檢查只看這一個判斷。</summary>
    [Theory]
    [InlineData("v0.15.7")]
    [InlineData("nightly")]
    public void 沒有新版就不主動出現(string tag) =>
        Assert.False(SqlAssistUpdateCheck.Compare("0.15.7", tag).ShouldAnnounce);

    [Fact]
    public void 從回應取出tag()
    {
        var json = "{\"tag_name\":\"v0.16.0\",\"draft\":false,\"html_url\":\"https://example.invalid\"}";
        var result = SqlAssistUpdateCheck.FromReleaseJson("0.15.7", json);

        Assert.Equal(SqlAssistUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.16.0", result.LatestVersion);
    }

    /// <summary>被限流、回了 HTML 錯誤頁或欄位改名，對使用者都是同一件事：這一次沒有答案。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>rate limited</html>")]
    [InlineData("{\"message\":\"Not Found\"}")]
    public void 讀不出版本一律是查不到(string? json)
    {
        var result = SqlAssistUpdateCheck.FromReleaseJson("0.15.7", json);

        Assert.Equal(SqlAssistUpdateStatus.Unknown, result.Status);
        Assert.Empty(result.LatestVersion);
        Assert.NotEmpty(result.Message);
    }

    /// <summary>端點只認最新的正式發行；草稿與 prerelease 不該進到比對這一步。</summary>
    [Fact]
    public void 端點與發行頁指向同一個儲存庫()
    {
        Assert.StartsWith("https://api.github.com/repos/a73013110/SqlAssist.Ssms22/releases/latest",
            SqlAssistUpdateCheck.LatestReleaseApiUrl);
        Assert.StartsWith("https://github.com/a73013110/SqlAssist.Ssms22/releases",
            SqlAssistUpdateCheck.LatestReleasePageUrl);
    }
}
