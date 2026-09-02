using System;
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

    /// <summary>
    /// 展開之後游標停在名稱之後：停在結尾的話一展開就被捲到定義的最後一行。
    /// </summary>
    [Theory]
    [InlineData("ALTER PROCEDURE dbo.usp_Test AS SELECT 1", "ALTER PROCEDURE dbo.usp_Test")]
    [InlineData("ALTER PROC dbo.usp_Test AS SELECT 1", "ALTER PROC dbo.usp_Test")]
    [InlineData("ALTER FUNCTION dbo.fn_Test() RETURNS int AS BEGIN RETURN 1 END", "ALTER FUNCTION dbo.fn_Test")]
    [InlineData("ALTER TRIGGER dbo.tr_Loan ON dbo.Loan AFTER INSERT AS SELECT 1", "ALTER TRIGGER dbo.tr_Loan")]
    [InlineData("ALTER VIEW dbo.v_Test AS SELECT 1 AS X", "ALTER VIEW dbo.v_Test")]
    [InlineData("ALTER PROCEDURE [dbo].[usp_Test] AS SELECT 1", "ALTER PROCEDURE [dbo].[usp_Test]")]
    [InlineData("ALTER PROCEDURE usp_Test AS SELECT 1", "ALTER PROCEDURE usp_Test")]
    [InlineData("alter procedure dbo.usp_Test as select 1", "alter procedure dbo.usp_Test")]
    [InlineData("ALTER PROCEDURE dbo . usp_Test AS SELECT 1", "ALTER PROCEDURE dbo . usp_Test")]
    [InlineData("ALTER PROCEDURE\r\n    dbo.usp_Test\r\n    @ReaderId int\r\nAS\r\nSELECT 1", "ALTER PROCEDURE\r\n    dbo.usp_Test")]
    public void 名稱結束的位置(string script, string expectedPrefix)
    {
        Assert.Equal(expectedPrefix.Length, SqlModuleScript.FindHeaderNameEnd(script));
    }

    /// <summary>
    /// 標頭在改寫之後才算得準：<c>CREATE OR ALTER</c> 併成一個 <c>ALTER</c>，
    /// 後面每一個字元都往前位移，在原始定義上算出來的位置會落在名稱中間。
    /// </summary>
    [Fact]
    public void 改寫之後的位置對得上名稱()
    {
        const string definition = "CREATE OR ALTER PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.True(SqlModuleScript.TryConvertCreateToAlter(definition, out var script));
        Assert.Equal("ALTER PROCEDURE dbo.usp_Test", script.Substring(0, SqlModuleScript.FindHeaderNameEnd(script)));
    }

    [Fact]
    public void 開頭的註解算進位置裡()
    {
        const string script = "-- 版權宣告\r\n/* 說明 */\r\nALTER PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.Equal(
            "-- 版權宣告\r\n/* 說明 */\r\nALTER PROCEDURE dbo.usp_Test".Length,
            SqlModuleScript.FindHeaderNameEnd(script));
    }

    /// <summary>
    /// 標頭只切前面一小段來找，因此超長的開頭註解會把名稱推出那個視窗；
    /// 少了退回完整掃描的那一步，症狀是「有版權宣告的程序游標停在最後一行」。
    /// </summary>
    [Fact]
    public void 超長的開頭註解仍找得到名稱()
    {
        var script = "/*" + new string('x', 4000) + "*/\r\nALTER PROCEDURE dbo.usp_Test AS SELECT 1";

        Assert.Equal(
            script.IndexOf("dbo.usp_Test", StringComparison.Ordinal) + "dbo.usp_Test".Length,
            SqlModuleScript.FindHeaderNameEnd(script));
    }

    /// <summary>
    /// 視窗剛好切在名稱中間時同樣要退回完整掃描，否則游標會停在名稱的一半上。
    /// </summary>
    [Fact]
    public void 名稱橫跨視窗邊界仍找得到結尾()
    {
        var name = new string('n', 200);
        var script = "/*" + new string('x', 900) + "*/ ALTER PROCEDURE dbo." + name + " AS SELECT 1";

        Assert.Equal(script.IndexOf(name, StringComparison.Ordinal) + name.Length, SqlModuleScript.FindHeaderNameEnd(script));
    }

    /// <summary>認不出標頭時回傳 -1，由呼叫端退回停在結尾。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SELECT 1")]
    [InlineData("-- 只有註解")]
    [InlineData("ALTER PROCEDURE")]
    public void 認不出標頭時回傳負值(string script)
    {
        Assert.Equal(-1, SqlModuleScript.FindHeaderNameEnd(script));
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
