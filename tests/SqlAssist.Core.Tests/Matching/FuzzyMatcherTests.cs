using System.Linq;
using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class FuzzyMatcherTests
{
    [Theory]
    [InlineData("libr", "Lib_Reader")]
    [InlineData("lr", "Lib_Reader")]
    [InlineData("libreader", "Lib_Reader")]
    [InlineData("LIBR", "lib_reader")]
    [InlineData("ssf", "ssf")]
    [InlineData("ot", "LoanTotal")]
    [InlineData("uspgl", "usp_GetLoan")]
    public void 可以命中詞首縮寫與子序列(string pattern, string candidate)
    {
        Assert.True(FuzzyMatcher.Match(pattern, candidate).IsMatch);
    }

    [Theory]
    [InlineData("xyz", "Lib_Reader")]
    [InlineData("reader_lib", "Lib_Reader")]
    [InlineData("lib_readerx", "Lib_Reader")]
    public void 不是子序列時不命中(string pattern, string candidate)
    {
        Assert.False(FuzzyMatcher.Match(pattern, candidate).IsMatch);
    }

    [Fact]
    public void 空樣式視為命中且分數為零()
    {
        var result = FuzzyMatcher.Match(string.Empty, "Lib_Reader");

        Assert.True(result.IsMatch);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Spans);
    }

    [Fact]
    public void 樣式比候選項長時不命中()
    {
        Assert.False(FuzzyMatcher.Match("Lib_Reader_Extra", "Lib_Reader").IsMatch);
    }

    /// <summary>
    /// 這是使用者回報的核心情境：底線後方的 R 必須被視為詞首，
    /// 讓 libr 在 Lib_Reader 上的分數高於只是把 libr 當成子字串的候選項。
    /// </summary>
    [Fact]
    public void 底線後方視為詞首_libr在Lib_Reader上優於子字串命中()
    {
        var boundary = FuzzyMatcher.Match("libr", "Lib_Reader");
        var substring = FuzzyMatcher.Match("libr", "MyLibrTable");

        Assert.True(boundary.IsMatch);
        Assert.True(substring.IsMatch);
        Assert.True(
            boundary.Score > substring.Score,
            $"Lib_Reader({boundary.Score}) 應高於 MyLibrTable({substring.Score})");
    }

    [Fact]
    public void 底線之後的字元取得詞首加成()
    {
        var boundary = FuzzyMatcher.Match("lr", "Lib_Reader");
        var noBoundary = FuzzyMatcher.Match("lr", "Libareader");

        Assert.True(
            boundary.Score > noBoundary.Score,
            $"Lib_Reader({boundary.Score}) 應高於 Libareader({noBoundary.Score})");
    }

    [Fact]
    public void camelCase轉折取得詞首加成()
    {
        var camel = FuzzyMatcher.Match("lr", "LibReader");
        var flat = FuzzyMatcher.Match("lr", "Libreader");

        Assert.True(
            camel.Score > flat.Score,
            $"LibReader({camel.Score}) 應高於 Libreader({flat.Score})");
    }

    /// <summary>
    /// 完全連續的前綴命中不需要付出任何 gap 代價，因此應該勝過中間隔著分隔符的
    /// 縮寫命中。這是刻意的取捨：使用者打完整前綴時，最直白的候選項要排最前面。
    /// </summary>
    [Fact]
    public void 連續命中優於隔著分隔符的縮寫命中()
    {
        var contiguous = FuzzyMatcher.Match("libr", "LibReader");
        var acronym = FuzzyMatcher.Match("libr", "Lib_Reader");

        Assert.True(
            contiguous.Score > acronym.Score,
            $"LibReader({contiguous.Score}) 應高於 Lib_Reader({acronym.Score})");
    }

    [Fact]
    public void 真正的連續前綴優於跨詞縮寫()
    {
        var prefix = FuzzyMatcher.Match("lo", "Location");
        var acronym = FuzzyMatcher.Match("lo", "Lib_Overdue");

        Assert.True(
            prefix.Score > acronym.Score,
            $"Location({prefix.Score}) 應高於 Lib_Overdue({acronym.Score})");
    }

    [Fact]
    public void 從開頭命中優於從中間命中()
    {
        var head = FuzzyMatcher.Match("book", "BookSummary");
        var tail = FuzzyMatcher.Match("book", "ArchivedBook");

        Assert.True(head.Score > tail.Score);
    }

    [Fact]
    public void 名稱較短時分數不會較低()
    {
        var shorter = FuzzyMatcher.Match("loan", "Loan");
        var longer = FuzzyMatcher.Match("loan", "LoanDetailHistory");

        Assert.True(shorter.Score >= longer.Score);
    }

    [Fact]
    public void 連續命中的區段會合併成單一Span()
    {
        var result = FuzzyMatcher.Match("lib", "Lib_Reader");

        Assert.True(result.IsMatch);
        Assert.Equal(new[] { new MatchSpan(0, 3) }, result.Spans);
    }

    [Fact]
    public void 跨詞命中會產生多個Span()
    {
        var result = FuzzyMatcher.Match("libr", "Lib_Reader");

        Assert.True(result.IsMatch);
        Assert.Equal(new[] { new MatchSpan(0, 3), new MatchSpan(4, 1) }, result.Spans);
    }

    [Fact]
    public void 單字元樣式回傳單一位置的Span()
    {
        var result = FuzzyMatcher.Match("r", "Lib_Reader");

        Assert.True(result.IsMatch);
        Assert.Equal(new[] { new MatchSpan(4, 1) }, result.Spans);
    }

    [Fact]
    public void 命中Span的字元必定與樣式相符()
    {
        const string candidate = "usp_GetPublisherLoan";
        var result = FuzzyMatcher.Match("uspgpl", candidate);

        Assert.True(result.IsMatch);

        var matched = string.Concat(
            result.Spans.SelectMany(span => candidate.Substring(span.Start, span.Length)));

        Assert.Equal("uspgpl", matched.ToLowerInvariant());
    }

    [Fact]
    public void Span依起點遞增且不重疊()
    {
        var result = FuzzyMatcher.Match("lrtl", "Lib_Reader_Total_Loan");

        Assert.True(result.IsMatch);

        var spans = result.Spans;

        for (var index = 1; index < spans.Count; index++)
        {
            Assert.True(spans[index].Start >= spans[index - 1].End);
        }
    }

    [Fact]
    public void 井號開頭的暫存表被視為詞首()
    {
        var temporary = FuzzyMatcher.Match("t", "#Temp");
        var middle = FuzzyMatcher.Match("t", "Batch");

        Assert.True(temporary.Score > middle.Score);
    }

    [Fact]
    public void 沒有大小寫的文字仍可比對()
    {
        var result = FuzzyMatcher.Match("讀者", "圖書館_讀者資料");

        Assert.True(result.IsMatch);
    }

    [Theory]
    [InlineData("libr", "Lib_Reader", true)]
    [InlineData("libr", "Lib_Tag", false)]
    public void 子序列預檢與完整比對結果一致(string pattern, string candidate, bool expected)
    {
        var normalized = FuzzyMatcher.NormalizePattern(pattern);

        Assert.Equal(expected, FuzzyMatcher.IsSubsequence(normalized, candidate));
        Assert.Equal(expected, FuzzyMatcher.MatchNormalized(normalized, candidate).IsMatch);
    }
}
