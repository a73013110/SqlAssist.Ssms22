using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 「只有幾個字合法」的引數與提示位置。
/// </summary>
public sealed class SqlArgumentPositionTests
{
    [Theory]
    [InlineData("SELECT DATEADD(")]
    [InlineData("SELECT DATEDIFF(")]
    [InlineData("SELECT DATENAME(")]
    [InlineData("SELECT DATEPART(")]
    [InlineData("SELECT DATETRUNC(")]
    [InlineData("SELECT DATEADD(DA")]
    public void 日期部分的位置(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.DatePart, context.Target);
    }

    /// <summary>只有第一個引數是日期部分；打過逗號之後要的是數字與日期。</summary>
    [Theory]
    [InlineData("SELECT DATEADD(DAY, ")]
    [InlineData("SELECT DATEDIFF(DAY, l.LoanedOn, ")]
    public void 第二個引數之後不是日期部分(string textBeforeCaret)
    {
        Assert.NotEqual(
            CompletionTarget.DatePart,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Loan WITH (")]
    [InlineData("SELECT * FROM dbo.Loan l WITH (NOLOCK, ")]
    [InlineData("SELECT * FROM dbo.Loan WITH (NOL")]
    public void 資料表提示的位置(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.TableHint, context.Target);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Loan OPTION (")]
    [InlineData("SELECT * FROM dbo.Loan OPTION (RECOMPILE, ")]
    [InlineData("SELECT * FROM dbo.Loan OPTION (MAXD")]
    public void 查詢提示的位置(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.QueryHint, context.Target);
    }

    /// <summary>
    /// CTE 的 <c>WITH</c> 後面接的是名稱，中間隔著那個名稱才是左括號。
    /// </summary>
    [Theory]
    [InlineData(";WITH CTE_TEST AS (")]
    [InlineData("SELECT * FROM dbo.Loan WHERE LoanId IN (")]
    [InlineData("SELECT COUNT(")]
    [InlineData("INSERT INTO dbo.Loan (")]
    public void 不是提示的括號(string textBeforeCaret)
    {
        var target = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target;

        Assert.NotEqual(CompletionTarget.TableHint, target);
        Assert.NotEqual(CompletionTarget.QueryHint, target);
    }

    [Fact]
    public void 各自的位置只列自己那一份()
    {
        var everything = SqlArgumentCatalog.DateParts
            .Concat(SqlArgumentCatalog.TableHints)
            .Concat(SqlArgumentCatalog.QueryHints)
            .ToArray();

        Assert.All(
            SuggestionMatcher.Filter(everything, SqlCompletionContextAnalyzer.Analyze("SELECT DATEADD(")),
            item => Assert.Equal(SuggestionKind.DatePart, item.Kind));
        Assert.All(
            SuggestionMatcher.Filter(everything, SqlCompletionContextAnalyzer.Analyze("SELECT * FROM t WITH (")),
            item => Assert.Equal(SuggestionKind.TableHint, item.Kind));
        Assert.All(
            SuggestionMatcher.Filter(everything, SqlCompletionContextAnalyzer.Analyze("SELECT * FROM t OPTION (")),
            item => Assert.Equal(SuggestionKind.QueryHint, item.Kind));
    }

    /// <summary>反過來，一般位置一個都不列。</summary>
    [Theory]
    [InlineData("SELECT DA")]
    [InlineData("SELECT * FROM NOL")]
    [InlineData("SELECT REC")]
    public void 一般位置不列這三份(string textBeforeCaret)
    {
        var everything = SqlArgumentCatalog.DateParts
            .Concat(SqlArgumentCatalog.TableHints)
            .Concat(SqlArgumentCatalog.QueryHints)
            .ToArray();

        Assert.Empty(SuggestionMatcher.Filter(
            everything,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret)));
    }

    [Theory]
    [InlineData("SELECT DATEADD(MON", "MONTH")]
    [InlineData("SELECT * FROM t WITH (UPD", "UPDLOCK")]
    [InlineData("SELECT * FROM t OPTION (RECOM", "RECOMPILE")]
    public void 前綴比對排在第一(string textBeforeCaret, string expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var everything = SqlArgumentCatalog.DateParts
            .Concat(SqlArgumentCatalog.TableHints)
            .Concat(SqlArgumentCatalog.QueryHints)
            .ToArray();

        var ranked = SuggestionMatcher.Match(everything, context);

        Assert.NotEmpty(ranked);
        Assert.Equal(expected, ranked[0].DisplayText);
    }

    /// <summary><c>INDEX</c> 後面一定要接索引名稱，因此提交時補左括號。</summary>
    [Fact]
    public void 需要引數的提示補左括號()
    {
        var index = SqlArgumentCatalog.TableHints.Single(item => item.DisplayText == "INDEX");

        Assert.Equal("INDEX(", index.InsertionText);
    }
}
