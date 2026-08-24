using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SqlCompletionContextAnalyzerTests
{
    [Theory]
    [InlineData("SELECT * FROM ", CompletionTarget.DataSource)]
    [InlineData("SELECT * FROM Orders INNER JOIN ", CompletionTarget.DataSource)]
    [InlineData("UPDATE ", CompletionTarget.DataSource)]
    [InlineData("INSERT INTO ", CompletionTarget.DataSource)]
    [InlineData("ALTER PROCEDURE ", CompletionTarget.Procedure)]
    [InlineData("ALTER FUNCTION ", CompletionTarget.Function)]
    public void 依前導關鍵字決定建議目標(string textBeforeCaret, CompletionTarget expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(expected, context.Target);
    }

    /// <summary>
    /// 沒有輸入前綴、也沒有可據以縮小範圍的前導關鍵字時不主動跳出清單，
    /// 否則按下空白鍵就會列出整個資料庫。
    /// </summary>
    [Theory]
    [InlineData("SELECT ")]
    [InlineData("WHERE ")]
    [InlineData("  ")]
    public void 既無前綴也無目標時不建議(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.False(context.IsValid);
        Assert.Equal(CompletionTarget.Any, context.Target);
    }

    [Theory]
    [InlineData("ALTER PROCEDURE ", 0, "ALTER")]
    [InlineData("\r\nALTER PROCEDURE usp", 2, "ALTER")]
    [InlineData("SELECT * FROM ", 9, "FROM")]
    [InlineData("SELECT * FROM Orders INNER JOIN pub", 27, "JOIN")]
    public void 回報決定目標的關鍵字起點(string textBeforeCaret, int expectedStart, string expectedKeyword)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(expectedStart, context.TargetKeywordStart);
        Assert.Equal(
            expectedKeyword,
            textBeforeCaret.Substring(expectedStart, expectedKeyword.Length));
    }

    [Theory]
    [InlineData("EXEC ")]
    [InlineData("EXECUTE ")]
    [InlineData("exec usp")]
    public void EXEC之後只建議Procedure(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.Procedure, context.Target);
    }

    /// <summary>
    /// ALTER PROCEDURE 與 EXEC 後方都只顯示預存程序，但提交行為必須不同：
    /// 前者要放進完整定義，後者只補上名稱。
    /// </summary>
    [Theory]
    [InlineData("ALTER PROCEDURE ", CompletionIntent.AlterDefinition)]
    [InlineData("ALTER FUNCTION ", CompletionIntent.AlterDefinition)]
    [InlineData("EXEC ", CompletionIntent.Reference)]
    [InlineData("SELECT * FROM ", CompletionIntent.Reference)]
    [InlineData("SELECT pub", CompletionIntent.Reference)]
    public void 區分提交意圖(string textBeforeCaret, CompletionIntent expected)
    {
        Assert.Equal(expected, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Intent);
    }

    [Fact]
    public void 沒有目標關鍵字時起點為負一()
    {
        Assert.Equal(-1, SqlCompletionContextAnalyzer.Analyze("SELECT pub").TargetKeywordStart);
    }

    [Fact]
    public void 取出目前輸入的前綴()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr");

        Assert.Equal("libr", context.Prefix);
        Assert.Equal(14, context.TokenStart);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.", "dbo")]
    [InlineData("SELECT * FROM [dbo].", "dbo")]
    [InlineData("SELECT * FROM [sales].", "sales")]
    public void 解析Schema限定字(string textBeforeCaret, string expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(expected, context.Qualifier);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    [Theory]
    [InlineData("-- 註解 publ")]
    [InlineData("/* publ")]
    [InlineData("SELECT 'publ")]
    [InlineData("SELECT \"publ")]
    [InlineData("SELECT [publ")]
    public void 字串與註解內不建議(string textBeforeCaret)
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).IsValid);
    }

    [Fact]
    public void 字串結束後恢復建議()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT 'a' FROM publ");

        Assert.True(context.IsValid);
        Assert.Equal("publ", context.Prefix);
    }

    [Fact]
    public void 空白輸入不建議()
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(string.Empty).IsValid);
    }
}
