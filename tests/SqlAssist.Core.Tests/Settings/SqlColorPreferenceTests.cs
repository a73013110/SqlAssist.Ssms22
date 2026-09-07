using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Settings;

public sealed class SqlColorPreferenceTests
{
    [Theory]
    [InlineData("#4F86c6", 0x4F86C6)]
    [InlineData("  #000000  ", 0)]
    [InlineData("#FFFFFF", 0xFFFFFF)]
    public void 只接受不透明RGB基準色(string value, int expected)
    {
        Assert.True(SqlColorPreference.TryParseRgb(value, out var rgb));
        Assert.Equal(expected, rgb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#804F86C6")]
    [InlineData("#GG0000")]
    [InlineData("# 12345")]
    [InlineData("#-12345")]
    public void 未設定或無效格式交由主題回退(string? value) =>
        Assert.False(SqlColorPreference.TryParseRgb(value, out _));
}
