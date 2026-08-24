using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SqlSnippetExpanderTests
{
    private readonly SqlSnippetExpander _expander = new();

    [Theory]
    [InlineData("ssf", "SELECT * FROM ", 0)]
    [InlineData("SELECT ssf", "SELECT * FROM ", 7)]
    [InlineData("ap", "ALTER PROCEDURE ", 0)]
    [InlineData("af", "ALTER FUNCTION ", 0)]
    [InlineData("-- 註解\r\nssf", "SELECT * FROM ", 7)]
    public void 展開Snippet(string input, string expected, int replacementStart)
    {
        Assert.True(_expander.TryExpand(input, out var result));
        Assert.NotNull(result);
        Assert.Equal(expected, result!.ReplacementText);
        Assert.Equal(ExpansionKind.Snippet, result.Kind);
        Assert.Equal(replacementStart, result.ReplacementStart);
    }

    [Fact]
    public void 小寫關鍵字轉大寫()
    {
        Assert.True(_expander.TryExpand("select", out var result));
        Assert.NotNull(result);
        Assert.Equal("SELECT", result!.ReplacementText);
        Assert.Equal(ExpansionKind.Keyword, result.Kind);
    }

    [Theory]
    [InlineData("SELECT")]
    [InlineData("SELECT 'ssf")]
    [InlineData("-- ssf")]
    [InlineData("/* ssf")]
    [InlineData("\"ssf")]
    [InlineData("[ssf")]
    [InlineData("")]
    public void 不應展開(string input)
    {
        Assert.False(_expander.TryExpand(input, out _));
    }
}
