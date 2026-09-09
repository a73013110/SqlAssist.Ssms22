using System.Linq;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetSearchTests
{
    private static readonly SqlSnippet[] Snippets =
    {
        new("be", "", "BEGIN END", "區塊內容"),
        new("trn", "", "TRANSACTION", "交易提交"),
        new("trr", "", "TRANSACTION ROLLBACK", "交易試跑")
    };

    [Theory]
    [InlineData("  ", "be,trn,trr")]
    [InlineData(null, "be,trn,trr")]
    [InlineData("TR", "trn,trr")]
    [InlineData("交易", "trn,trr")]
    [InlineData("trans 試跑", "trr")]
    [InlineData("\ttrans\r\n提交 ", "trn")]
    [InlineData("找不到", "")]
    public void 多詞跨欄位比對且保留設定順序(string? query, string expected)
    {
        Assert.Equal(expected, string.Join(",", SqlSnippetSearch.Filter(Snippets, query).Select(item => item.Shortcut)));
        Assert.Equal(3, Snippets.Length);
    }

    [Fact]
    public void 空搜尋不複製不可變清單()
    {
        Assert.Same(Snippets, SqlSnippetSearch.Filter(Snippets, " "));
    }
}
