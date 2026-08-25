using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SqlKeywordCaseTests
{
    /// <summary>以游標位置在文字結尾的常見情形改寫。</summary>
    private static SqlKeywordRewrite? Rewrite(string text) =>
        SqlKeywordCase.TryUppercaseWordBefore(text, text.Length);

    [Theory]
    [InlineData("select", "SELECT")]
    [InlineData("Select", "SELECT")]
    [InlineData("sElEcT", "SELECT")]
    [InlineData("inner", "INNER")]
    [InlineData("join", "JOIN")]
    [InlineData("on", "ON")]
    [InlineData("desc", "DESC")]
    public void 關鍵字改寫成大寫(string typed, string expected)
    {
        var rewrite = Rewrite(typed);

        Assert.NotNull(rewrite);
        Assert.Equal(0, rewrite!.Start);
        Assert.Equal(typed.Length, rewrite.Length);
        Assert.Equal(expected, rewrite.Replacement);
    }

    [Fact]
    public void 已經是大寫時不改寫()
    {
        // 多一次編輯就多一個復原步驟，沒有必要。
        Assert.Null(Rewrite("SELECT"));
    }

    [Fact]
    public void 只改寫游標前的那一個字()
    {
        var rewrite = Rewrite("SELECT * from");

        Assert.NotNull(rewrite);
        Assert.Equal(9, rewrite!.Start);
        Assert.Equal("FROM", rewrite.Replacement);
    }

    [Fact]
    public void 不是關鍵字就不動()
    {
        Assert.Null(Rewrite("selectx"));
        Assert.Null(Rewrite("publisher"));
    }

    [Fact]
    public void 限定字後方的名稱不是關鍵字()
    {
        // dbo.Select 是資料表名稱，不是關鍵字。
        Assert.Null(Rewrite("SELECT * FROM dbo.select"));
    }

    [Fact]
    public void 變數名稱不改寫()
    {
        // @ 是識別字的一部分，因此 @select 整個被讀成一個變數名稱。
        Assert.Null(Rewrite("DECLARE @select"));
    }

    [Fact]
    public void 字串與註解內不改寫()
    {
        Assert.Null(Rewrite("SELECT 'select"));
        Assert.Null(Rewrite("-- select"));
        Assert.Null(Rewrite("/* select"));
    }

    [Fact]
    public void 括住的識別字內不改寫()
    {
        // [select] 與 "select" 都是欄位名稱，改成大寫就換了一個物件。
        Assert.Null(Rewrite("SELECT [select"));
        Assert.Null(Rewrite("SELECT \"select"));
    }

    [Fact]
    public void 字串結束之後恢復改寫()
    {
        Assert.Equal("FROM", Rewrite("SELECT 'x' from")!.Replacement);
    }

    [Fact]
    public void 游標不在字尾時只看游標前方()
    {
        const string text = "select * FROM PUBLISHER";
        var rewrite = SqlKeywordCase.TryUppercaseWordBefore(text, 6);

        Assert.NotNull(rewrite);
        Assert.Equal(0, rewrite!.Start);
        Assert.Equal("SELECT", rewrite.Replacement);
    }

    [Fact]
    public void 游標前不是文字時不改寫()
    {
        Assert.Null(SqlKeywordCase.TryUppercaseWordBefore("select ", 7));
        Assert.Null(SqlKeywordCase.TryUppercaseWordBefore(string.Empty, 0));
        Assert.Null(SqlKeywordCase.TryUppercaseWordBefore("select", 0));
    }

    [Theory]
    [InlineData(' ', true)]
    [InlineData('\t', true)]
    [InlineData(',', true)]
    [InlineData('(', true)]
    [InlineData(';', true)]
    [InlineData('=', true)]
    [InlineData('.', true)]
    [InlineData('a', false)]
    [InlineData('_', false)]
    [InlineData('9', false)]
    [InlineData('@', false)]
    public void 判斷字的分隔字元(char value, bool expected)
    {
        Assert.Equal(expected, SqlKeywordCase.IsWordSeparator(value));
    }

    [Fact]
    public void 只做大寫的關鍵字不進建議清單()
    {
        // DESC 要能自動大寫，但沒有必要佔用清單的位置。
        Assert.True(SqlKeywordCatalog.TryGetCanonical("desc", out var canonical));
        Assert.Equal("DESC", canonical);
        Assert.DoesNotContain("DESC", SqlKeywordCatalog.SuggestionKeywords);
        Assert.Contains("SELECT", SqlKeywordCatalog.SuggestionKeywords);
    }
}
