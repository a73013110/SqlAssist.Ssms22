using System;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

/// <summary>
/// F12 送進新查詢視窗的那一份指令碼。
/// </summary>
/// <remarks>
/// 這裡固定三件事：批次分隔對不對、模組才改寫成 ALTER、以及游標停在名稱之後。
/// 前兩件錯了指令碼就執行不了，第三件錯了只是難用——但三件都不會在編譯時被發現。
/// </remarks>
public sealed class SqlObjectScriptTests
{
    private const string Header =
        "SET QUOTED_IDENTIFIER ON\r\nSET ANSI_NULLS ON\r\nGO\r\n";

    private static SqlObjectStructure Module(
        SqlObjectKind kind,
        string name,
        string? definition)
    {
        return new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(1, "dbo", name, kind), definition: definition));
    }

    private static SqlObjectStructure Table()
    {
        return new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(2, "dbo", "Lib_Reader", SqlObjectKind.Table),
                new[]
                {
                    new SqlColumnInfo(1, "Id", "int", false, isIdentity: true, isPrimaryKey: true),
                    new SqlColumnInfo(2, "DisplayName", "nvarchar(60)", false)
                }));
    }

    [Fact]
    public void 模組的CREATE改寫成ALTER並包進批次樣板()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.Procedure, "usp_LoanFinish", "CREATE PROCEDURE dbo.usp_LoanFinish\r\nAS\r\nSELECT 1;"),
            "\r\n");

        Assert.Equal(
            Header + "ALTER PROCEDURE dbo.usp_LoanFinish\r\nAS\r\nSELECT 1;\r\nGO\r\n",
            script.Text);
    }

    [Fact]
    public void CREATE_OR_ALTER併成單一個ALTER()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.View, "v_LoanDetail", "CREATE OR ALTER VIEW dbo.v_LoanDetail AS SELECT 1 AS x;"),
            "\r\n");

        Assert.Contains("ALTER VIEW dbo.v_LoanDetail", script.Text);
        Assert.DoesNotContain("CREATE", script.Text);
    }

    /// <remarks>
    /// 資料表沒有對應的 ALTER TABLE 整體寫法。改寫下去得到的是一段執行不了的
    /// 指令碼，而且是執行到一半才失敗的那一種。
    /// </remarks>
    [Fact]
    public void 資料表維持CREATE_TABLE()
    {
        var script = SqlObjectScript.BuildEditable(Table(), "\r\n");

        Assert.StartsWith(Header + "CREATE TABLE [dbo].[Lib_Reader]", script.Text);
        Assert.EndsWith("GO\r\n", script.Text);
    }

    /// <remarks>
    /// 檢視同時是模組也有欄位。定義取不到時 BuildScript 已經把整段換成註解，
    /// 這裡要確認的是那段註解原樣帶出來，沒有被當成 CREATE 改寫掉。
    /// </remarks>
    [Fact]
    public void 取不到定義時整段註解原樣保留()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.View, "v_LoanDetail", definition: null),
            "\r\n");

        Assert.StartsWith(Header + "-- 取不到 [dbo].[v_LoanDetail] 的定義。", script.Text);
        Assert.Contains("WITH ENCRYPTION", script.Text);
        Assert.DoesNotContain("CREATE TABLE", script.Text);
    }

    /// <remarks>
    /// 欄位查得回來與否是這一輪的事，不是種類的事：資料表過得了種類那一關，
    /// 卻可能一列都沒有回來（物件被卸除、權限被收回）。BuildScript 那一端已經
    /// 換成整段註解，這裡要確認的是它原樣帶出來，沒有被當成 CREATE 改寫掉，
    /// 也沒有留下一段只剩空括號、卻仍然貼得上去的 CREATE TABLE。
    /// </remarks>
    [Fact]
    public void 取不到欄位的資料表整段註解()
    {
        var script = SqlObjectScript.BuildEditable(
            new SqlObjectStructure(
                new SqlObjectDetail(new SqlObjectInfo(5, "dbo", "Lib_Tag", SqlObjectKind.Table))),
            "\r\n");

        Assert.StartsWith(Header + "-- 取不到 [dbo].[Lib_Tag] 的欄位。", script.Text);
        Assert.Contains("sys.columns", script.Text);
        Assert.DoesNotContain("CREATE TABLE", script.Text);
    }

    /// <remarks>
    /// 資料表型別有欄位，落到資料表那一支就會被寫成 CREATE TABLE——照著執行
    /// 會多出一張同名的資料表。與檢視取不到定義時不能掉進 CREATE TABLE 同一條理由。
    ///
    /// 這一條與浮動預覽的指令碼分頁走同一個判斷（<c>HasExecutableScript</c>），
    /// 所以 F12 拿到的就是 BuildScript 組出來的 CREATE TYPE，只多包了批次樣板。
    /// 兩邊各留一份判斷的症狀，就是同一個型別在預覽是 CREATE TABLE、在這裡是註解。
    /// </remarks>
    [Fact]
    public void 資料表型別寫成CREATE_TYPE()
    {
        var script = SqlObjectScript.BuildEditable(
            new SqlObjectStructure(
                new SqlObjectDetail(
                    new SqlObjectInfo(3, "dbo", "LoanIdList", SqlObjectKind.TableType),
                    new[] { new SqlColumnInfo(1, "LoanId", "int", false) })),
            "\r\n");

        Assert.StartsWith(Header + "CREATE TYPE [dbo].[LoanIdList] AS TABLE", script.Text);
        Assert.Contains("    [LoanId] int NOT NULL", script.Text);
        Assert.EndsWith("GO\r\n", script.Text);
        Assert.DoesNotContain("CREATE TABLE", script.Text);

        // 型別沒有 ALTER 的整體寫法，開頭的 CREATE 不可以被改寫掉。
        Assert.DoesNotContain("ALTER", script.Text);
    }

    /// <remarks>
    /// 同義字指向誰不在本擴充查詢的中繼資料裡，組不出 CREATE SYNONYM。
    /// BuildScript 給的摘要（<c>Synonym [dbo].[syn_Loan]</c>）不是 T-SQL，
    /// 原樣送進查詢視窗就是一執行就語法錯誤。
    /// </remarks>
    [Fact]
    public void 同義字整段註解掉()
    {
        var script = SqlObjectScript.BuildEditable(
            new SqlObjectStructure(
                new SqlObjectDetail(new SqlObjectInfo(4, "dbo", "syn_Loan", SqlObjectKind.Synonym))),
            "\r\n");

        foreach (var line in script.Text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(
                line.StartsWith("--", StringComparison.Ordinal) ||
                line is "SET QUOTED_IDENTIFIER ON" or "SET ANSI_NULLS ON" or "GO",
                $"這一行不是註解也不是樣板：{line}");
        }
    }

    [Fact]
    public void 游標停在標頭的物件名稱之後()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.Procedure, "usp_LoanFinish", "CREATE PROCEDURE dbo.usp_LoanFinish\r\n@Id int\r\nAS\r\nSELECT 1;"),
            "\r\n");

        Assert.Equal(
            Header + "ALTER PROCEDURE dbo.usp_LoanFinish",
            script.Text.Substring(0, script.CaretOffset));
    }

    /// <remarks>
    /// 認不出標頭時停在本體的第一個字元，不是停在結尾——停在結尾等於
    /// 一打開就被捲到最後一行。
    /// </remarks>
    [Fact]
    public void 認不出標頭時停在本體開頭()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.Procedure, "usp_LoanFinish", definition: null),
            "\r\n");

        Assert.Equal(Header.Length, script.CaretOffset);
    }

    /// <remarks>
    /// 資料庫裡存的定義用哪一種換行完全看當初是誰建的，而樣板是本擴充寫死的。
    /// 不統一的話兩者會在同一份檔案裡混著出現。
    /// </remarks>
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void 樣板與定義的換行統一成同一種(string newLine)
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.Procedure, "usp_LoanFinish", "CREATE PROCEDURE dbo.usp_LoanFinish\nAS\rSELECT 1;\r\nRETURN;"),
            newLine);

        // 樣板 3 個換行、定義 3 個、定義結尾補 1 個、結尾的 GO 1 個。
        Assert.Equal(8, script.Text.Split(new[] { newLine }, StringSplitOptions.None).Length - 1);

        var remainder = script.Text.Replace(newLine, " ");
        Assert.DoesNotContain('\r', remainder);
        Assert.DoesNotContain('\n', remainder);
    }

    [Fact]
    public void 認不得的換行退回作業系統預設值()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.Procedure, "usp_LoanFinish", "CREATE PROCEDURE dbo.usp_LoanFinish AS SELECT 1;"),
            newLine: " ");

        Assert.StartsWith("SET QUOTED_IDENTIFIER ON" + Environment.NewLine, script.Text);
    }

    /// <remarks>已經是 ALTER 的定義不能再被動一次，否則關鍵字會被吃掉。</remarks>
    [Fact]
    public void 已經是ALTER的定義原樣帶出()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.Procedure, "usp_LoanFinish", "ALTER PROCEDURE dbo.usp_LoanFinish AS SELECT 1;"),
            "\r\n");

        Assert.Equal(
            Header + "ALTER PROCEDURE dbo.usp_LoanFinish AS SELECT 1;\r\nGO\r\n",
            script.Text);
    }

    /// <remarks>
    /// 定義本身以換行結尾時不要多墊一行；GO 前面固定只有一個換行。
    /// </remarks>
    [Fact]
    public void 定義結尾已有換行時不重複墊行()
    {
        var script = SqlObjectScript.BuildEditable(
            Module(SqlObjectKind.Procedure, "usp_LoanFinish", "CREATE PROCEDURE dbo.usp_LoanFinish AS SELECT 1;\r\n"),
            "\r\n");

        Assert.EndsWith("SELECT 1;\r\nGO\r\n", script.Text);
    }
}
