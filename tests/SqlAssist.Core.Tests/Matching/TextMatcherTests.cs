using System;
using System.Text;
using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class TextMatcherTests
{
    private const TextMatchOptions Exact = TextMatchOptions.MatchCasing;
    private const TextMatchOptions ExactWord = TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord;

    [Fact]
    public void 找得到片段第一次出現的位置()
    {
        Assert.Equal(9, new TextMatcher("FROM", Exact).IndexOf("SELECT * FROM Loan"));
    }

    [Fact]
    public void 從指定位置之後才找()
    {
        Assert.Equal(10, new TextMatcher("Loan", Exact).IndexOf("Loan JOIN Loan", 1));
    }

    [Fact]
    public void 找不到時回傳負一()
    {
        Assert.Equal(-1, new TextMatcher("Loan", Exact).IndexOf("SELECT 1"));
    }

    [Fact]
    public void 取最後一次出現()
    {
        Assert.Equal(8, new TextMatcher("Loan", Exact).LastIndexOf("[Loan].[Loan]"));
    }

    /// <remarks>重疊時由前往後走，挑到的是與 <c>IndexOf</c> 同一條規則下的最後一處。</remarks>
    [Fact]
    public void 最後一次出現照樣算重疊()
    {
        Assert.Equal(2, new TextMatcher("aa", Exact).LastIndexOf("aaaa"));
    }

    [Theory]
    [InlineData("[CopyNo] int")]
    [InlineData("CopyNo int")]
    [InlineData("a.CopyNo = 1")]
    [InlineData("#CopyNo")]
    public void 方括號點與井號都算詞界(string text)
    {
        Assert.True(new TextMatcher("CopyNo", ExactWord).IsMatch(text));
    }

    [Fact]
    public void 整個字不命中別的字裡面那一段()
    {
        Assert.Equal(-1, new TextMatcher("Copy", ExactWord).IndexOf("[CopyNo] int"));
    }

    /// <remarks>詞界不合的那一次之後可能緊接著一次合格的；停在第一次找到的位置就會整組放棄。</remarks>
    [Fact]
    public void 詞界不合時繼續往後找()
    {
        Assert.Equal(11, new TextMatcher("CopyNo", ExactWord).IndexOf("CopyNoTag [CopyNo]"));
    }

    [Fact]
    public void 預設不分大小寫()
    {
        Assert.Equal(-1, new TextMatcher("CopyNo", ExactWord).IndexOf("[COPYNO]"));
        Assert.Equal(1, new TextMatcher("CopyNo", TextMatchOptions.WholeWord).IndexOf("[COPYNO]"));
        Assert.True(new TextMatcher("copyno", TextMatchOptions.None).IsMatch("Cat_CopyNo"));
    }

    [Fact]
    public void 空樣式與越界起點都不算命中()
    {
        Assert.Equal(-1, new TextMatcher("", Exact).IndexOf("SELECT 1"));
        Assert.Equal(-1, new TextMatcher("SELECT", Exact).IndexOf("SELECT 1", 5));
        Assert.Equal(-1, new TextMatcher("SELECT 1", Exact).IndexOf("SELECT"));
        Assert.Equal(-1, new TextMatcher("SELECT", Exact).IndexOf("SELECT", 99));
        Assert.False(new TextMatcher("", Exact).IsMatchUtf16(Utf16("SELECT")));
        Assert.False(new TextMatcher("Loan", Exact).IsMatch(null));
    }

    [Fact]
    public void 認不得的選項位元直接拒絕()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextMatcher("x", (TextMatchOptions)4));
    }

    /// <remarks>
    /// 預覽那一端靠它走「上一個／下一個命中」：一個資料行名稱在 <c>CREATE TABLE</c> 裡出現
    /// 一次，在擴充屬性那一串裡還會再出現一次，只回第一次的話第二處走不過去。
    /// </remarks>
    [Fact]
    public void 找出全部出現()
    {
        const string script = "[ReturnDate] datetime, -- ReturnDate\nEXEC sp(N'ReturnDate')";

        Assert.Equal(new[] { 1, 26, 47 }, new TextMatcher("ReturnDate", TextMatchOptions.WholeWord).FindAll(script));
    }

    [Fact]
    public void 全部出現照樣認詞界與重疊()
    {
        Assert.Equal(new[] { 0, 9 }, new TextMatcher("copyno", TextMatchOptions.WholeWord).FindAll("CopyNo, [CopyNo], CopyNoted"));
        Assert.Equal(new[] { 0, 1 }, new TextMatcher("aa", Exact).FindAll("aaa"));
    }

    /// <summary>上限湊滿就不再往下掃：一份幾千行的定義本文裡同一個字出現幾百次是常態。</summary>
    [Fact]
    public void 湊滿上限就停下來()
    {
        var matcher = new TextMatcher("Loan", Exact);

        Assert.Equal(new[] { 0, 5 }, matcher.FindAll("Loan Loan Loan Loan", limit: 2));
        Assert.Empty(matcher.FindAll("Loan", limit: 0));
        Assert.Empty(matcher.FindAll("SELECT 1"));
    }

    /// <summary>
    /// 位元組那條路與字串那條路對每一種組合都要說同一句話。
    /// </summary>
    /// <remarks>
    /// SQL Memory 的全文走位元組、名稱與說明走字串，同一筆收藏兩處答案不一樣的症狀是
    /// 名稱裡找得到、SQL 裡明明也有卻不算。非 ASCII 的大小寫（全形、希臘、德文）
    /// 走 <see cref="char.ToUpperInvariant"/>，與 <see cref="StringComparison.OrdinalIgnoreCase"/> 同一份折疊。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM Lib_Reader", "lib_reader")]
    [InlineData("SELECT * FROM Lib_ReaderTag", "Lib_Reader")]
    [InlineData("-- ＬＯＡＮ 借閱", "ｌｏａｎ")]
    [InlineData("ΣΤΟΙΧΕΊΟ", "στοιχείο")]
    [InlineData("straße", "STRASSE")]
    [InlineData("abababc", "ababc")]
    [InlineData("x_Loan Loan", "Loan")]
    [InlineData("Loan_x", "Loan")]
    [InlineData("讀者收藏", "收藏")]
    public void 位元組與字串兩條路一致(string text, string pattern)
    {
        foreach (TextMatchOptions options in new[]
                 {
                     TextMatchOptions.None, TextMatchOptions.MatchCasing, TextMatchOptions.WholeWord,
                     TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord
                 })
        {
            var matcher = new TextMatcher(pattern, options);

            Assert.Equal(matcher.IsMatch(text), matcher.IsMatchUtf16(Utf16(text)));
        }
    }

    /// <remarks>先解碼的那一版會把它換成 U+FFFD，搜 U+FFFD 就誤判命中。</remarks>
    [Fact]
    public void 未配對的_surrogate_照字面比()
    {
        var bytes = new byte[] { 0x3D, 0xD8, (byte)'A', 0 };

        Assert.True(new TextMatcher("\uD83DA", Exact).IsMatchUtf16(bytes));
        Assert.False(new TextMatcher("�", TextMatchOptions.None).IsMatchUtf16(bytes));
    }

    [Fact]
    public void 奇數長度時最後一個位元組不參與()
    {
        Assert.False(new TextMatcher("AB", Exact).IsMatchUtf16(new byte[] { (byte)'A', 0, (byte)'B' }));
    }

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);
}
