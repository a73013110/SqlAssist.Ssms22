using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>游標在哪一個呼叫的引數清單裡，現在輪到第幾個引數。</summary>
public sealed class SqlCallSignatureTests
{
    private static SqlCallSignatureContext? Resolve(string sql)
    {
        var caret = sql.IndexOf('|');
        return SqlCallSignature.Resolve(sql.Remove(caret, 1), caret);
    }

    [Fact]
    public void 認得限定名稱的呼叫()
    {
        var site = Resolve("SELECT dbo.dtoc(|)");

        Assert.NotNull(site);
        Assert.Equal(0, site!.ArgumentIndex);
        Assert.Equal("SELECT dbo.".Length, site.NameStart);
        Assert.Equal("SELECT dbo.dtoc".Length, site.NameEnd);
        Assert.Equal("SELECT dbo.dtoc".Length, site.OpenParenthesis);
        Assert.True(site.IsQualified);
        Assert.False(Resolve("SELECT DATEDIFF(|")!.IsQualified);
    }

    [Fact]
    public void 逗號往下走一格()
    {
        Assert.Equal(2, Resolve("SELECT dbo.fn_Fee(1, 2, |)")!.ArgumentIndex);
    }

    /// <remarks>
    /// 裡面那兩個逗號屬於 <c>g</c>；數進來的話外層會停在一個不存在的第三個引數上。
    /// </remarks>
    [Fact]
    public void 巢狀呼叫的逗號不算外層的()
    {
        var site = Resolve("SELECT dbo.f(dbo.g(1, 2), |)");

        Assert.Equal(1, site!.ArgumentIndex);
        Assert.Equal("SELECT dbo.".Length, site.NameStart);
    }

    /// <remarks>游標走進裡面那一組括號時，講的就是裡面那個函式。</remarks>
    [Fact]
    public void 游標在內層時認的是內層的名稱()
    {
        var site = Resolve("SELECT dbo.f(dbo.g(1, |))");

        Assert.Equal("SELECT dbo.f(dbo.".Length, site!.NameStart);
        Assert.Equal(1, site.ArgumentIndex);
    }

    /// <remarks>
    /// 關鍵字後面的括號是語法的一部分或是內建函式，兩種都不歸這裡管。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM t WHERE (|")]
    [InlineData("INSERT INTO t VALUES (|")]
    [InlineData("SELECT CONVERT(|")]
    [InlineData("SELECT (1 + 2) * (|")]
    public void 不是呼叫的括號一律不認(string sql)
    {
        Assert.Null(Resolve(sql));
    }

    /// <remarks>
    /// 有限定字就已經在說「這是一個物件」，拿最後一段去比關鍵字清單會把它擋掉。
    /// </remarks>
    [Fact]
    public void 限定名稱不受關鍵字清單影響()
    {
        Assert.NotNull(Resolve("SELECT dbo.[Left](|"));
        Assert.NotNull(Resolve("SELECT dbo.Value(|"));
    }

    /// <remarks>
    /// 參數提示續接只問「游標是不是在呼叫裡」，SSMS 那一份正好涵蓋這幾個關鍵字函式；
    /// 語法括號照樣不認。
    /// </remarks>
    [Theory]
    [InlineData("SELECT CONVERT(|", true)]
    [InlineData("SELECT LEFT(|", true)]
    [InlineData("SELECT DATEDIFF(|", true)]
    [InlineData("INSERT INTO t VALUES (|", false)]
    [InlineData("SELECT * FROM t WHERE a IN (|", false)]
    public void 可以一併認得關鍵字型的內建函式(string sql, bool expected)
    {
        var caret = sql.IndexOf('|');
        var site = SqlCallSignature.Resolve(sql.Remove(caret, 1), caret, includeKeywordFunctions: true);

        Assert.Equal(expected, site is not null);
    }

    [Fact]
    public void 括號已經收掉就不在清單裡()
    {
        Assert.Null(Resolve("SELECT dbo.dtoc(GETDATE())|"));
    }

    /// <remarks>字串與註解裡的括號是內容不是語法。</remarks>
    [Theory]
    [InlineData("SELECT 'dbo.dtoc(|'")]
    [InlineData("-- dbo.dtoc(|")]
    public void 字串與註解裡不認(string sql)
    {
        Assert.Null(Resolve(sql));
    }

    /// <remarks>
    /// 提示開著的期間每一次游標移動都要問一次，所以只吃左括號到游標那一段。
    /// </remarks>
    [Theory]
    [InlineData("(", 0)]
    [InlineData("(1, ", 1)]
    [InlineData("(1, 2, ", 2)]
    [InlineData("(dbo.g(1, 2), ", 1)]
    [InlineData("(N'a, b', ", 1)]
    [InlineData("((1 + ", 0)]
    [InlineData("(1, (SELECT MAX(a) FROM t WHERE b IN (", 1)]
    public void 只看引數清單那一段也數得出來(string argumentList, int expected)
    {
        Assert.Equal(expected, SqlCallSignature.TrackArgument(argumentList));
    }

    /// <remarks>括號被刪掉、或已經在游標之前收起來，兩種都代表提示該收掉了。</remarks>
    [Theory]
    [InlineData("")]
    [InlineData("dbo.dtoc(")]
    [InlineData("(1) + 2")]
    public void 已經離開引數清單時回傳空值(string argumentList)
    {
        Assert.Null(SqlCallSignature.TrackArgument(argumentList));
    }

    /// <remarks>引數裡的呼叫歸內層那一份，外層的提示要讓開，否則兩份簽章疊在一起。</remarks>
    [Theory]
    [InlineData("(GETDATE(")]
    [InlineData("(1, dbo.g(")]
    [InlineData("((CONVERT(")]
    public void 走進內層呼叫時回傳空值(string argumentList)
    {
        Assert.Null(SqlCallSignature.TrackArgument(argumentList));
    }
}
