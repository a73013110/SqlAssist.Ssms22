using System.Linq;
using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class MatchHighlightsTests
{
    private const string Sql = "SELECT CopyNo FROM Cat_BookCopy WHERE copyno = @CopyNo;";

    private static string[] Texts(string text, TextMatcher matcher) =>
        MatchHighlights.Locate(matcher, text).Spans.Select(span => text.Substring(span.Start, span.Length)).ToArray();

    [Fact]
    public void 沒有比對器時一處都不標()
    {
        Assert.Same(MatchHighlightSet.Empty, MatchHighlights.Locate(null, Sql));
    }

    [Fact]
    public void 沒有修飾時不分大小寫也不看詞界()
    {
        Assert.Equal(new[] { "Copy", "Copy", "copy", "Copy" }, Texts(Sql, new TextMatcher("copy", TextMatchOptions.None)));
    }

    /// <remarks>
    /// 位置照比對器的選項走：清單上用大小寫相同與整個字篩出來的那一列，預覽標的就只能是那幾處。
    /// </remarks>
    [Theory]
    [InlineData(TextMatchOptions.None, 3)]
    [InlineData(TextMatchOptions.MatchCasing, 2)]
    [InlineData(TextMatchOptions.WholeWord, 3)]
    [InlineData(TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord, 2)]
    public void 大小寫相同與整個字照比對器的選項(TextMatchOptions options, int expected)
    {
        var highlights = MatchHighlights.Locate(new TextMatcher("CopyNo", options), Sql);

        Assert.Equal(expected, highlights.Count);
        Assert.False(highlights.IsTruncated);
        Assert.All(highlights.Spans, span => Assert.Equal("CopyNo", Sql.Substring(span.Start, span.Length), ignoreCase: true));
    }

    [Fact]
    public void 整個字不標在較長識別字的中間()
    {
        Assert.Empty(Texts(Sql, new TextMatcher("Copy", TextMatchOptions.WholeWord)));
    }

    /// <remarks>
    /// 文件那一層要不重疊的區段；重疊或緊貼的出現併成一段，算一處。
    /// </remarks>
    [Fact]
    public void 重疊與緊貼的出現併成一段()
    {
        var highlights = MatchHighlights.Locate(new TextMatcher("aa", TextMatchOptions.None), "aaa b aaaa");

        Assert.Equal(new[] { new MatchSpan(0, 3), new MatchSpan(6, 4) }, highlights.Spans);
    }

    /// <remarks>
    /// SQL Search 的區段來自好幾個命中、沒有順序而且會互相包住；與邊找邊併的那一條走同一份規則。
    /// </remarks>
    [Fact]
    public void 任意順序的區段排序後併成不重疊的標記()
    {
        var highlights = MatchHighlights.Merge(new[]
        {
            new MatchSpan(20, 3), new MatchSpan(0, 3), new MatchSpan(0, 7), new MatchSpan(5, 4), new MatchSpan(21, 1)
        });

        Assert.Equal(new[] { new MatchSpan(0, 9), new MatchSpan(20, 3) }, highlights.Spans);
        Assert.False(highlights.IsTruncated);
    }

    [Fact]
    public void 剛好達到上限不算少標()
    {
        var text = string.Join(" ", Enumerable.Repeat("Loan", MatchHighlights.Maximum));

        var highlights = MatchHighlights.Locate(new TextMatcher("Loan", TextMatchOptions.None), text);

        Assert.Equal(MatchHighlights.Maximum, highlights.Count);
        Assert.False(highlights.IsTruncated);
    }

    [Fact]
    public void 超過上限時兩條路都截斷並回報少標了()
    {
        var text = string.Join(" ", Enumerable.Repeat("Loan", MatchHighlights.Maximum + 1));
        var located = MatchHighlights.Locate(new TextMatcher("Loan", TextMatchOptions.None), text);
        var merged = MatchHighlights.Merge(Enumerable.Range(0, MatchHighlights.Maximum + 1).Select(index => new MatchSpan(index * 5, 4)).Reverse());

        Assert.Equal((MatchHighlights.Maximum, true), (located.Count, located.IsTruncated));
        Assert.Equal(located.Spans, merged.Spans);
        Assert.True(merged.IsTruncated);
    }

    /// <remarks>
    /// 上限數的是標記不是出現次數：一長串連在一起的出現只是一處，不能因為出現了幾千次就說少標了。
    /// </remarks>
    [Fact]
    public void 上限數的是併完之後的標記()
    {
        var text = new string('a', MatchHighlights.Maximum * 4);

        var highlights = MatchHighlights.Locate(new TextMatcher("a", TextMatchOptions.None), text);

        Assert.Equal(new MatchSpan(0, text.Length), Assert.Single(highlights.Spans));
        Assert.False(highlights.IsTruncated);
    }

    /// <remarks>
    /// 少標了永遠先說；一處都沒有時說功能自己給的那一句，沒給就不說話。
    /// </remarks>
    [Fact]
    public void 提示的優先順序只有一份()
    {
        var truncated = MatchHighlights.Locate(new TextMatcher("Loan", TextMatchOptions.None),
            string.Join(" ", Enumerable.Repeat("Loan", MatchHighlights.Maximum + 1)));
        var found = MatchHighlights.Locate(new TextMatcher("CopyNo", TextMatchOptions.None), Sql);
        var none = MatchHighlights.Locate(new TextMatcher("Loan", TextMatchOptions.None), Sql);

        Assert.Equal(MatchHighlights.TruncatedNotice, truncated.Notice("對不上"));
        Assert.Equal("", found.Notice("對不上"));
        Assert.Equal("對不上", none.Notice("對不上"));
        Assert.Equal("", none.Notice(null));
    }
}
