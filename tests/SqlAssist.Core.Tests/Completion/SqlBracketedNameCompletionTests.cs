using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 使用者自己打了左方括號：清單照樣列得出來，提交的名稱保留方括號。
/// </summary>
/// <remarks>
/// 以前左方括號之後一律當成字串那一類，清單整個不開——以位址命名的連結伺服器
/// （<c>[192.0.2.10]</c>）只能打方括號，於是剛好是最需要清單的那一種完全沒有。
/// </remarks>
public sealed class SqlBracketedNameCompletionTests
{
    private static readonly SqlAssistSettings Qualified =
        new() { QualifyObjectNames = true, UseSquareBrackets = false };

    private static readonly SqlAssistSettings Unqualified =
        new() { QualifyObjectNames = false, UseSquareBrackets = false };

    private static readonly SqlSuggestion Loan =
        new("Loan", "Loan", "Table", "Loan", SuggestionKind.Table, schemaName: "dbo");

    private static readonly SqlSuggestion Archive =
        new("LibArchive", "LibArchive", "Database", "USE LibArchive", SuggestionKind.Database);

    private static readonly SqlSuggestion Mirror =
        new("192.0.2.10", "[192.0.2.10]", "Linked server", "192.0.2.10", SuggestionKind.LinkedServer);

    private static readonly SqlSuggestion Select =
        new("SELECT", "SELECT", "Keyword", "SELECT", SuggestionKind.Keyword);

    private static readonly SqlSuggestion Count =
        new("COUNT", "COUNT(", "Function", "COUNT", SuggestionKind.BuiltInFunction);

    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    [Theory]
    [InlineData("SELECT * FROM [19|", "19")]
    [InlineData("SELECT * FROM [|", "")]
    [InlineData("SELECT * FROM [Lib Reader|", "Lib Reader")]
    [InlineData("SELECT * FROM [a]]b|", "a]b")]

    // 方括號裡的左方括號只是名稱的一個字元，起點仍是第一個。
    [InlineData("SELECT * FROM [a[b|", "a[b")]
    public void 左方括號之後照樣是名稱(string sqlWithCaret, string prefix)
    {
        var context = Analyze(sqlWithCaret);

        Assert.True(context.Bracketed);
        Assert.Equal(SqlCompletionSlot.Grammar, context.Slot);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
        Assert.Equal(prefix, context.Prefix);
        Assert.Equal(sqlWithCaret.IndexOf('['), context.TokenStart);
    }

    [Fact]
    public void 限定字之後的方括號沿用限定字()
    {
        var context = Analyze("SELECT * FROM [192.0.2.10].[Lib|");

        Assert.True(context.Bracketed);
        Assert.Equal("Lib", context.Prefix);
        Assert.Equal("192.0.2.10", context.Qualifier);
    }

    /// <summary>忘了關的左方括號不能讓底下整份指令碼都變成一個名稱。</summary>
    [Fact]
    public void 跨行或超過長度上限的方括號不當成正在打的名稱()
    {
        Assert.Equal(SqlCompletionSlot.Inert, Analyze("SELECT [Loan\r\nFROM Lo|").Slot);
        Assert.Equal(
            SqlCompletionSlot.Inert,
            Analyze("SELECT * FROM [" + new string('a', SqlIdentifier.MaximumLength + 1) + "|").Slot);
    }

    [Fact]
    public void 關上的方括號之後不是方括號名稱()
    {
        Assert.False(Analyze("SELECT * FROM [Loan] |").Bracketed);
    }

    /// <summary>左方括號與限定字一樣把範圍講完了，空前綴也開清單。</summary>
    [Fact]
    public void 只打左方括號就開清單()
    {
        Assert.True(SqlCompletionPolicy.Participates(Analyze("SELECT [|"), triggerAfterCharacters: 3));
    }

    [Fact]
    public void 方括號裡只列包得起來的名稱()
    {
        var names = SuggestionContextFilter
            .Filter(new[] { Loan, Select, Count }, Analyze("SELECT * FROM [|"))
            .Select(item => item.DisplayText);

        Assert.Equal(new[] { "Loan" }, names);
    }

    /// <summary>程式補的限定字照設定，使用者打了方括號的那一段一定包。</summary>
    [Theory]
    [InlineData("SELECT * FROM [Lo|", true, "dbo.[Loan]")]
    [InlineData("SELECT * FROM [Lo|", false, "[Loan]")]
    [InlineData("SELECT * FROM dbo.[Lo|", true, "[Loan]")]
    public void 提交的名稱保留方括號(string sqlWithCaret, bool qualify, string expected)
    {
        Assert.Equal(
            expected,
            SqlInsertionText.Build(Loan, Analyze(sqlWithCaret), qualify ? Qualified : Unqualified));
    }

    [Fact]
    public void 不包也合法的資料庫名稱只在打了方括號時才包()
    {
        Assert.Equal("LibArchive", SqlInsertionText.Build(Archive, Analyze("BACKUP DATABASE |"), Qualified));
        Assert.Equal("[LibArchive]", SqlInsertionText.Build(Archive, Analyze("BACKUP DATABASE [Lib|"), Qualified));
    }

    [Fact]
    public void 本來就要包的名稱不重複包()
    {
        Assert.Equal("[192.0.2.10]", SqlInsertionText.Build(Mirror, Analyze("SELECT * FROM [19|"), Qualified));
    }

    [Fact]
    public void 欄位的限定字照原樣只包欄位名稱()
    {
        var column = new SqlSuggestion("CopyNo", "c.CopyNo", "int", "CopyNo", SuggestionKind.Column);

        Assert.Equal("c.[CopyNo]", SqlInsertionText.Build(column, Analyze("SELECT [Co|"), Qualified));
    }

    /// <summary>自己打了右方括號就是打完了：比對落空，清單跟著關。</summary>
    [Theory]
    [InlineData("[Lib", "Lib")]
    [InlineData("[", "")]
    [InlineData("[a]]b", "a]b")]
    [InlineData("[Lib]", "Lib]")]
    [InlineData("Lib", "Lib")]
    public void 篩選字拿掉左方括號(string applicableText, string expected)
    {
        Assert.Equal(expected, SuggestionList.TypedText(applicableText, fieldDefault: null));
    }
}
