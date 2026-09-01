using SqlAssist.Core.Diagnostics;
using Xunit;

namespace SqlAssist.Core.Tests.Diagnostics;

public sealed class SqlAssistBuildVersionTests
{
    [Fact]
    public void 顯示版號取自InformationalVersion而不是固定的AssemblyVersion()
    {
        var version = SqlAssistBuildVersion.Create(
            "0.14.58+bc1c42183c",
            "0.14.58.48156",
            "0.14.0.0");

        Assert.Equal("0.14.58", version.DisplayVersion);
        Assert.Equal("0.14.58+bc1c42183c", version.FullVersion);
        Assert.Equal("bc1c42183c", version.CommitId);
        Assert.Equal("bc1c421", version.ShortCommitId);
    }

    [Fact]
    public void 缺少InformationalVersion時檔案版號去掉來源修訂段()
    {
        var version = SqlAssistBuildVersion.Create(
            informationalVersion: null,
            fileVersion: "0.14.58.48156",
            assemblyVersion: "0.14.0.0");

        Assert.Equal("0.14.58", version.DisplayVersion);
        Assert.Equal("0.14.58.48156", version.FullVersion);
        Assert.Equal("未知", version.ShortCommitId);
    }

    [Fact]
    public void 三種版本都缺少時回傳未知()
    {
        var version = SqlAssistBuildVersion.Create(null, " ", null);

        Assert.Same(SqlAssistBuildVersion.Unknown, version);
    }
}
