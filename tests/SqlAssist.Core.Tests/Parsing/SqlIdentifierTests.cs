using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlIdentifierTests
{
    [Theory]
    [InlineData("[Loan]", "Loan")]
    [InlineData("[a]]b]", "a]b")]
    [InlineData("\"a\"\"b\"", "a\"b")]
    [InlineData("Loan", "Loan")]
    public void 拿掉括住整個名稱的括號(string text, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.Unquote(text));
    }

    /// <summary>提交時一起換掉的右半邊：只到右方括號，而且中間只能是識別字字元。</summary>
    [Theory]
    [InlineData("]", 1)]
    [InlineData("_Reader] AS r", 8)]
    [InlineData("", 0)]
    [InlineData("Reader", 0)]
    [InlineData(" FROM t WHERE [x]", 0)]
    public void 同一個方括號名稱剩下的長度(string text, int expected)
    {
        Assert.Equal(expected, SqlIdentifier.MeasureClosingBracket(text, 0));
    }

    [Theory]
    [InlineData("SELECT [Lo", 7)]
    [InlineData("SELECT [a[b", 7)]
    [InlineData("SELECT [a]]b", 7)]
    [InlineData("SELECT [a] [b", 11)]
    [InlineData("SELECT [a]", -1)]
    [InlineData("SELECT '[a", -1)]
    [InlineData("SELECT [a\nb", -1)]
    public void 找出還沒關上的左方括號(string textBeforeCaret, int expected)
    {
        Assert.Equal(expected, SqlIdentifier.FindOpenBracket(textBeforeCaret));
    }
}
