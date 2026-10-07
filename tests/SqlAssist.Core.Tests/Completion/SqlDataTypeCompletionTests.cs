using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Settings;
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
    [InlineData("SELECT JSON_VALUE(@doc, '$.Fee' RETURNING ")]
    [InlineData("SELECT CONVERT(")]
    [InlineData("SELECT TRY_CONVERT(")]
    [InlineData("SELECT IDENTITY(")]
    [InlineData("SELECT RowNo = IDENTITY(")]
    [InlineData("SELECT CopyNo, IDENTITY(")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT, DueDate DATETIME2 DEFAULT CONVERT (")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT, DueText AS CONVERT(")]
    [InlineData("ALTER TABLE dbo.Loan ADD DueDate DATETIME2 NOT NULL DEFAULT TRY_CONVERT(")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId ")]
    [InlineData("CREATE TABLE Loan (LoanId ")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT NOT NULL, CopyNo ")]
    [InlineData("DECLARE @copies TABLE (CopyNo ")]
    [InlineData("ALTER TABLE dbo.Loan ALTER COLUMN CopyNo ")]
    [InlineData("ALTER TABLE [dbo].[Loan] ALTER COLUMN [CopyNo] ")]
    [InlineData("ALTER TABLE dbo.Loan ADD ReaderId ")]
    [InlineData("ALTER TABLE dbo.Loan ADD ReaderId INT NULL, CopyNo ")]
    [InlineData("CREATE TABLE #Loan (CopyNo ")]
    [InlineData("CREATE TYPE dbo.CopyList AS TABLE (CopyNo ")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @readerId INT = NULL, @days ")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @readerId INT OUTPUT, @days ")]
    [InlineData("CREATE PROC dbo.usp_Renew @readerId AS INT, @days ")]
    [InlineData("DECLARE @rows INT = NULL, @name ")]
    [InlineData("DECLARE @copies TABLE (CopyNo INT), @name ")]
    [InlineData("CREATE SEQUENCE dbo.LoanSeq AS ")]
    [InlineData("CREATE TYPE dbo.Code FROM ")]
    [InlineData("CREATE PARTITION FUNCTION LibRange (")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS ((Branch ")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS ((Branch varchar(10), CopyCount ")]
    [InlineData("EXEC dbo.usp_Copies WITH RECOMPILE, RESULT SETS ((Branch int), (CopyNo ")]
    [InlineData("SELECT * FROM OPENJSON(@doc, '$') WITH (CopyNo ")]
    [InlineData("SELECT * FROM OPENJSON(@doc, '$') WITH (CopyNo int, Branch ")]
    [InlineData("SELECT * FROM dbo.Loan l CROSS APPLY OPENJSON(l.Doc) WITH (CopyNo ")]
    [InlineData("SELECT * FROM OPENXML(@handle, '/r') WITH (CopyNo int, Branch ")]
    [InlineData("CREATE EXTERNAL TABLE dbo.LoanFile (LoanId ")]
    [InlineData("CREATE EXTERNAL TABLE dbo.LoanFile (LoanId int, CopyNo ")]
    [InlineData("CREATE TABLE LibArchive..Loan (LoanId ")]
    [InlineData("CREATE TABLE LibArchive..Loan (LoanId int, CopyNo ")]
    [InlineData("CREATE AGGREGATE dbo.LoanConcat (@doc ")]
    [InlineData("CREATE AGGREGATE dbo.LoanConcat (@doc xml(LibSchemaCollection), @copyNo ")]
    [InlineData("CREATE FUNCTION dbo.fn_Copies () RETURNS @t ")]
    [InlineData("CREATE TABLE [dbo].[Loan]([LoanId] ")]
    [InlineData("CREATE TABLE ..Loan (LoanId ")]
    [InlineData("CREATE TABLE .dbo.Loan (LoanId ")]
    [InlineData("INSERT BULK dbo.Loan (LoanId int, CopyNo ")]
    [InlineData("CREATE FUNCTION dbo.fn_Copies () RETURNS TABLE (CopyNo ")]
    [InlineData("CREATE TABLE [dbo].[Loan]([LoanId] [int] NOT NULL, [Key] ")]
    [InlineData("CREATE SELECTIVE XML INDEX sx ON dbo.Copy (Note) FOR (BookTitle = '/book/title' AS SQL ")]
    [InlineData("CREATE SELECTIVE XML INDEX sx ON dbo.Copy (Note) FOR (BookTitle = '/a' AS XQUERY 'xs:string', BookYear = '/b' AS SQL ")]
    [InlineData("ALTER INDEX sx ON dbo.Copy FOR (ADD BookTitle = '/book/title' AS SQL ")]
    public void 型別的位置只建議型別(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(SqlCompletionSlot.Grammar, context.Slot);
        Assert.Equal(CompletionTarget.DataType, context.Target);
    }

    /// <summary>
    /// 這些位置長得像但不是；判錯的代價是使用者在那裡什麼都打不出來。
    /// </summary>
    [Theory]
    [InlineData("SELECT f.Amount AS ")]
    [InlineData("SELECT f.Amount AS SQL ")]
    [InlineData("SELECT * FROM (SELECT f.Amount AS SQL ")]
    [InlineData("SELECT COUNT(f.Amount) AS ")]
    [InlineData("SELECT * FROM (SELECT 1 AS a) AS ")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew AS ")]
    [InlineData("CREATE VIEW v AS ")]
    [InlineData("INSERT INTO dbo.Loan (LoanId ")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT NOT ")]
    [InlineData("SELECT * FROM dbo.Loan WHERE LoanId IN (1, ")]
    [InlineData("SELECT ISNULL(f.Amount, ")]
    [InlineData("DECLARE @rows INT;\r\nSELECT ")]
    [InlineData("ALTER TABLE dbo.Loan DROP COLUMN CopyNo ")]
    [InlineData("ALTER TABLE dbo.Loan DROP COLUMN [CopyNo] ")]
    [InlineData("ALTER TABLE dbo.Loan ADD CONSTRAINT ")]
    [InlineData("SELECT @rows = 1, @name ")]
    [InlineData("EXEC dbo.usp_Renew @readerId = NULL, @days ")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @readerId INT AS SELECT @readerId ")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS ((Branch int ")]
    [InlineData("SELECT * FROM dbo.fn_Copies(1) WITH (Branch ")]
    [InlineData("SELECT * FROM dbo.Loan WITH (Branch ")]
    [InlineData("CREATE FUNCTION dbo.fn_Copies (")]
    [InlineData("ALTER PARTITION FUNCTION LibRange () SPLIT RANGE (")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT IDENTITY(")]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo INT, LoanId DECIMAL(10, 0) NOT NULL IDENTITY(")]
    [InlineData("ALTER TABLE dbo.Loan ADD LoanId INT IDENTITY(")]
    [InlineData("DECLARE @copies TABLE (RowNo INT IDENTITY(")]
    [InlineData("SELECT JSON_OBJECT('Fee': f.Amount RETURNING ")]
    [InlineData("SELECT JSON_VALUE(@doc, '$.Fee' RETURNING int, ")]
    public void 不是型別的位置(string textBeforeCaret)
    {
        Assert.NotEqual(
            CompletionTarget.DataType,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);
    }

    /// <summary>
    /// 型別位置的清單裡沒有關鍵字、片段與資料庫物件。
    /// </summary>
    /// <remarks>宣告那一格另有可省的 <c>AS</c>，見 <see cref="型別前面的AS列得出來"/>。</remarks>
    [Fact]
    public void 型別位置排掉其他所有類別()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT CAST(f.Amount AS ");
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty)
            .Concat(SqlDataTypeCatalog.All)
            .Concat(new[]
            {
                new SqlSuggestion("Lib_Reader", "Lib_Reader", "資料表", "資料表", SuggestionKind.Table)
            });

        var filtered = SuggestionContextFilter.Filter(candidates, context);

        Assert.NotEmpty(filtered);
        Assert.All(filtered, item => Assert.Equal(SuggestionKind.DataType, item.Kind));
    }

    /// <summary>
    /// 型別那一組的 <c>AS</c> 列得出來：宣告的可省、資料行可改寫成計算資料行，<c>CAST</c> 的不在選取清單時沒有別名的 AS 可借。
    /// </summary>
    [Theory]
    [InlineData("DECLARE @rows |")]
    [InlineData("DECLARE @rows INT, @name |")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @readerId |")]
    [InlineData("SET @rows = CAST (@name |")]
    [InlineData("SET @rows = CAST(@name |")]
    [InlineData("SET @rows = TRY_CAST(@name |")]
    [InlineData("SET @rows = PARSE(@name |")]
    [InlineData("SET @rows = CAST(ISNULL(@name, 0) |")]
    [InlineData("SET @rows = CAST(CASE WHEN @name = 1 THEN 1 END |")]
    [InlineData("SELECT CAST(f.Amount |")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT, Total |")]
    [InlineData("DECLARE @copies TABLE (CopyNo INT, Total |")]
    [InlineData("ALTER TABLE dbo.Loan ADD Total |")]
    public async Task 型別前面的AS列得出來(string sqlWithCaret)
    {
        var list = await GetAsync(sqlWithCaret);

        Assert.Contains(list, item => item.Kind == SuggestionKind.Keyword && item.DisplayText == "AS");
    }

    /// <summary>
    /// 不寫型別的 <c>timestamp</c> 是 rowversion 資料行：名稱之後型別與資料行條件約束都接得上。
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE dbo.Loan (timestamp |")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT PRIMARY KEY, timestamp |")]
    [InlineData("ALTER TABLE dbo.Loan ADD timestamp |")]
    public async Task 不寫型別的timestamp也列資料行條件約束(string sqlWithCaret)
    {
        var list = await GetAsync(sqlWithCaret);

        Assert.Contains(list, item => item.Kind == SuggestionKind.DataType && item.DisplayText == "INT");
        Assert.Contains(list, item => item.Kind == SuggestionKind.Keyword && item.DisplayText == "NOT");
        Assert.Contains(list, item => item.Kind == SuggestionKind.Keyword && item.DisplayText.StartsWith("PRIMARY", StringComparison.Ordinal));
        Assert.Contains(list, item => item.Kind == SuggestionKind.Keyword && item.DisplayText == "NULL");
    }

    /// <summary>其餘名稱之後只接型別（與計算資料行的 AS）：資料行定義的尾端不打散型別那一格。</summary>
    [Theory]
    [InlineData("CREATE TABLE dbo.Loan (LoanId |")]
    [InlineData("CREATE TABLE dbo.Loan ([timestamp] |")]
    public async Task 一般資料行名稱之後不列資料行條件約束(string sqlWithCaret)
    {
        var list = await GetAsync(sqlWithCaret);

        Assert.Contains(list, item => item.Kind == SuggestionKind.DataType && item.DisplayText == "INT");
        Assert.DoesNotContain(list, item => item.Kind == SuggestionKind.Keyword && item.DisplayText == "NOT");
        Assert.DoesNotContain(list, item => item.Kind == SuggestionKind.Keyword && item.DisplayText == "NULL");
    }

    /// <summary>運算元還沒寫完、型別已經寫了，或那個函式不接型別時不另外放行 AS。</summary>
    /// <remarks>判不出位置（<c>Any</c>）的格子本來就列整份關鍵字，所以問的是旗標而不是清單。</remarks>
    [Theory]
    [InlineData("SET @rows = CAST(|")]
    [InlineData("SET @rows = CAST(@name + |")]
    [InlineData("SET @rows = CAST(@name AS INT |")]
    [InlineData("SET @rows = CAST(@name AS NVARCHAR(10) |")]
    [InlineData("SET @rows = ISNULL(@name |")]
    [InlineData("SET @rows = @name |")]
    [InlineData("DECLARE @rows INT = @name |")]
    [InlineData("DECLARE @rows AS |")]
    [InlineData("DECLARE @rows INT |")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS ((Branch |")]
    [InlineData("ALTER TABLE dbo.Loan ALTER COLUMN CopyNo |")]
    [InlineData("SELECT a, b |")]
    public void 不接型別的地方不列AS(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.False(SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret).AcceptsTypeAs);
    }

    /// <summary>反過來，一般位置的清單裡一個型別都不該有。</summary>
    [Theory]
    [InlineData("SELECT IN")]
    [InlineData("SELECT * FROM DAT")]
    [InlineData("CREATE ")]
    public void 一般位置不列型別(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Empty(SuggestionContextFilter.Filter(SqlDataTypeCatalog.All, context));
    }

    [Theory]
    [InlineData("DECLARE @rows IN", "INT")]
    [InlineData("DECLARE @name NVARCH", "NVARCHAR")]
    [InlineData("SELECT CAST(f.Amount AS DECIM", "DECIMAL")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId BIGIN", "BIGINT")]
    [InlineData("DECLARE @v VECT", "VECTOR")]
    [InlineData("DECLARE @doc AS JSO", "JSON")]
    public void 前綴比對排在第一(string textBeforeCaret, string expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var ranked = SuggestionListProbe.Match(SqlDataTypeCatalog.All, context);

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
    [InlineData("VECTOR", "VECTOR(")]
    [InlineData("JSON", "JSON")]
    [InlineData("INT", "INT")]
    [InlineData("DATETIME2", "DATETIME2")]
    [InlineData("BIT", "BIT")]
    public void 帶引數的型別提交時補左括號(string name, string expected)
    {
        var item = SqlDataTypeCatalog.All.Single(entry => entry.DisplayText == name);

        Assert.Equal(expected, item.InsertionText);
    }

    /// <summary>
    /// SQL Server 2025 的新型別在每一種型別位置都列得出來；清單不看連線的版本。
    /// </summary>
    [Theory]
    [InlineData("DECLARE @v |")]
    [InlineData("DECLARE @v AS |")]
    [InlineData("DECLARE @rows INT, @v |")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @v |")]
    [InlineData("SET @doc = CAST(@text AS |")]
    [InlineData("SELECT TRY_CONVERT(|")]
    [InlineData("CREATE TABLE dbo.Loan (LoanId INT, Embedding |")]
    [InlineData("ALTER TABLE dbo.Loan ADD Doc |")]
    [InlineData("ALTER TABLE dbo.Loan ALTER COLUMN Doc |")]
    [InlineData("DECLARE @copies TABLE (CopyNo INT, Doc |")]
    public async Task 新版型別在型別位置列得出來(string sqlWithCaret)
    {
        var list = await GetAsync(sqlWithCaret);

        Assert.Contains(list, item => item.Kind == SuggestionKind.DataType && item.DisplayText == "VECTOR");
        Assert.Contains(list, item => item.Kind == SuggestionKind.DataType && item.DisplayText == "JSON");
    }

    /// <summary>多字的 ANSI 寫法在型別位置列得出來，說明取它代表的型別。</summary>
    [Theory]
    [InlineData("NATIONAL CHARACTER VARYING", "NVARCHAR")]
    [InlineData("NATIONAL CHAR", "NCHAR")]
    [InlineData("CHARACTER VARYING", "VARCHAR")]
    [InlineData("DOUBLE PRECISION", "FLOAT")]
    public void 多字的ANSI寫法是型別的同義字(string synonym, string type)
    {
        Assert.Contains(SqlDataTypeCatalog.All, item => item.DisplayText == synonym);
        Assert.True(SqlDataTypeCatalog.TryGetDescription(synonym, out var description));
        Assert.True(SqlDataTypeCatalog.TryGetDescription(type, out var expected));
        Assert.Equal(expected, description);
    }

    [Fact]
    public void 新版型別的說明寫明版本且有英文()
    {
        Assert.True(SqlDataTypeCatalog.TryGetDescription("vector", out var vector));
        Assert.Contains("SQL Server 2025", vector);

        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            Assert.True(SqlDataTypeCatalog.TryGetDescription("json", out var json));
            Assert.Equal("Native JSON object or array (SQL Server 2025)", json);
        }
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
