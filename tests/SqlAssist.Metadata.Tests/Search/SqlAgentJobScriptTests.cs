using System;
using System.Linq;
using System.Threading;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 作業結果的主要動作：把步驟命令組成可以送進查詢視窗的文字。
/// </summary>
[Collection(MetadataFailureCollection.Name)]
public sealed class SqlAgentJobScriptTests
{
    private const string NewLine = "\n";

    [Fact]
    public void 指名步驟時只取那一步()
    {
        var server = NewServer();
        var script = Build(server, StepTarget(server, stepId: 2));

        Assert.NotNull(script);
        Assert.Contains("UPDATE STATISTICS dbo.Loan;", script!);
        Assert.DoesNotContain("ALTER INDEX", script);
        Assert.Contains("第 2 步：更新 Loan 統計值（TSQL）", script);
    }

    [Fact]
    public void 沒有指名步驟時整個作業的步驟都開出來()
    {
        var server = NewServer();
        var script = Build(server, JobTarget(server));

        Assert.NotNull(script);
        Assert.Contains("ALTER INDEX ALL ON dbo.Cat_BookCopy REBUILD;", script!);
        Assert.Contains("UPDATE STATISTICS dbo.Loan;", script);
    }

    /// <summary>
    /// 檔頭要說得出「這是哪一台伺服器的哪一個作業」。
    /// </summary>
    /// <remarks>
    /// 新視窗沿用的是查詢視窗那條連線，不一定是作業所在的那一台；少了這幾行，
    /// 使用者會對著另一台伺服器按 F5。
    /// </remarks>
    [Fact]
    public void 檔頭寫出伺服器與作業()
    {
        var server = NewServer();
        var script = Build(server, JobTarget(server))!;

        Assert.StartsWith("-- SQL Agent 作業：Lib_Loan 夜間維護\n-- 伺服器：LIBSQL01\n", script);
        Assert.Contains("不會回寫到作業上", script);
    }

    /// <summary>停用的作業要說，否則使用者會以為這段 SQL 每天都在跑。</summary>
    [Fact]
    public void 停用的作業在檔頭說一句()
    {
        var server = NewServer();
        var disabled = server.Jobs.Single(job => !job.Enabled);

        var script = Build(
            server,
            new SqlAgentJobSearchTarget("LIBSQL01", disabled.JobId, disabled.Name, isEnabled: false))!;

        Assert.Contains("-- 這個作業目前是停用的。", script);
    }

    /// <summary>
    /// 不是 T-SQL 的步驟整段換成註解。
    /// </summary>
    /// <remarks>
    /// CmdExec 的命令貼進查詢視窗一樣看得懂，但按下 F5 是一個語法錯誤，
    /// 而錯誤訊息指不出「這本來就不是 SQL」。與「資料不齊時不輸出半份可以執行的東西」
    /// 是同一條規則。
    /// </remarks>
    [Fact]
    public void 非TSQL的步驟整段註解掉()
    {
        var server = NewServer();
        var job = server.Jobs.Single(entry => !entry.Enabled);

        var script = Build(
            server,
            new SqlAgentJobSearchTarget(
                "LIBSQL01", job.JobId, job.Name, isEnabled: false, stepId: 2, subsystem: "CmdExec"))!;

        Assert.Contains("子系統不是 TSQL", script);

        // 命令原文的每一行都在註解裡；漏一行就是一行貼得上去卻跑不動的東西。
        foreach (var line in script.Split('\n'))
        {
            Assert.True(line.Length == 0 || line.StartsWith("-- ", StringComparison.Ordinal), line);
        }
    }

    /// <summary>命令裡的換行統一成目的地文件那一種。</summary>
    /// <remarks>
    /// msdb 裡存的可能是 CRLF，而目的地可能是 LF；混在一起的症狀是查詢視窗裡
    /// 每一行結尾多一個看不見的字元。
    /// </remarks>
    [Fact]
    public void 命令的換行統一成目的地那一種()
    {
        var server = new FakeAgentServer();
        server.AddJob("Lib_Loan 夜間維護").WithStep(1, "兩行", "SELECT 1;\r\nSELECT 2;");

        var script = Build(server, JobTarget(server))!;

        Assert.DoesNotContain("\r", script);
        Assert.Contains("SELECT 1;\nSELECT 2;", script);
    }

    /// <summary>
    /// 查得到作業卻一個步驟都沒有回來時回 null。
    /// </summary>
    /// <remarks>
    /// 原因只有兩個：作業在清單被快取之後被刪掉，或這個登入對它的權限在那之後被收回。
    /// 組一份「這個作業沒有步驟」的空指令碼會把兩種情形說成第三種。
    /// </remarks>
    [Fact]
    public void 一個步驟都沒有回來時回null()
    {
        var server = new FakeAgentServer();
        server.AddJob("Lib_Tag 重新整理");

        Assert.Null(Build(server, JobTarget(server)));
    }

    /// <summary>權限不足不擲例外，帶著「哪一條查詢」走 SqlMetadataFailure。</summary>
    [Fact]
    public void 權限不足降級並回報是哪一條查詢()
    {
        var server = NewServer();
        var target = JobTarget(server);
        server.MsdbDenied = true;

        string? script = null;
        var reported = SqlCatalogSearchIndexTests.Capture(() => script = Build(server, target));

        Assert.Null(script);

        // Reporter 是行程共用的靜態接線，同時跑的其他測試也會寫進來，所以只找自己那一行。
        Assert.Contains(
            reported,
            line => line.Contains("載入 SQL Agent 作業步驟") && line.Contains("Lib_Loan 夜間維護"));
    }

    [Fact]
    public void 參數違約仍然擲出例外()
    {
        var server = NewServer();

        Assert.Throws<ArgumentNullException>(
            () => SqlAgentJobScript.TryBuild(null!, JobTarget(server), NewLine, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(
            () => SqlAgentJobScript.TryBuild(server.SourceFor(), null!, NewLine, CancellationToken.None));
    }

    private static string? Build(FakeAgentServer server, SqlAgentJobSearchTarget target) =>
        SqlAgentJobScript.TryBuild(server.SourceFor(), target, NewLine, CancellationToken.None);

    private static SqlAgentJobSearchTarget JobTarget(FakeAgentServer server)
    {
        var job = server.Jobs[0];
        return new SqlAgentJobSearchTarget("LIBSQL01", job.JobId, job.Name, job.Enabled);
    }

    private static SqlAgentJobSearchTarget StepTarget(FakeAgentServer server, int stepId)
    {
        var job = server.Jobs[0];
        return new SqlAgentJobSearchTarget(
            "LIBSQL01", job.JobId, job.Name, job.Enabled, stepId, subsystem: "TSQL", databaseName: "Library");
    }

    private static FakeAgentServer NewServer()
    {
        var server = new FakeAgentServer();

        server.AddJob("Lib_Loan 夜間維護")
            .WithStep(1, "重建 Cat_BookCopy 索引", "ALTER INDEX ALL ON dbo.Cat_BookCopy REBUILD;")
            .WithStep(2, "更新 Loan 統計值", "UPDATE STATISTICS dbo.Loan;");

        server.AddJob("Lib_Reader 每日同步", enabled: false)
            .WithStep(1, "匯入 Lib_Reader", "INSERT INTO dbo.Lib_Reader SELECT * FROM stage.Reader;")
            .WithStep(2, "搬走匯入檔", "move \\\\reports\\reader.csv .", "CmdExec", "");

        return server;
    }
}
