using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 文法指定了資料行所屬資料表的位置：與寫出限定字一樣只列那張表的資料行，空前綴就開。
/// </summary>
/// <remarks>
/// 回報的症狀是 <c>UPDATE t SET </c> 按了空白沒有清單，要再打一個字才有，而那份清單是整個資料庫。
/// </remarks>
public sealed class SqlColumnOwnerTests
{
    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    [Theory]
    [InlineData("UPDATE dbo.Loan SET |", "Loan")]
    [InlineData("UPDATE dbo.Loan SET Fee = 1, |", "Loan")]
    [InlineData("UPDATE dbo.Loan SET Fee = (SELECT 1), |", "Loan")]
    [InlineData("UPDATE dbo.Loan\nSET |", "Loan")]
    [InlineData("UPDATE dbo.Loan\nSET Fee = 1,\n    |", "Loan")]
    [InlineData("UPDATE TOP (5) dbo.Loan WITH (ROWLOCK) SET |", "Loan")]
    [InlineData("UPDATE l SET | FROM dbo.Loan l JOIN dbo.Copy c ON c.CopyNo = l.CopyNo", "Loan")]
    [InlineData("MERGE dbo.Loan AS t USING dbo.Copy AS s ON t.CopyNo = s.CopyNo WHEN MATCHED THEN UPDATE SET |", "Loan")]
    [InlineData("MERGE INTO dbo.Loan t USING dbo.Copy s ON t.CopyNo = s.CopyNo WHEN NOT MATCHED THEN INSERT (|", "Loan")]
    [InlineData("INSERT INTO dbo.Loan (|", "Loan")]
    [InlineData("INSERT INTO dbo.Loan (CopyNo, |", "Loan")]
    [InlineData("INSERT dbo.Loan (|", "Loan")]
    [InlineData("INSERT TOP (5) INTO dbo.Loan (|", "Loan")]
    [InlineData("CREATE INDEX ix ON dbo.Loan (|", "Loan")]
    [InlineData("CREATE INDEX ix ON dbo.Loan (CopyNo, |", "Loan")]
    [InlineData("CREATE INDEX ix ON dbo.Loan (CopyNo) INCLUDE (|", "Loan")]
    [InlineData("CREATE STATISTICS st ON dbo.Loan (|", "Loan")]
    [InlineData("ALTER TABLE dbo.Loan ADD CONSTRAINT fk FOREIGN KEY (CopyNo) REFERENCES dbo.Copy (|", "Copy")]
    [InlineData("ALTER TABLE dbo.Loan ALTER COLUMN |", "Loan")]
    [InlineData("ALTER TABLE dbo.Loan DROP COLUMN |", "Loan")]
    public void 列出所屬資料表的資料行(string sqlWithCaret, string table)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal(table, Assert.Single(context.ColumnSources!).Table?.ObjectName);
        Assert.True(SqlCompletionPolicy.Participates(context, 2));
    }

    [Theory]
    [InlineData("SET |")]
    [InlineData("SET ANSI_NULLS, |")]
    [InlineData("SELECT 1\nSET |")]
    [InlineData("UPDATE dbo.Loan SET Fee = 1\nSET |")]
    [InlineData("INSERT INTO dbo.Loan (CopyNo) VALUES (|")]
    [InlineData("CREATE INDEX ix ON dbo.Loan (CopyNo |")]
    [InlineData("CREATE INDEX ix ON dbo.Loan (CopyNo) WITH (|")]
    [InlineData("SELECT COUNT(|")]
    [InlineData("SELECT * FROM dbo.Loan ORDER BY |")]
    [InlineData("EXEC dbo.usp_Renew |")]
    public void 其餘位置沒有所屬資料表(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Null(context.ColumnOwner);
        Assert.NotEqual(CompletionTarget.Column, context.Target);
    }

    /// <summary>
    /// 資料行的位置不列工作階段選項；片語的字照列（<c>DROP COLUMN IF EXISTS</c>）。
    /// </summary>
    [Theory]
    [InlineData("UPDATE dbo.Loan\nSET |", "NOCOUNT", false)]
    [InlineData("UPDATE dbo.Loan SET |", "ROWCOUNT", false)]
    [InlineData("ALTER TABLE dbo.Loan DROP COLUMN |", "IF", true)]
    public void 資料行位置的關鍵字(string sqlWithCaret, string keyword, bool listed)
    {
        var context = Analyze(sqlWithCaret);
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty)
            .Concat(context.ClausePhrase?.Suggestions ?? Enumerable.Empty<SqlSuggestion>());

        Assert.Equal(
            listed,
            SuggestionContextFilter.Filter(suggestions, context)
                .Any(suggestion => suggestion.Kind == SuggestionKind.Keyword && suggestion.DisplayText == keyword));
    }
}
