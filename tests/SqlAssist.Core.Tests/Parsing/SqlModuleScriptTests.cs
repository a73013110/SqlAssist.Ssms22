using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlModuleScriptTests
{
    [Fact]
    public void CREATE改寫為ALTER()
    {
        const string definition = "CREATE PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Equal("ALTER PROCEDURE dbo.usp_Test AS SELECT 1", result);
    }

    [Fact]
    public void CREATE_OR_ALTER合併改寫為單一ALTER()
    {
        const string definition = "CREATE OR ALTER PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Equal("ALTER PROCEDURE dbo.usp_Test AS SELECT 1", result);
    }

    [Fact]
    public void 關鍵字之間的多餘空白不影響改寫()
    {
        const string definition = "CREATE   OR\r\n  ALTER  PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Equal("ALTER  PROCEDURE dbo.usp_Test AS SELECT 1", result);
    }

    [Fact]
    public void 大小寫不影響判斷()
    {
        Assert.True(SqlModuleScript.TryConvertCreateToAlter("create proc dbo.x as select 1", out var result));
        Assert.Equal("ALTER proc dbo.x as select 1", result);
    }

    [Fact]
    public void 保留開頭的註解與空白()
    {
        const string definition = "-- 版權宣告\r\n/* 說明 */\r\nCREATE PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Equal("-- 版權宣告\r\n/* 說明 */\r\nALTER PROCEDURE dbo.usp_Test AS SELECT 1", result);
    }

    /// <summary>
    /// T-SQL 的區塊註解可以巢狀，內層的 <c>*/</c> 不是註解的結尾。
    /// </summary>
    /// <remarks>
    /// 自己找第一個 <c>*/</c> 的版本會停在內層的結尾上，接著把「還在註解裡」的
    /// 那段文字當成第一個單字，於是判定「開頭不是 CREATE」而放棄改寫——
    /// 使用者看到的是「這個程序展不開，別的都可以」。
    /// </remarks>
    [Fact]
    public void 略過巢狀的區塊註解()
    {
        const string definition =
            "/* 說明 /* 補充 */ 仍在註解裡 */\r\nCREATE PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Equal(
            "/* 說明 /* 補充 */ 仍在註解裡 */\r\nALTER PROCEDURE dbo.usp_Test AS SELECT 1",
            result);
    }

    /// <summary>
    /// 主體裡的 CREATE 不能被動到：全域取代會把暫存資料表之類的語句一起破壞。
    /// </summary>
    [Fact]
    public void 只改寫開頭的關鍵字()
    {
        const string definition =
            "CREATE PROCEDURE dbo.usp_Test AS\r\nBEGIN\r\n    CREATE TABLE #tmp (Id int);\r\nEND";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Contains("CREATE TABLE #tmp", result);
        Assert.StartsWith("ALTER PROCEDURE", result);
    }

    [Fact]
    public void 已經是ALTER時維持原樣()
    {
        const string definition = "ALTER PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Equal(definition, result);
    }

    [Fact]
    public void 改寫函式與檢視()
    {
        Assert.True(SqlModuleScript.TryConvertCreateToAlter(
            "CREATE FUNCTION dbo.fn_Test() RETURNS int AS BEGIN RETURN 1 END", out var function));
        Assert.StartsWith("ALTER FUNCTION", function);

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(
            "CREATE VIEW dbo.v_Test AS SELECT 1 AS X", out var view));
        Assert.StartsWith("ALTER VIEW", view);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SELECT 1")]
    [InlineData("-- 只有註解")]
    public void 不是CREATE開頭時回傳false(string definition)
    {
        Assert.False(SqlModuleScript.TryConvertCreateToAlter(definition, out var result));
        Assert.Equal(definition, result);
    }
}
