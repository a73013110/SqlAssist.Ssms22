using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// <c>COLLATE</c> 之後的定序位置。
/// </summary>
/// <remarks>
/// 少了這一份，那個位置落在 <see cref="CompletionTarget.Any"/>：整個資料庫的物件、
/// 一百九十幾個關鍵字與全部片段一起進場，而文法上接得了的只有定序名稱與
/// <c>DATABASE_DEFAULT</c> 這一族。
/// </remarks>
public sealed class SqlCollationCompletionTests
{
    private const string DatabaseCollation = "Chinese_Taiwan_Stroke_CI_AS";

    /// <summary>三種 <c>COLLATE</c> 的位置：運算式之後、資料行定義、資料庫。</summary>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan l JOIN dbo.Copy c ON l.CopyNo = c.CopyNo COLLATE ")]
    [InlineData("WHERE a.InternalCode = c.InternalCode COLLATE Chin")]
    [InlineData("ORDER BY r.ReaderName COLLATE ")]
    [InlineData("CREATE TABLE dbo.Lib_Reader (ReaderName NVARCHAR(50) COLLATE ")]
    [InlineData("ALTER TABLE dbo.Lib_Reader ALTER COLUMN ReaderName NVARCHAR(50) COLLATE ")]
    [InlineData("CREATE DATABASE LibArchive COLLATE ")]
    [InlineData("ALTER DATABASE LibArchive COLLATE ")]
    public void 定序的位置(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.Collation, context.Target);
    }

    /// <summary>名稱打完之後就不是了；否則整句話剩下的位置都只剩定序。</summary>
    [Theory]
    [InlineData("WHERE a.Code = c.Code COLLATE Chinese_Taiwan_Stroke_CI_AS ")]
    [InlineData("SELECT COLLATE")]
    [InlineData("SELECT ")]
    public void 不是定序的位置(string textBeforeCaret)
    {
        Assert.NotEqual(
            CompletionTarget.Collation,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);
    }

    /// <summary>
    /// 反過來，<c>COLLATE</c> 之後不列資料表、關鍵字與片段。
    /// </summary>
    /// <remarks>
    /// 這一條才是整個目標存在的理由。少了它使用者看到的是一份看起來很正常的
    /// 清單，而裡面沒有一項在那個位置是合法的——順手按下 Enter 就寫出一句
    /// 執行不了的 SQL。
    /// </remarks>
    [Fact]
    public void 定序的位置不列別的東西()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[] { Table("Lib_Reader"), Table("Loan") })
            .Concat(SqlCollationCatalog.Defaults)
            .ToArray();

        var filtered = SuggestionMatcher.Filter(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("WHERE a.Code = c.Code COLLATE "));

        Assert.NotEmpty(filtered);
        Assert.All(filtered, item => Assert.Equal(SuggestionKind.Collation, item.Kind));
    }

    /// <summary>定序反過來也不進一般清單：那五千多筆會把真正要找的東西淹掉。</summary>
    [Theory]
    [InlineData("SELECT * FROM Chin")]
    [InlineData("SELECT DAT")]
    public void 一般位置不列定序(string textBeforeCaret)
    {
        var candidates = SqlCollationCatalog.Defaults
            .Concat(new[] { Collation(DatabaseCollation, SuggestionKind.CollationInUse) })
            .ToArray();

        Assert.Empty(SuggestionMatcher.Filter(
            candidates,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret)));
    }

    /// <summary>查不到伺服器的名單時，這個位置仍然有東西可選。</summary>
    [Fact]
    public void 封閉名稱不必問伺服器()
    {
        Assert.Contains(
            SqlCollationCatalog.Defaults,
            item => item.DisplayText == "DATABASE_DEFAULT");
    }

    [Fact]
    public void 指令碼裡出現過的定序會列出來()
    {
        var sql = SqlWithCaret.Parse(
            "SELECT * FROM dbo.Loan l\n" +
            "JOIN dbo.Copy c ON l.CopyNo = c.CopyNo COLLATE " + DatabaseCollation + "\n" +
            "WHERE l.Branch = @branch COLLATE |");

        var context = SqlCompletionContextAnalyzer.Analyze(sql.Text, sql.Caret);

        var source = Assert.Single(context.ScriptSources);
        Assert.Equal(DatabaseCollation, source.DisplayText);
        Assert.Equal(SuggestionKind.CollationInUse, source.Kind);
    }

    /// <summary>同一個名稱只列一次，封閉清單裡已經有的也不再重複。</summary>
    [Fact]
    public void 重複的定序只列一次()
    {
        var tokens = SqlTokenizer.Tokenize(
            "SELECT a COLLATE Latin1_General_CI_AS, b COLLATE LATIN1_GENERAL_CI_AS, " +
            "c COLLATE DATABASE_DEFAULT");

        var source = Assert.Single(SqlScriptCollationSuggestions.Create(tokens));
        Assert.Equal("Latin1_General_CI_AS", source.DisplayText);
    }

    /// <summary>
    /// 實際在用的定序排在伺服器名單之前。
    /// </summary>
    /// <remarks>
    /// 五千多個名稱長得幾乎一樣，模糊比對撈回來的順序沒有意義；名稱長度反而
    /// 會把短的那一個推到前面，而使用者要的是這個資料庫的那一個。
    /// </remarks>
    [Fact]
    public void 實際在用的定序排在前面()
    {
        var candidates = ServerCollations()
            .Concat(new[] { Collation(DatabaseCollation, SuggestionKind.CollationInUse) })
            .ToArray();

        var ranked = SuggestionMatcher.Match(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("WHERE a.Code = c.Code COLLATE "));

        Assert.Equal(DatabaseCollation, ranked[0].DisplayText);
    }

    /// <summary>使用者打了前綴之後仍然如此：同分時實際在用的那一個在前。</summary>
    [Fact]
    public void 有前綴時實際在用的定序仍然在前()
    {
        var candidates = ServerCollations()
            .Concat(new[] { Collation(DatabaseCollation, SuggestionKind.CollationInUse) })
            .ToArray();

        var ranked = SuggestionMatcher.Match(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("WHERE a.Code = c.Code COLLATE Chin"));

        Assert.Equal(DatabaseCollation, ranked[0].DisplayText);
    }

    /// <summary>名單長得幾乎一樣，只差尾巴；排名不能靠長度決定。</summary>
    private static IReadOnlyList<SqlSuggestion> ServerCollations()
    {
        return new[]
        {
            "Chinese_Taiwan_Stroke_CI_AI",
            "Chinese_Taiwan_Stroke_CI_AS",
            "Chinese_Taiwan_Stroke_CS_AS",
            "Latin1_General_CI_AS",
            "SQL_Latin1_General_CP1_CI_AS"
        }.Select(name => Collation(name, SuggestionKind.Collation)).ToArray();
    }

    private static SqlSuggestion Collation(string name, SuggestionKind kind)
    {
        return new SqlSuggestion(name, name, "定序", name, kind);
    }

    private static SqlSuggestion Table(string name)
    {
        return new SqlSuggestion(
            name,
            $"[dbo].[{name}]",
            "Table · dbo",
            $"Table {name}",
            SuggestionKind.Table,
            schemaName: "dbo");
    }
}
