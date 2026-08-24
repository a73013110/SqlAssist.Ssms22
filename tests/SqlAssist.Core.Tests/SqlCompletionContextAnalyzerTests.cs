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
        Assert.Equal(expected, context.SchemaQualifier);
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
