using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// <c>EXEC dbo.usp_Renew @|</c> 認得出他在呼叫誰。
/// </summary>
/// <remarks>
/// 參數名稱只在中繼資料裡，Core 只負責回答這個問題。
/// </remarks>
public sealed class SqlExecutedModuleTests
{
    [Theory]
    [InlineData("EXEC dbo.usp_Renew @", "dbo", "usp_Renew")]
    [InlineData("EXECUTE dbo.usp_Renew @", "dbo", "usp_Renew")]
    [InlineData("EXEC usp_Renew @", null, "usp_Renew")]
    [InlineData("EXEC [dbo].[usp_Renew] @", "dbo", "usp_Renew")]
    [InlineData("EXEC dbo.usp_Renew @readerId = 1, @", "dbo", "usp_Renew")]
    [InlineData("EXEC dbo.usp_Renew @readerId = @rid, @", "dbo", "usp_Renew")]
    [InlineData("EXEC dbo.usp_Renew @days = 7 OUTPUT, @", "dbo", "usp_Renew")]
    [InlineData("EXEC dbo.usp_Renew @days = DEFAULT, @", "dbo", "usp_Renew")]
    [InlineData("DECLARE @rid INT;\r\nEXEC dbo.usp_Renew @", "dbo", "usp_Renew")]
    public void 認得出正在呼叫的模組(string textBeforeCaret, string? schema, string name)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(CompletionTarget.Variable, context.Target);
        Assert.NotNull(context.ExecutedModule);
        Assert.Equal(schema, context.ExecutedModule!.SchemaName);
        Assert.Equal(name, context.ExecutedModule.ObjectName);
    }

    /// <summary>三段式的名稱取後兩段：中繼資料只看得到目前連線的資料庫。</summary>
    [Fact]
    public void 三段式名稱取後兩段()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("EXEC LibraryDb.dbo.usp_Renew @");

        Assert.Equal("dbo", context.ExecutedModule!.SchemaName);
        Assert.Equal("usp_Renew", context.ExecutedModule.ObjectName);
    }

    /// <summary>
    /// 中間夾了別的敘述就不是同一個 EXEC 了。
    /// </summary>
    /// <remarks>
    /// 「往回找最近的 EXEC」會在這裡撈到別人的參數清單。往回跳過的必須全部是
    /// 引數，落點剛好是 EXEC 才算。
    /// </remarks>
    [Theory]
    [InlineData("EXEC dbo.usp_Renew\r\nSELECT * FROM dbo.Loan WHERE ReaderId = @")]
    [InlineData("EXEC dbo.usp_Renew;\r\nDECLARE @rid INT;\r\nSET @")]
    [InlineData("EXEC dbo.usp_Renew\r\nUPDATE dbo.Loan SET ReturnedOn = @")]
    [InlineData("SET @")]
    [InlineData("SELECT * FROM dbo.Loan WHERE ReaderId = @")]
    public void 不在引數清單裡就沒有模組(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Null(context.ExecutedModule);
    }

    /// <summary>讀不出名稱的呼叫方式一律放棄。</summary>
    [Theory]
    [InlineData("EXEC @")]
    [InlineData("EXEC ('SELECT 1'), @")]
    public void 讀不出名稱就放棄(string textBeforeCaret)
    {
        Assert.Null(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).ExecutedModule);
    }

    /// <summary>其他位置不會拖著一個模組參考。</summary>
    [Theory]
    [InlineData("SELECT @@")]
    [InlineData("DECLARE @rows ")]
    [InlineData("SELECT * FROM ")]
    public void 其他目標不帶模組(string textBeforeCaret)
    {
        Assert.Null(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).ExecutedModule);
    }
}
