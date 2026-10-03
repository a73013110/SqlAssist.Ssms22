using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 具名視窗：<c>OVER </c> 與視窗規格的左括號之後列出游標所在查詢的 WINDOW 子句取的名稱。
/// </summary>
public sealed class SqlWindowCompletionTests
{
    private const string Windows = " FROM dbo.Loan WINDOW ByBranch AS (PARTITION BY BranchId), ByDate AS (ByBranch ORDER BY LoanDate)";

    [Theory]
    [InlineData("SELECT SUM(Fee) OVER |" + Windows)]
    [InlineData("SELECT SUM(Fee) OVER (|)" + Windows)]
    [InlineData("SELECT BranchId" + Windows + " ORDER BY SUM(Fee) OVER |")]
    public async Task OVER之後列出這個查詢的視窗(string sqlWithCaret)
    {
        var names = (await GetAsync(sqlWithCaret)).Where(item => item.Kind == SuggestionKind.Window).Select(item => item.DisplayText);

        Assert.Equal(new[] { "ByBranch", "ByDate" }, names);
    }

    /// <summary>括號裡同時列 PARTITION、ORDER 與框架；OVER 之後只有視窗名稱。</summary>
    [Fact]
    public async Task 視窗規格的括號裡列子句的字()
    {
        var open = (await GetAsync("SELECT SUM(Fee) OVER (|)" + Windows)).Select(item => item.DisplayText).ToArray();
        var over = await GetAsync("SELECT SUM(Fee) OVER |" + Windows);

        Assert.Contains("PARTITION", open);
        Assert.Contains("ORDER", open);
        Assert.DoesNotContain("Loan", open);
        Assert.All(over, item => Assert.Equal(SuggestionKind.Window, item.Kind));
    }

    /// <summary>視窗不能以自己為基底；基底名稱之後不再列視窗。</summary>
    [Fact]
    public async Task 定義裡的基底不列自己()
    {
        var names = (await GetAsync("SELECT 1 FROM dbo.Loan WINDOW ByBranch AS (PARTITION BY BranchId), ByDate AS (|)"))
            .Where(item => item.Kind == SuggestionKind.Window)
            .Select(item => item.DisplayText);
        var afterBase = await GetAsync("SELECT SUM(Fee) OVER (ByBranch |)" + Windows);

        Assert.Equal(new[] { "ByBranch" }, names);
        Assert.DoesNotContain(afterBase, item => item.Kind == SuggestionKind.Window);
        Assert.Contains(afterBase, item => item.DisplayText == "ROWS");
    }

    /// <summary>子查詢與下一句的 WINDOW 子句屬於它們自己。</summary>
    [Theory]
    [InlineData("SELECT SUM(Fee) OVER |, (SELECT 1 FROM dbo.Copy WINDOW Inner1 AS ()) FROM dbo.Loan WINDOW Outer1 AS ()", "Outer1")]
    [InlineData("SELECT SUM(Fee) OVER | FROM dbo.Loan WINDOW Outer1 AS ();\nSELECT 1 FROM dbo.Copy WINDOW Next1 AS ()", "Outer1")]
    public async Task 只列這一層的視窗(string sqlWithCaret, string expected)
    {
        var names = (await GetAsync(sqlWithCaret)).Where(item => item.Kind == SuggestionKind.Window).Select(item => item.DisplayText);

        Assert.Equal(new[] { expected }, names);
    }

    /// <summary>WINDOW 之後與一項寫完的逗號之後是新名字；名字之後只有 AS。</summary>
    [Theory]
    [InlineData("SELECT 1 FROM dbo.Loan WINDOW |", SqlCompletionSlot.Name)]
    [InlineData("SELECT 1 FROM dbo.Loan WINDOW ByBranch AS (PARTITION BY BranchId), |", SqlCompletionSlot.Name)]
    [InlineData("SELECT 1 FROM dbo.Loan WINDOW ByBranch |", SqlCompletionSlot.Grammar)]
    public void WINDOW子句的名字與AS(string sqlWithCaret, SqlCompletionSlot slot)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(slot, context.Slot);
    }

    [Theory]
    [InlineData("SELECT 1 FROM dbo.Loan WINDOW ByBranch |")]
    [InlineData("SELECT 1 FROM dbo.Loan WINDOW ByBranch AS (PARTITION BY BranchId), ByDate |")]
    public async Task 視窗名稱之後只列AS(string sqlWithCaret)
    {
        Assert.Equal(new[] { "AS" }, (await GetAsync(sqlWithCaret)).Select(item => item.DisplayText));
    }

    private static Task<IReadOnlyList<SqlSuggestion>> GetAsync(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        return SqlCompletionCandidates.GetAsync(
            context,
            BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current),
            new SqlAssistSettings(),
            SqlCompletionMetadata.None,
            CancellationToken.None);
    }
}
