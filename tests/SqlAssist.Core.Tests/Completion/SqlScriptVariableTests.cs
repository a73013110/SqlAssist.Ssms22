using System.Linq;
using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// <c>@</c> 之後列出這份指令碼宣告過的變數與參數。
/// </summary>
public sealed class SqlScriptVariableTests
{
    [Theory]
    [InlineData("DECLARE @readerId INT;\r\nSELECT @|")]
    [InlineData("DECLARE @readerId INT;\r\nSET @|")]
    [InlineData("DECLARE @readerId INT;\r\nSELECT * FROM dbo.Loan WHERE ReaderId = @|")]
    [InlineData("DECLARE @readerId INT;\r\nEXEC dbo.usp_Renew @|")]
    [InlineData("DECLARE @readerId INT;\r\nIF @| > 0 RETURN")]
    public void 引用的位置列出宣告過的變數(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.Variable, context.Target);
        Assert.Contains(context.ScriptSources, item => item.DisplayText == "@readerId");
    }

    /// <summary>
    /// 宣告的位置一項都不列。
    /// </summary>
    /// <remarks>
    /// 那是使用者正在取的新名字；清單彈出來的唯一效果是他順手按下 Enter，
    /// 剛打的字被換成別的變數，要按復原才救得回來。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @readerId INT;\r\nDECLARE @|")]
    [InlineData("DECLARE @readerId INT, @|")]
    [InlineData("DECLARE @readerId INT, @loan|")]
    [InlineData("DECLARE @rows TABLE (Id INT), @|")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @|")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @readerId INT, @|")]
    [InlineData("ALTER PROCEDURE dbo.usp_Renew @|")]
    [InlineData("CREATE FUNCTION dbo.fn_Fee (@|")]
    public void 宣告的位置不建議(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.False(SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret).IsValid);
        Assert.False(SqlCompletionContextAnalyzer.Analyze(input.BeforeCaret).IsValid);
    }

    /// <summary>
    /// 程序與函式的參數在自己的主體裡照樣列得出來。
    /// </summary>
    [Fact]
    public void 模組參數在主體裡列得出來()
    {
        var input = SqlWithCaret.Parse(
            "CREATE PROCEDURE dbo.usp_Renew @readerId INT, @days INT\r\nAS\r\nBEGIN\r\n  SELECT @|\r\nEND");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(CompletionTarget.Variable, context.Target);
        Assert.Equal(
            new[] { "@readerId", "@days" },
            context.ScriptSources.Select(item => item.DisplayText).ToArray());
    }

    /// <summary>
    /// 打到一半的那個名字自己不進清單——選它等於什麼都沒做。
    /// </summary>
    [Fact]
    public void 正在輸入的名字不列自己()
    {
        var input = SqlWithCaret.Parse("DECLARE @readerId INT;\r\nSELECT @read|");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(
            new[] { "@readerId" },
            context.ScriptSources.Select(item => item.DisplayText).ToArray());
    }

    /// <summary>游標之後才宣告的變數不列：T-SQL 要求先宣告再使用。</summary>
    [Fact]
    public void 游標之後宣告的不列()
    {
        var input = SqlWithCaret.Parse("SELECT @|\r\nDECLARE @laterOne INT");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Empty(context.ScriptSources);
    }

    /// <summary>全域變數不混進來：那是另一份封閉的清單。</summary>
    [Fact]
    public void 不收兩個小老鼠的名稱()
    {
        var input = SqlWithCaret.Parse("SELECT @@ROWCOUNT;\r\nDECLARE @rows INT;\r\nSELECT @|");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(
            new[] { "@rows" },
            context.ScriptSources.Select(item => item.DisplayText).ToArray());
    }

    [Theory]
    [InlineData("DECLARE @rows INT;\r\nSELECT @|", "INT")]
    [InlineData("DECLARE @name NVARCHAR(50);\r\nSELECT @|", "NVARCHAR")]
    [InlineData("DECLARE @copies TABLE (Id INT);\r\nSELECT @|", "TABLE")]
    [InlineData("CREATE PROCEDURE p @readerId BIGINT AS SELECT @|", "BIGINT")]
    public void 宣告時寫的型別當成說明(string sqlWithCaret, string expected)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(expected, context.ScriptSources[0].Description);
    }

    /// <summary>沒宣告過就沒有清單，不是一份空框停在游標旁邊。</summary>
    [Fact]
    public void 沒有變數時清單是空的()
    {
        var input = SqlWithCaret.Parse("SELECT @|");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Empty(SuggestionMatcher.Filter(context.ScriptSources, context));
    }

    /// <summary>反過來，一般位置的清單裡一個變數都不該有。</summary>
    [Fact]
    public void 一般位置不列變數()
    {
        var variables = SqlScriptVariableSuggestions.Create(
            SqlAssist.Core.Parsing.SqlTokenizer.Tokenize("DECLARE @rows INT "),
            caretPosition: 19,
            new System.Collections.Generic.Dictionary<string, SqlAssist.Core.Parsing.SqlScriptTable>());
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT ro");

        Assert.NotEmpty(variables);
        Assert.Empty(SuggestionMatcher.Filter(variables, context));
    }
}
