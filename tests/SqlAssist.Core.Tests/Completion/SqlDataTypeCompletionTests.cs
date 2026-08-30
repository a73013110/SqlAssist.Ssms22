using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 文法上只接受資料型別的位置。
/// </summary>
public sealed class SqlDataTypeCompletionTests
{
    [Theory]
    [InlineData("DECLARE @rows ")]
    [InlineData("DECLARE @rows INT, @name ")]
    [InlineData("DECLARE @rows AS ")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @readerId ")]
    [InlineData("ALTER PROCEDURE dbo.usp_Renew @readerId INT, @days ")]
    [InlineData("CREATE FUNCTION dbo.fn_Fee (@days ")]
    [InlineData("CREATE FUNCTION dbo.fn_Fee () RETURNS ")]
    [InlineData("SELECT CAST(f.Amount AS ")]
    [InlineData("SELECT TRY_CAST(f.Amount AS ")]
    [InlineData("SELECT PARSE(f.Amount AS ")]
    [InlineData("SELECT CONVERT(")]
    [InlineData("SELECT TRY_CONVERT(")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId ")]
    [InlineData("CREATE TABLE Loan (LoanId ")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT NOT NULL, CopyNo ")]
    [InlineData("DECLARE @copies TABLE (CopyNo ")]
    [InlineData("ALTER TABLE dbo.Loan ALTER COLUMN CopyNo ")]
    public void 型別的位置只建議型別(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.DataType, context.Target);
    }

    /// <summary>
    /// 這些位置長得像但不是；判錯的代價是使用者在那裡什麼都打不出來。
    /// </summary>
    [Theory]
    [InlineData("SELECT f.Amount AS ")]
    [InlineData("SELECT COUNT(f.Amount) AS ")]
    [InlineData("SELECT * FROM (SELECT 1 AS a) AS ")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew AS ")]
    [InlineData("CREATE VIEW v AS ")]
    [InlineData("INSERT INTO dbo.Loan (LoanId ")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT NOT ")]
    [InlineData("SELECT * FROM dbo.Loan WHERE LoanId IN (1, ")]
    [InlineData("SELECT ISNULL(f.Amount, ")]
    [InlineData("DECLARE @rows INT;\r\nSELECT ")]
    public void 不是型別的位置(string textBeforeCaret)
    {
        Assert.NotEqual(
            CompletionTarget.DataType,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);
    }

    /// <summary>
    /// 型別位置的清單裡沒有關鍵字、片段與資料庫物件。
    /// </summary>
    [Fact]
    public void 型別位置排掉其他所有類別()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("DECLARE @rows ");
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty)
            .Concat(SqlDataTypeCatalog.All)
            .Concat(new[]
            {
                new SqlSuggestion("Lib_Reader", "Lib_Reader", "資料表", "資料表", SuggestionKind.Table)
            });

        var filtered = SuggestionMatcher.Filter(candidates, context);

        Assert.NotEmpty(filtered);
        Assert.All(filtered, item => Assert.Equal(SuggestionKind.DataType, item.Kind));
    }

    /// <summary>反過來，一般位置的清單裡一個型別都不該有。</summary>
    [Theory]
    [InlineData("SELECT IN")]
    [InlineData("SELECT * FROM DAT")]
    [InlineData("CREATE ")]
    public void 一般位置不列型別(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Empty(SuggestionMatcher.Filter(SqlDataTypeCatalog.All, context));
    }

    [Theory]
    [InlineData("DECLARE @rows IN", "INT")]
    [InlineData("DECLARE @name NVARCH", "NVARCHAR")]
    [InlineData("SELECT CAST(f.Amount AS DECIM", "DECIMAL")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId BIGIN", "BIGINT")]
    public void 前綴比對排在第一(string textBeforeCaret, string expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var ranked = SuggestionMatcher.Match(SqlDataTypeCatalog.All, context);

        Assert.NotEmpty(ranked);
        Assert.Equal(expected, ranked[0].DisplayText);
    }

    /// <summary>
    /// 幾乎一定要寫長度的型別帶著左括號提交，游標剛好停在引數上。
    /// </summary>
    [Theory]
    [InlineData("NVARCHAR", "NVARCHAR(")]
    [InlineData("VARCHAR", "VARCHAR(")]
    [InlineData("DECIMAL", "DECIMAL(")]
    [InlineData("VARBINARY", "VARBINARY(")]
    [InlineData("INT", "INT")]
    [InlineData("DATETIME2", "DATETIME2")]
    [InlineData("BIT", "BIT")]
    public void 帶引數的型別提交時補左括號(string name, string expected)
    {
        var item = SqlDataTypeCatalog.All.Single(entry => entry.DisplayText == name);

        Assert.Equal(expected, item.InsertionText);
    }

    [Fact]
    public void 全部歸在型別類別()
    {
        Assert.All(
            SqlDataTypeCatalog.All,
            item => Assert.Equal(SuggestionKind.DataType, item.Kind));
    }

    /// <summary>
    /// 已淘汰但仍然運作的型別收下來，只在說明欄寫明替代品。
    /// </summary>
    /// <remarks>
    /// 維護舊結構描述的人本來就要打出它們；藏起來只是讓他自己打。
    /// </remarks>
    [Theory]
    [InlineData("TEXT")]
    [InlineData("NTEXT")]
    [InlineData("IMAGE")]
    [InlineData("TIMESTAMP")]
    public void 已淘汰的型別仍然收錄並標示(string name)
    {
        var item = SqlDataTypeCatalog.All.Single(entry => entry.DisplayText == name);

        Assert.StartsWith("已淘汰", item.Description);
    }
}
