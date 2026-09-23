using System.Linq;
using SqlAssist.Core.Matching;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryPreviewMatchesTests
{
    private const string Sql = "SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo = @copyNo;";

    private static SqlMemoryQuery Query(string search, TextMatchOptions options)
    {
        var model = new SqlMemoryBrowserModel { Search = search, MatchOptions = options };
        return model.Query();
    }

    [Fact]
    public void 沒有搜尋字時不標也不說話()
    {
        var matches = SqlMemoryPreviewMatches.Locate(Query("", TextMatchOptions.None).Matcher, Sql, favorite: false);

        Assert.Same(SqlMemoryPreviewMatches.None, matches);
        Assert.Empty(matches.Spans);
        Assert.Equal("", matches.Notice);
    }

    /// <remarks>
    /// 位置出自清單那一輪的比對器：同一個搜尋字，換了選項就標在不同的地方。
    /// </remarks>
    [Theory]
    [InlineData(TextMatchOptions.None, 3)]
    [InlineData(TextMatchOptions.MatchCasing, 2)]
    [InlineData(TextMatchOptions.WholeWord, 3)]
    public void 位置照清單那一輪的搜尋字與選項(TextMatchOptions options, int expected)
    {
        var matches = SqlMemoryPreviewMatches.Locate(Query("CopyNo", options).Matcher, Sql, favorite: false);

        Assert.Equal(expected, matches.Spans.Count);
        Assert.All(matches.Spans, span => Assert.Equal("CopyNo", Sql.Substring(span.Start, span.Length), ignoreCase: true));
        Assert.Equal("", matches.Notice);
    }

    [Fact]
    public void 整個字不標在較長識別字的中間()
    {
        var matches = SqlMemoryPreviewMatches.Locate(Query("Copy", TextMatchOptions.WholeWord).Matcher, Sql, favorite: false);

        Assert.Empty(matches.Spans);
    }

    [Fact]
    public void 命中太多時截斷並說出來()
    {
        var sql = string.Join(" ", Enumerable.Repeat("CopyNo", MatchHighlights.Maximum + 1));

        var matches = SqlMemoryPreviewMatches.Locate(Query("CopyNo", TextMatchOptions.None).Matcher, sql, favorite: true);

        Assert.Equal(MatchHighlights.Maximum, matches.Spans.Count);
        Assert.Equal(MatchHighlights.TruncatedNotice, matches.Notice);
    }

    /// <remarks>
    /// 收藏比對名稱、說明與 SQL 的聯集；只靠名稱命中時 SQL 上沒有標記，要說出原因。History 只比對 SQL，不說。
    /// </remarks>
    [Fact]
    public void 收藏只靠名稱或說明命中時說明SQL裡沒有()
    {
        var matcher = Query("借閱", TextMatchOptions.None).Matcher;

        Assert.Equal(SqlMemoryPreviewMatches.OutsideSqlNotice, SqlMemoryPreviewMatches.Locate(matcher, Sql, favorite: true).Notice);
        Assert.Equal("", SqlMemoryPreviewMatches.Locate(matcher, Sql, favorite: false).Notice);
    }
}
