using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// <c>inserted</c>／<c>deleted</c>：DML 觸發程序的主體裡指父資料表，OUTPUT 子句裡指那句 DML 的目標。
/// </summary>
/// <remarks>
/// 中繼資料查不到這兩個名字，少了這一份的症狀是 <c>OUTPUT </c> 與 <c>FROM </c> 之後列不出它們，
/// <c>inserted.</c> 之後退成結構描述的解讀、一個欄位都沒有。
/// </remarks>
public sealed class SqlChangeTablesTests
{
    private const string Trigger = "CREATE TRIGGER dbo.tr_Loan ON dbo.Loan AFTER INSERT, UPDATE AS\n";

    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    private static string[] Listed(string sqlWithCaret, SuggestionKind kind)
    {
        var context = Analyze(sqlWithCaret);

        return SuggestionContextFilter.Filter(context.ScriptSources, context)
            .Where(suggestion => suggestion.Kind == kind)
            .Select(suggestion => suggestion.DisplayText)
            .ToArray();
    }

    /// <summary>限定字之後要查欄位的那張表與補在欄位前面的名稱；不是欄位位置時是 null。</summary>
    private static string? QualifiedTable(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        if (context.Target != CompletionTarget.Column || context.ColumnSources is not { Count: 1 } sources)
        {
            return null;
        }

        return $"{sources[0].Table?.Path} {sources[0].Qualifier}";
    }

    [Theory]
    [InlineData("INSERT INTO dbo.Loan (CopyNo) OUTPUT | VALUES (1)")]
    [InlineData("UPDATE dbo.Loan SET CopyNo = 1 OUTPUT |")]
    [InlineData("UPDATE dbo.Loan SET CopyNo = 1 OUTPUT inserted.CopyNo AS NewCopyNo, |")]
    [InlineData("DELETE dbo.Loan OUTPUT |")]
    [InlineData("MERGE dbo.Loan AS t USING dbo.Copy AS s ON t.CopyNo = s.CopyNo WHEN MATCHED THEN DELETE OUTPUT $action, |")]
    public void OUTPUT子句列出inserted與deleted(string sqlWithCaret)
    {
        Assert.Equal(new[] { "inserted", "deleted" }, Listed(sqlWithCaret, SuggestionKind.Alias).Take(2));
    }

    [Theory]
    [InlineData("INSERT INTO dbo.Loan (CopyNo) OUTPUT inserted.| VALUES (1)", "dbo.Loan inserted")]
    [InlineData("UPDATE TOP (5) dbo.Loan SET CopyNo = 1 OUTPUT deleted.|", "dbo.Loan deleted")]
    [InlineData("UPDATE l SET CopyNo = 1 OUTPUT inserted.| FROM dbo.Loan l", "dbo.Loan inserted")]
    [InlineData("DELETE FROM dbo.Loan OUTPUT deleted.| WHERE ReaderId = 2", "dbo.Loan deleted")]
    [InlineData("DELETE dbo.Loan OUTPUT deleted.CopyNo, COMPRESS(deleted.|", "dbo.Loan deleted")]
    [InlineData("MERGE INTO dbo.Loan AS t USING dbo.Copy AS s ON t.CopyNo = s.CopyNo WHEN MATCHED THEN DELETE OUTPUT deleted.|", "dbo.Loan deleted")]
    [InlineData(Trigger + "UPDATE dbo.Copy SET CopyNo = 1 OUTPUT inserted.|", "dbo.Copy inserted")]
    public void OUTPUT子句裡點號之後是目標的欄位(string sqlWithCaret, string expected)
    {
        Assert.Equal(expected, QualifiedTable(sqlWithCaret));
    }

    [Theory]
    [InlineData(Trigger + "SELECT inserted.| FROM inserted", "dbo.Loan inserted")]
    [InlineData(Trigger + "SELECT deleted.|", "dbo.Loan deleted")]
    [InlineData(Trigger + "SELECT i.| FROM inserted i", "dbo.Loan i")]
    [InlineData(Trigger + "IF EXISTS (SELECT 1 FROM deleted d WHERE d.|)", "dbo.Loan d")]
    [InlineData("ALTER TRIGGER dbo.tr_Loan ON dbo.Loan INSTEAD OF DELETE AS\nDELETE dbo.LoanDetail FROM dbo.LoanDetail JOIN deleted ON deleted.|", "dbo.Loan deleted")]
    public void 觸發程序裡點號之後是父資料表的欄位(string sqlWithCaret, string expected)
    {
        Assert.Equal(expected, QualifiedTable(sqlWithCaret));
    }

    [Fact]
    public void 觸發程序裡沒寫限定字的欄位屬於父資料表()
    {
        var sources = Analyze(Trigger + "SELECT CopyNo FROM inserted WHERE |").ScopeSources;

        Assert.Equal(new[] { "dbo.Loan inserted" }, sources.Select(source => $"{source.Table?.Path} {source.Qualifier}"));
    }

    [Theory]
    [InlineData(Trigger + "SELECT * FROM |")]
    [InlineData(Trigger + "SELECT * FROM dbo.Copy c JOIN |")]
    public void 觸發程序裡FROM之後列出inserted與deleted(string sqlWithCaret)
    {
        Assert.Equal(new[] { "inserted", "deleted" }, Listed(sqlWithCaret, SuggestionKind.ScriptDataSource));
    }

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("CREATE TRIGGER tr_Ddl ON DATABASE FOR CREATE_TABLE AS\nSELECT * FROM |")]
    [InlineData("CREATE TRIGGER tr_Logon ON ALL SERVER FOR LOGON AS\nSELECT * FROM |")]
    [InlineData(Trigger + "SELECT 1\nGO\nSELECT * FROM |")]
    public void 觸發程序之外不列(string sqlWithCaret)
    {
        Assert.Empty(Listed(sqlWithCaret, SuggestionKind.ScriptDataSource));
    }

    [Theory]
    [InlineData("SELECT inserted.| FROM dbo.Loan")]
    [InlineData("UPDATE dbo.Loan SET CopyNo = 1 OUTPUT inserted.CopyNo INTO @t FROM dbo.Copy WHERE deleted.|")]
    public void OUTPUT子句與觸發程序之外不是欄位位置(string sqlWithCaret)
    {
        Assert.Null(QualifiedTable(sqlWithCaret));
    }

    /// <summary>UPDATE 的 OUTPUT 清單逗號之後是運算式，不是 SET 的指派：不能只列目標的資料行。</summary>
    [Fact]
    public void UPDATE的OUTPUT逗號之後不是指派()
    {
        Assert.Null(Analyze("UPDATE dbo.Loan SET CopyNo = 1 OUTPUT inserted.CopyNo, |").ColumnOwner);
    }
}
