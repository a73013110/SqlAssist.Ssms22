using System.Linq;
using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class MatchHighlightsTests
{
    private const string Sql = "SELECT CopyNo FROM Cat_BookCopy WHERE copyno = @CopyNo;";

    private static string[] Texts(string text, TextMatcher matcher) =>
        MatchHighlights.Locate(matcher, text, out _).Select(span => text.Substring(span.Start, span.Length)).ToArray();

    [Fact]
    public void 沒有修飾時不分大小寫也不看詞界()
    {
        Assert.Equal(new[] { "Copy", "Copy", "copy", "Copy" }, Texts(Sql, new TextMatcher("copy", TextMatchOptions.None)));
    }

    /// <remarks>
    /// 位置照比對器的選項走：清單上用大小寫相同與整個字篩出來的那一列，預覽標的就只能是那幾處。
    /// </remarks>
    [Fact]
    public void 大小寫相同與整個字照比對器的選項()
    {
        var matcher = new TextMatcher("CopyNo", TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord);

        var spans = MatchHighlights.Locate(matcher, Sql, out var truncated);

        Assert.False(truncated);
        Assert.Equal(new[] { Sql.IndexOf("CopyNo"), Sql.LastIndexOf("CopyNo") }, spans.Select(span => span.Start));
        Assert.All(spans, span => Assert.Equal(6, span.Length));
    }

    [Fact]
    public void 沒有命中是空的()
    {
        Assert.Empty(MatchHighlights.Locate(new TextMatcher("Loan", TextMatchOptions.None), Sql, out var truncated));
        Assert.False(truncated);
    }

    /// <remarks>
    /// 文件那一層要不重疊的區段；重疊或緊貼的出現併成一段，算一處。
    /// </remarks>
    [Fact]
    public void 重疊與緊貼的出現併成一段()
    {
        var spans = MatchHighlights.Locate(new TextMatcher("aa", TextMatchOptions.None), "aaa b aaaa", out _);

        Assert.Equal(new[] { new MatchSpan(0, 3), new MatchSpan(6, 4) }, spans);
    }

    [Fact]
    public void 剛好達到上限不算少標()
    {
        var text = string.Join(" ", Enumerable.Repeat("Loan", MatchHighlights.Maximum));

        var spans = MatchHighlights.Locate(new TextMatcher("Loan", TextMatchOptions.None), text, out var truncated);

        Assert.Equal(MatchHighlights.Maximum, spans.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void 超過上限時截斷並回報少標了()
    {
        var text = string.Join(" ", Enumerable.Repeat("Loan", MatchHighlights.Maximum + 1));

        var spans = MatchHighlights.Locate(new TextMatcher("Loan", TextMatchOptions.None), text, out var truncated);

        Assert.Equal(MatchHighlights.Maximum, spans.Count);
        Assert.True(truncated);
        Assert.Equal(text.LastIndexOf("Loan") - 5, spans[spans.Count - 1].Start);
    }

    /// <remarks>
    /// 上限數的是標記不是出現次數：一長串連在一起的出現只是一處，不能因為出現了幾千次就說少標了。
    /// </remarks>
    [Fact]
    public void 上限數的是併完之後的標記()
    {
        var text = new string('a', MatchHighlights.Maximum * 4);

        var span = Assert.Single(MatchHighlights.Locate(new TextMatcher("a", TextMatchOptions.None), text, out var truncated));

        Assert.Equal(new MatchSpan(0, text.Length), span);
        Assert.False(truncated);
    }
}
