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

    /// <summary>
    /// 指派的左邊寫得出限定字（<c>SET t.Fee =</c>）：範圍裡的別名與資料行同列，MERGE 兩邊的都在。
    /// 其他資料行的位置不寫限定字，不列。
    /// </summary>
    [Theory]
    [InlineData("MERGE dbo.Loan AS t USING (SELECT 1, 2) AS s (CopyNo, Fee) ON t.CopyNo = s.CopyNo WHEN MATCHED THEN UPDATE SET |", "t", "s")]
    [InlineData("MERGE dbo.Loan AS t USING dbo.Copy AS s ON t.CopyNo = s.CopyNo WHEN MATCHED THEN UPDATE SET t.Fee = s.Fee, |", "t", "s")]
    [InlineData("UPDATE l SET | FROM dbo.Loan l JOIN dbo.Copy c ON c.CopyNo = l.CopyNo", "l", "c")]
    public void 指派的左邊另列別名(string sqlWithCaret, params string[] aliases)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal(aliases, context.ScriptSources.Select(item => item.DisplayText));
    }

    [Theory]
    [InlineData("MERGE INTO dbo.Loan t USING dbo.Copy s ON t.CopyNo = s.CopyNo WHEN NOT MATCHED THEN INSERT (|")]
    [InlineData("INSERT INTO dbo.Loan (|")]
    public void 資料行清單不列別名(string sqlWithCaret)
    {
        Assert.Empty(Analyze(sqlWithCaret).ScriptSources);
    }

    [Theory]
    [InlineData("ALTER TABLE dbo.Loan ADD CONSTRAINT pk PRIMARY KEY CLUSTERED (|")]
    [InlineData("ALTER TABLE dbo.Loan ADD UNIQUE (CopyNo, |")]
    [InlineData("ALTER TABLE dbo.Loan WITH NOCHECK ADD CONSTRAINT fk FOREIGN KEY (|")]
    public void ALTER_TABLE_ADD的條件約束清單列那張表的資料行(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal("Loan", Assert.Single(context.ColumnSources!).Table?.ObjectName);
        Assert.True(SqlCompletionPolicy.IsClosed(context));
    }

    /// <summary>
    /// 資料表定義（CREATE TABLE、資料表變數、資料表型別）的條件約束與索引清單列同一份定義寫出來的資料行：中繼資料查不到。
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int, PRIMARY KEY (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int, CONSTRAINT pk PRIMARY KEY NONCLUSTERED (CopyNo, |")]
    [InlineData("CREATE TABLE #Loan (CopyNo int,\n    Branch int NOT NULL,\n    UNIQUE (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int, INDEX ix_Loan NONCLUSTERED (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int, INDEX ix_Loan (CopyNo) INCLUDE (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int, FOREIGN KEY (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int REFERENCES dbo.Loan (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo datetime2, Branch datetime2, PERIOD FOR SYSTEM_TIME (|")]
    [InlineData("CREATE TABLE dbo.Loan (PRIMARY KEY (|), CopyNo int, Branch int)")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int NOT NULL -- 分館\n    PRIMARY KEY (CopyNo, |")]
    [InlineData("DECLARE @Loan TABLE (CopyNo int, Branch int, PRIMARY KEY (|")]
    [InlineData("CREATE FUNCTION dbo.fn_Loan() RETURNS @Loan TABLE (CopyNo int, Branch int UNIQUE (|")]
    [InlineData("CREATE TYPE dbo.LoanList AS TABLE (CopyNo int, Branch int, INDEX ix (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int, INDEX ix NONCLUSTERED HASH (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int, INDEX ix UNIQUE HASH (CopyNo, |")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, Branch int INDEX ix HASH (|")]
    [InlineData("CREATE TYPE dbo.LoanList AS TABLE (CopyNo int, Branch int, PRIMARY KEY NONCLUSTERED HASH (|")]
    public void 資料表定義的條件約束清單列同一份定義的資料行(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal(new[] { "CopyNo", "Branch" }, Assert.Single(context.ColumnSources!).Names);
        Assert.True(SqlCompletionPolicy.IsClosed(context));
    }

    [Theory]
    [InlineData("CREATE TABLE dbo.Loan (|")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, |")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, PRIMARY KEY (CopyNo) WITH (|")]
    public void CREATE_TABLE的資料行定義是新名字(string sqlWithCaret)
    {
        Assert.Null(Analyze(sqlWithCaret).ColumnOwner);
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
    [InlineData("ALTER TABLE dbo.Loan DROP COLUMN |", "IF EXISTS", true)]
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
