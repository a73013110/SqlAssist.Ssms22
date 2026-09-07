using System;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class BlockContextTextTests
{
    [Fact]
    public void BEGIN自成一行時保留前一行條件() =>
        Assert.Equal("↑ BEGIN（第 42 行） IF 1 = 1", BlockContextText.Format(BlockKind.Block, 42, "  BEGIN", " IF 1 = 1 "));

    [Fact]
    public void 同行內容與種類均可辨認() =>
        Assert.Equal("↑ CASE（第 3 行） SELECT CASE WHEN 1=1", BlockContextText.Format(BlockKind.Case, 3, "SELECT CASE WHEN 1=1", ""));

    [Fact]
    public void 長行有界且不拆UTF16代理對()
    {
        var summary = BlockContextText.Summarize(new string('x', 159) + "😀後續內容");
        Assert.Equal(new string('x', 159) + "…", summary);
        Assert.Equal("BEGIN TRY", BlockContextText.Summarize(" \tBEGIN\r\n TRY "));
        Assert.Equal(string.Empty, BlockContextText.Summarize("   "));
    }

    [Fact]
    public void 行號必須為一基底() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockContextText.Format(BlockKind.Block, 0, "BEGIN", ""));

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "")]
    [InlineData(1, " ")]
    [InlineData(8, "\t")]
    public void 有界行前綴的尾端不留下半個Unicode字元(int indentation, string trailing)
    {
        var prefix = new string(' ', indentation) + new string('x', 159) + "\ud83d" + trailing;
        Assert.Equal(new string('x', 159) + "…", BlockContextText.Summarize(prefix));
    }
}
