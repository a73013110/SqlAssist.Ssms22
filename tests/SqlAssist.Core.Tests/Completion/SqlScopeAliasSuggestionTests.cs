using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 欄位列得出來的地方也列得出限定它們的別名，含外層查詢的。
/// </summary>
/// <remarks>
/// 回報的症狀是 <c>MERGE … AS target USING … AS source ON |</c> 的清單裡沒有
/// <c>target</c> 與 <c>source</c>：別名只寫在這一句裡，中繼資料與指令碼宣告的名冊都沒有它。
/// </remarks>
public sealed class SqlScopeAliasSuggestionTests
{
    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    /// <summary>上下文過濾之後留下來的別名，依清單順序。</summary>
    private static string[] Aliases(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        return SuggestionContextFilter.Filter(context.ScriptSources, context)
            .Where(suggestion => suggestion.Kind == SuggestionKind.Alias)
            .Select(suggestion => suggestion.DisplayText)
            .ToArray();
    }

    [Theory]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON |")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = |")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = sou|")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN MATCHED THEN\n    UPDATE SET |")]
    public void MERGE列出target與source(string sqlWithCaret)
    {
        Assert.Equal(new[] { "target", "source" }, Aliases(sqlWithCaret));
    }

    [Theory]
    [InlineData("SELECT | FROM dbo.Loan l JOIN dbo.Copy c ON c.CopyNo = l.CopyNo")]
    [InlineData("SELECT * FROM dbo.Loan l JOIN dbo.Copy c ON |")]
    [InlineData("SELECT * FROM dbo.Loan l JOIN dbo.Copy c ON c.CopyNo = l.CopyNo WHERE |")]
    [InlineData("SELECT * FROM dbo.Loan l JOIN dbo.Copy c ON c.CopyNo = l.CopyNo ORDER BY |")]
    public void 欄位列得出來的位置列出別名(string sqlWithCaret)
    {
        Assert.Equal(new[] { "l", "c" }, Aliases(sqlWithCaret));
    }

    /// <summary>
    /// 相互關聯子查詢：自己那一層在前，外層的接在後面；同名時內層遮住外層。
    /// </summary>
    [Fact]
    public void 子查詢也列出外層的別名()
    {
        Assert.Equal(
            new[] { "c", "a", "b" },
            Aliases("SELECT * FROM dbo.Loan a JOIN dbo.Copy b ON b.CopyNo = a.CopyNo\nWHERE NOT EXISTS(SELECT * FROM dbo.Branch c WHERE c.CopyNo = |)"));
    }

    [Fact]
    public void 內層別名遮住外層同名的別名()
    {
        var context = Analyze("SELECT * FROM dbo.Loan a WHERE EXISTS (SELECT 1 FROM dbo.Copy a WHERE |)");
        var alias = Assert.Single(context.ScriptSources);

        Assert.Equal("Copy", Assert.IsType<SqlTableReference>(alias.Tag).ObjectName);
    }

    /// <summary>
    /// 名稱的位置以外不列：資料來源那一格要的是表，限定字之後要的是欄位，
    /// 別名那一格是使用者要取的新名字。
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan l JOIN |")]
    [InlineData("SELECT * FROM dbo.Loan l WHERE l.|")]
    [InlineData("SELECT * FROM dbo.Loan AS |")]
    [InlineData("SELECT * FROM dbo.Loan WHERE |")]
    public void 別名以外的位置不列(string sqlWithCaret)
    {
        Assert.Empty(Aliases(sqlWithCaret));
    }

    /// <summary>
    /// 提交寫進去的只有別名本身，照使用者的寫法：點號留給他自己打，「一律加方括號」也不套用——
    /// 那個設定管的是資料庫物件的名稱，別名是他剛在這一句取的。
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan l WHERE |", "l")]
    [InlineData("SELECT * FROM dbo.Loan AS [my loan] WHERE |", "[my loan]")]
    public void 提交別名本身(string sqlWithCaret, string expected)
    {
        var context = Analyze(sqlWithCaret);
        var alias = Assert.Single(context.ScriptSources);
        var bracketed = new SqlAssistSettings { QualifyObjectNames = true, UseSquareBrackets = true };

        Assert.Equal(expected, SqlInsertionText.Build(alias, context, bracketed));
        Assert.Contains("dbo.Loan", alias.Preview);
    }
}
