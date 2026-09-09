using System.Linq;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetIndentationTests
{
    [Theory]
    [InlineData("SELECT 1;\n\nSELECT 2;", "\t", "SELECT 1;\n\n\tSELECT 2;")]
    [InlineData("SELECT 1;\r\nSELECT 2;\r\n", "  ", "SELECT 1;\r\n  SELECT 2;\r\n")]
    [InlineData("SELECT 1;\rSELECT 2;", "    ", "SELECT 1;\r    SELECT 2;")]
    [InlineData("SELECT 1;", "    ", "SELECT 1;")]
    public void 基準縮排只補後續非空行(string text, string indent, string expected)
    {
        var caret = text.Length;
        Assert.Equal(expected, SqlSnippetIndentation.Apply(text, indent, ref caret));
        Assert.Equal(expected.Length, caret);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(4, 6)]
    [InlineData(5, 9)]
    public void 游標只加上落點之前的縮排(int original, int expected)
    {
        var caret = original;
        SqlSnippetIndentation.Apply("a\nb\n\nc", "  ", ref caret);
        Assert.Equal(expected, caret);
    }

    [Fact]
    public void 換行轉換與縮排後的游標仍停在原標記()
    {
        var snippet = new SqlSnippet("be", "BEGIN\n\n$end$SELECT 1;\nEND");
        var text = snippet.Expansion.GetText("\r\n", "\t", out var caret);
        Assert.Equal("BEGIN\r\n\r\n\tSELECT 1;\r\n\tEND", text);
        Assert.Equal("SELECT 1;\r\n\tEND", text.Substring(caret));
    }

    [Fact]
    public void 原生逐點插入與完整文字模式使用相同行首()
    {
        const string text = "BEGIN\r\n\r\n    SELECT 1;\nEND";
        var native = text;
        foreach (var offset in SqlSnippetIndentation.ContinuationStarts(text).Reverse())
        {
            native = native.Insert(offset, "\t");
        }

        var caret = 0;
        Assert.Equal(native, SqlSnippetIndentation.Apply(text, "\t", ref caret));
    }
}
