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
    [InlineData("DECLARE @readerId INT;\r\nDECLARE @copyNo INT = @|")]
    [InlineData("DECLARE @readerId INT;\r\nDECLARE @copyNo INT = 1, @loanId INT = COALESCE(@readerId, @|")]
    public void 引用的位置列出宣告過的變數(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(SqlCompletionSlot.Grammar, context.Slot);
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
    [InlineData("CREATE FUNCTION dbo.fn_Fee (@readerId INT = 1, @|")]
    [InlineData("CREATE FUNCTION dbo.fn_Fee (@readerId INT) RETURNS @|")]
    [InlineData("DECLARE @readerId INT = @copyNo, @|")]
    public void 宣告的位置不建議(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.Equal(
            SqlCompletionSlot.Name,
            SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret).Slot);

        var context = SqlCompletionContextAnalyzer.Analyze(input.BeforeCaret);

        Assert.Equal(SqlCompletionSlot.Name, context.Slot);
        Assert.False(SqlCompletionPolicy.Participates(context, triggerAfterCharacters: 1));
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

        Assert.Empty(SuggestionContextFilter.Filter(context.ScriptSources, context));
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
        Assert.Empty(SuggestionContextFilter.Filter(variables, context));
    }

    /// <summary>
    /// SET 的選項清單在空前綴就開好，變數要在同一份裡：打 <c>@</c> 只是篩選它。
    /// </summary>
    /// <remarks>
    /// 前一格判得出位置時（<c>;</c>、<c>)</c>、<c>BEGIN</c>）片語確定、清單封閉；判不出來時清單不預開，
    /// 打 <c>@</c> 才走變數那一條。兩邊要列出同一份。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @readerId INT;\r\nSET |")]
    [InlineData("DECLARE @readerId INT\r\nIF (1 = 1) SET |")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @readerId INT AS\r\nBEGIN\r\nSET |")]
    [InlineData("DECLARE @readerId INT;\r\nSET ROWCOUNT |")]
    [InlineData("DECLARE @readerId INT;\r\nFETCH FROM c INTO |")]
    [InlineData("DECLARE @readerId UNIQUEIDENTIFIER;\r\nBEGIN DIALOG |")]
    [InlineData("DECLARE @readerId UNIQUEIDENTIFIER;\r\nBEGIN DIALOG CONVERSATION |")]
    [InlineData("DECLARE @readerId UNIQUEIDENTIFIER;\r\nGET CONVERSATION GROUP |")]
    [InlineData("DECLARE @readerId UNIQUEIDENTIFIER;\r\nWAITFOR (GET CONVERSATION GROUP |")]
    public void 收變數的封閉片語連變數一起列(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(CompletionTarget.ClauseKeyword, context.Target);
        Assert.Contains(
            SuggestionContextFilter.Filter(context.ScriptSources, context),
            item => item.DisplayText == "@readerId");
    }

    /// <summary>
    /// 清單因為目標封閉而在空前綴開好時（<c>EXEC </c> 列程序），片語那一格收變數就一起列：
    /// <c>EXEC @proc</c> 執行變數裡的模組名稱，<c>EXEC @ret = p</c> 接回傳值。
    /// </summary>
    /// <remarks>只看片語封不封閉的話，打 <c>@</c> 只篩選那一份程序，一個變數都沒有。</remarks>
    [Theory]
    [InlineData("DECLARE @readerId NVARCHAR(100);\r\nEXEC |")]
    [InlineData("DECLARE @readerId NVARCHAR(100);\r\nEXECUTE |")]
    public void 目標封閉的清單也放片語收的變數(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(CompletionTarget.Procedure, context.Target);
        Assert.True(SqlCompletionPolicy.IsClosed(context));
        Assert.Contains(
            SuggestionContextFilter.Filter(context.ScriptSources, context),
            item => item.DisplayText == "@readerId");
    }

    /// <summary>
    /// 帶方向字的 FETCH 沒有片語接住，INTO 之後照它所屬的動詞列變數，不當成 SELECT … INTO 的資料表。
    /// </summary>
    [Theory]
    [InlineData("DECLARE @readerId INT;\r\nFETCH NEXT FROM c INTO |")]
    [InlineData("DECLARE @readerId INT;\r\nFETCH PRIOR FROM GLOBAL c INTO |")]
    [InlineData("DECLARE @readerId INT;\r\nFETCH ABSOLUTE 1 FROM c INTO |")]
    [InlineData("DECLARE @readerId INT;\r\nFETCH RELATIVE @readerId FROM c INTO |")]
    public void FETCH的INTO之後列變數(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(CompletionTarget.Variable, context.Target);
        Assert.Contains(
            SuggestionContextFilter.Filter(context.ScriptSources, context),
            item => item.DisplayText == "@readerId");
    }

    /// <summary>
    /// FETCH 的 INTO 之後打 <c>@</c> 是純量位置：資料表變數不照資料來源提交。
    /// </summary>
    [Fact]
    public void FETCH的INTO之後的小老鼠是純量位置()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("FETCH NEXT FROM c INTO @");

        Assert.Equal(CompletionTarget.Variable, context.Target);
        Assert.True(context.ExpectsScalar);
    }

    /// <summary>
    /// 封閉片語那一格接不了變數，或變數在那裡是新取的名字時不列。
    /// </summary>
    /// <remarks>
    /// <c>CREATE PROCEDURE p </c> 之後剖析器也收 <c>@a</c>，但那是參數的宣告，與打 <c>@</c> 時同一條判斷。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @readerId INT;\r\nSET NOCOUNT |")]
    [InlineData("DECLARE @readerId INT;\r\nCREATE PROCEDURE dbo.usp_Renew |")]
    public void 不收變數的封閉片語不列變數(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.Equal(CompletionTarget.ClauseKeyword, context.Target);
        Assert.Empty(context.ScriptSources);
    }
}
