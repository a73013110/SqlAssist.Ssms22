using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// SQL Agent 作業搜尋：分類映射、名稱與本文命中、預算、取消、權限不足降級與範圍。
/// </summary>
/// <remarks>
/// 一律不連資料庫，走 <see cref="FakeAgentServer"/>。
/// </remarks>
[Collection(MetadataFailureCollection.Name)]
public sealed class SqlAgentJobSearchProviderTests
{
    [Fact]
    public async Task 作業名稱命中掛在作業分類上並帶著高亮區段()
    {
        var sink = await RunAsync(NewServer(), new SearchQuery("Lib_Loan"));

        var hit = Assert.Single(sink.Hits, entry => entry.CategoryId == "agent-job.job");

        Assert.Equal(SearchMatchTarget.Name, hit.MatchTarget);
        Assert.Equal("Lib_Loan 夜間維護", hit.Title);

        // 片段就是名稱本體，區段的索引落在它上面——沒有這一份就沒有地方放高亮。
        Assert.Equal("Lib_Loan 夜間維護", hit.Snippet);
        Assert.Equal("Lib_Loan", Flatten(hit));

        // 作業沒有限定名稱：它不在任何結構描述底下，也不屬於任何資料庫。
        Assert.Null(hit.Path);
    }

    /// <summary>步驟是自己一種分類，不掛在作業那一顆 pill 上。</summary>
    [Fact]
    public async Task 步驟名稱命中掛在作業步驟分類上()
    {
        var sink = await RunAsync(NewServer(), new SearchQuery("Cat_BookCopy", targets: SearchTargets.Name));

        var hit = Assert.Single(sink.Hits);

        Assert.Equal("agent-job.step", hit.CategoryId);
        Assert.Equal(SearchMatchTarget.Name, hit.MatchTarget);

        // 標題帶著作業與第幾步：步驟名稱在不同作業裡重複是常態，只寫它的話
        // 清單上會出現一排一模一樣的字。
        Assert.Equal("Lib_Loan 夜間維護 › 第 1 步 重建 Cat_BookCopy 索引", hit.Title);

        var target = Assert.IsType<SqlAgentJobSearchTarget>(hit.ActivatePayload);
        Assert.Equal(1, target.StepId);
        Assert.Equal("Lib_Loan 夜間維護", target.JobName);
        Assert.Equal("LIBSQL01", target.ServerName);
        Assert.Equal("TSQL", target.Subsystem);
    }

    /// <summary>步驟的命令就是這個來源的「定義本文」。</summary>
    [Fact]
    public async Task 步驟命令命中帶著前後文片段()
    {
        var sink = await RunAsync(NewServer(), new SearchQuery("UPDATE STATISTICS"));

        var hit = Assert.Single(sink.Hits, entry => entry.MatchTarget == SearchMatchTarget.Text);

        Assert.Equal("agent-job.step", hit.CategoryId);
        Assert.Equal("Lib_Loan 夜間維護 › 第 2 步 更新 Loan 統計值", hit.Title);
        Assert.Equal("UPDATE STATISTICS dbo.Loan;", hit.Snippet);
        Assert.Equal("UPDATE STATISTICS", Flatten(hit));
    }

    /// <summary>本文命中只掛在步驟上；作業本身沒有本文。</summary>
    [Fact]
    public async Task 本文命中不會掛到作業身上()
    {
        var sink = await RunAsync(NewServer(), new SearchQuery("ALTER INDEX"));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("agent-job.step", hit.CategoryId);
        Assert.Equal(SearchMatchTarget.Text, hit.MatchTarget);
    }

    /// <summary>
    /// 不搜本文的那一輪<b>連命令都不撈</b>。
    /// </summary>
    /// <remarks>
    /// 撈回來再丟掉的話，這個來源最貴的那一段一毫秒都沒有省到，而使用者以為自己關掉了它。
    /// </remarks>
    [Fact]
    public async Task 只搜名稱時不送命令那一條查詢()
    {
        var server = NewServer();

        await RunAsync(server, new SearchQuery("Loan", targets: SearchTargets.Name));

        Assert.Equal(1, server.CountCommands("FROM msdb.dbo.sysjobs AS j"));
        Assert.Equal(0, server.CountCommands("s.command"));
    }

    /// <summary>
    /// 兩顆 pill 都被勾掉時整輪不必付任何代價。
    /// </summary>
    /// <remarks>
    /// 靠聚合器那道最後防線的話，這一輪仍然會向 msdb 撈一次全部的作業，
    /// 只為了讓每一筆結果被靜靜丟掉。
    /// </remarks>
    [Fact]
    public async Task 分類全部勾掉時連一次連線都不開()
    {
        var server = NewServer();

        var sink = await RunAsync(
            server, new SearchQuery("Loan", categories: new[] { "catalog.table" }));

        Assert.Empty(sink.Hits);
        Assert.Equal(0, server.Opened);
    }

    /// <summary>只勾「作業」時，步驟的名稱與命令都不該出現。</summary>
    [Fact]
    public async Task 只勾作業時步驟命中不出現()
    {
        var server = NewServer();

        var sink = await RunAsync(
            server, new SearchQuery("Cat_BookCopy", categories: new[] { "agent-job.job" }));

        Assert.Empty(sink.Hits);

        // 步驟被勾掉了，命令那一段也不必付。
        Assert.Equal(0, server.CountCommands("s.command"));
    }

    /// <summary>還沒有步驟的作業仍然搜得到名稱。</summary>
    /// <remarks>
    /// 產品那一條是 <c>LEFT JOIN</c>；換成 <c>INNER JOIN</c> 的話它會整個消失，
    /// 而那與「這台伺服器上沒有這個作業」在畫面上一模一樣。
    /// </remarks>
    [Fact]
    public async Task 沒有步驟的作業仍然搜得到()
    {
        var server = new FakeAgentServer();
        server.AddJob("Lib_Tag 重新整理");

        var sink = await RunAsync(server, new SearchQuery("Lib_Tag"));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("agent-job.job", hit.CategoryId);
        Assert.Equal("Lib_Tag 重新整理", hit.Title);
    }

    /// <summary>同一個步驟被名稱與命令同時命中時是同一個去重鍵。</summary>
    /// <remarks>
    /// 兩個鍵的話，清單上會出現兩列指向同一個步驟；聚合器靠的就是兩邊寫出同一個字串。
    /// </remarks>
    [Fact]
    public async Task 名稱與本文命中共用同一個去重鍵()
    {
        var server = new FakeAgentServer();
        server.AddJob("Lib_Loan 夜間維護").WithStep(1, "Loan 重建", "TRUNCATE TABLE dbo.Loan;");

        var sink = await RunAsync(server, new SearchQuery("Loan"));

        var step = sink.Hits.Where(hit => hit.CategoryId == "agent-job.step").ToArray();

        Assert.Equal(2, step.Length);
        Assert.Equal(step[0].DedupeKey, step[1].DedupeKey);
        Assert.Contains(step, hit => hit.MatchTarget == SearchMatchTarget.Name);
        Assert.Contains(step, hit => hit.MatchTarget == SearchMatchTarget.Text);

        // 去重鍵含伺服器：兩台互相還原出來的 msdb 上，同一個作業的 job_id 一模一樣。
        Assert.StartsWith("agent-job|LIBSQL01|", step[0].DedupeKey);
    }

    /// <summary>每一列都掛伺服器膠囊；停用與步驟的資料庫各自再加一顆。</summary>
    /// <remarks>
    /// 清單上其他來源的列掛的是資料庫，兩者擺在一起使用者才看得出這一列不跟著資料庫範圍走。
    /// </remarks>
    [Fact]
    public async Task 命中帶著伺服器停用與資料庫膠囊()
    {
        var sink = await RunAsync(NewServer(), new SearchQuery("Lib_Reader", targets: SearchTargets.Name));

        var job = Assert.Single(sink.Hits, hit => hit.CategoryId == "agent-job.job");

        Assert.Equal("server:LIBSQL01", job.Badges[0].ToString());
        Assert.Equal("已停用", job.Badges[1].Text);

        var step = Assert.Single(sink.Hits, hit => hit.CategoryId == "agent-job.step");

        Assert.Equal("database:Library", step.Badges[2].ToString());
    }

    /// <summary>非 T-SQL 的步驟沒有資料庫，不掛一顆空膠囊。</summary>
    [Fact]
    public async Task 說不出資料庫的步驟不掛資料庫膠囊()
    {
        var sink = await RunAsync(NewServer(), new SearchQuery("搬走匯入檔", targets: SearchTargets.Name));

        var hit = Assert.Single(sink.Hits);

        Assert.DoesNotContain(hit.Badges, badge => badge.IconToken == SearchBadge.DatabaseIcon);
    }

    [Fact]
    public async Task 預算用完時立刻停止掃描()
    {
        var server = new FakeAgentServer();
        server.AddJob("Loan01");
        server.AddJob("Loan02");
        server.AddJob("Loan03");

        var sink = await RunAsync(server, new SearchQuery("Loan"), acceptLimit: 1);

        Assert.Single(sink.Hits);

        // 只看筆數分不出「停下來了」與「照掃到底但多的被丟掉」；分得出來的是候選數。
        Assert.Equal(2, sink.Examined);
        Assert.True(sink.IsTruncated);
        Assert.Equal("agent-job|LIBSQL01|00000002-0000-0000-0000-000000000000", sink.Checkpoint);
    }

    [Fact]
    public async Task 取消時擲出取消例外()
    {
        var provider = new SqlAgentJobSearchProvider(NewServer().SourceFor());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync(new SearchQuery("Loan"), new RecordingSearchSink(), cancellation.Token));
    }

    /// <summary>
    /// 讀不到 <c>msdb</c> 是常態：降級成「這個來源沒有資料」，不是錯誤也不是空白。
    /// </summary>
    /// <remarks>
    /// 讓 <c>DbException</c> 冒出去的話，聚合器會把它記成一次來源失敗，而工具窗的頁尾
    /// 會被紅字佔住——對一個多半讀不到的來源，那等於每一次搜尋都在報錯。
    /// 什麼都不說則與「這台伺服器上真的沒有這個作業」一模一樣。
    /// </remarks>
    [Fact]
    public async Task 權限不足降級成這個來源沒有資料()
    {
        var server = NewServer();
        server.MsdbDenied = true;

        var cache = new SqlAgentJobSearchSnapshotCache();

        var reported = SqlCatalogSearchIndexTests.Capture(() =>
            RunAsync(server, new SearchQuery("Loan"), cache: cache).GetAwaiter().GetResult());

        // 失敗帶著「哪一條查詢」與伺服器說的那句話走 SqlMetadataFailure，不冒出去。
        // Reporter 是行程共用的靜態接線，同時跑的其他測試也會寫進來，所以只找自己那一行。
        Assert.Contains(reported, line => line.Contains("SQL Agent 作業清單") && line.Contains("連不上伺服器。"));

        var sink = await RunAsync(server, new SearchQuery("Loan"), cache: cache);

        Assert.Empty(sink.Hits);

        // 「讀不到」不是「沒掃完」：後者叫使用者縮小範圍，而那對沒有權限完全沒有用。
        Assert.False(sink.IsTruncated);
        Assert.Null(sink.Checkpoint);

        Assert.True(sink.IsUnavailable);

        // 那一句話由這個來源自己寫，呼叫端原樣貼上去——它指得出少了哪一個來源，
        // 也指得出該去看什麼。
        Assert.Contains("SQL Agent 作業", sink.UnavailableReason);
        Assert.Contains("msdb", sink.UnavailableReason);

        // 失敗不進快取：否則權限恢復之後仍然拿到「沒有資料」。
        Assert.False(cache.IsFresh(server.SourceFor().ServerCacheKey));
        Assert.Equal(2, cache.Loads);
    }

    /// <summary>權限恢復之後下一輪就拿得到，不需要另外一套重試邏輯。</summary>
    [Fact]
    public async Task 權限恢復之後下一輪就有結果()
    {
        var server = NewServer();
        server.MsdbDenied = true;
        var cache = new SqlAgentJobSearchSnapshotCache();

        SqlCatalogSearchIndexTests.Capture(() =>
            RunAsync(server, new SearchQuery("Lib_Loan"), cache: cache).GetAwaiter().GetResult());

        server.MsdbDenied = false;
        var sink = await RunAsync(server, new SearchQuery("Lib_Loan"), cache: cache);

        Assert.Single(sink.Hits, hit => hit.CategoryId == "agent-job.job");
        Assert.False(sink.IsTruncated);
    }

    /// <summary>
    /// 指名了伺服器就整輪不回結果。
    /// </summary>
    /// <remarks>
    /// 手上這條連線是工具窗選的那一台；拿它的作業回答「另一台有哪些作業」正是明文禁止的
    /// 那種退路，而退過在畫面上看不出來。
    /// </remarks>
    [Fact]
    public async Task 指名別台伺服器時整輪不回結果()
    {
        var server = NewServer();

        var sink = await RunAsync(
            server,
            new SearchQuery("Lib_Loan", scope: new SearchScope(new[] { "LIBSQL02" }, null)));

        Assert.Empty(sink.Hits);
        Assert.False(sink.IsTruncated);

        // 連線一次都不開：整輪不回結果就不該付任何代價。
        Assert.Equal(0, server.Opened);
    }

    /// <summary>
    /// 資料庫範圍不縮小這個來源。
    /// </summary>
    /// <remarks>
    /// <c>sysjobsteps.database_name</c> 只有 TSQL 子系統填得出來，拿它過濾等於把
    /// CmdExec 與 PowerShell 步驟整組藏掉，而畫面上看不出被藏過。要拿掉作業用分類 pill。
    /// </remarks>
    [Fact]
    public async Task 指名資料庫不影響作業來源()
    {
        var scoped = await RunAsync(
            NewServer(),
            new SearchQuery("Lib_Loan", scope: new SearchScope(null, new[] { "LibArchive" })));

        Assert.Single(scoped.Hits, hit => hit.CategoryId == "agent-job.job");
    }

    /// <summary>
    /// 快照以<b>伺服器</b>為鍵，不是資料庫。
    /// </summary>
    /// <remarks>
    /// 用錯的症狀不是錯誤而是浪費：使用者在同一台伺服器上換一次資料庫，整批作業就重撈一次，
    /// 而畫面上只看得出「搜尋變慢了」。
    /// </remarks>
    [Fact]
    public async Task 同一台伺服器換資料庫不重撈()
    {
        var server = NewServer();
        var cache = new SqlAgentJobSearchSnapshotCache();

        await RunAsync(server, new SearchQuery("Lib_Loan"), cache: cache, databaseName: "Library");
        await RunAsync(server, new SearchQuery("Lib_Loan"), cache: cache, databaseName: "LibArchive");

        Assert.Equal(1, cache.Loads);
        Assert.Equal(1, server.Opened);
    }

    /// <summary>手上那一份只有識別、而這一輪要命令本文時要補撈。</summary>
    [Fact]
    public async Task 之後才要本文時補撈一次()
    {
        var server = NewServer();
        var cache = new SqlAgentJobSearchSnapshotCache();

        await RunAsync(server, new SearchQuery("Loan", targets: SearchTargets.Name), cache: cache);
        Assert.Equal(0, server.CountCommands("s.command"));

        await RunAsync(server, new SearchQuery("Loan", 1), cache: cache);
        Assert.Equal(1, server.CountCommands("s.command"));

        // 補撈之後就留著：第三輪不再問。
        await RunAsync(server, new SearchQuery("Loan", 2), cache: cache);
        Assert.Equal(1, server.CountCommands("s.command"));
    }

    /// <summary>伺服器說不出自己叫什麼時不擲例外，退到一個說得出口的名字。</summary>
    /// <remarks>
    /// 退成空字串的話，酬載的建構子會擲出 ArgumentException——那不是 DbException，
    /// 降級接不住，而聚合器會把整個來源記成一次失敗。
    /// </remarks>
    [Fact]
    public async Task 伺服器說不出名字時仍然回得了結果()
    {
        var server = NewServer();
        server.ServerName = null;

        var sink = await RunAsync(server, new SearchQuery("Lib_Loan", targets: SearchTargets.Name));

        var hit = Assert.Single(sink.Hits, entry => entry.CategoryId == "agent-job.job");
        var target = Assert.IsType<SqlAgentJobSearchTarget>(hit.ActivatePayload);

        Assert.Equal(SqlAgentJobSearchSnapshot.UnknownServerName, target.ServerName);
    }

    /// <summary>命令本文只收到一半也是「沒掃完」。</summary>
    /// <remarks>
    /// 不說的話，使用者看到的與「這個字串在這台伺服器的作業裡不存在」一模一樣。
    /// </remarks>
    [Fact]
    public void 命令本文超過上限時標記不完整()
    {
        var server = new FakeAgentServer();
        server.AddJob("Lib_Loan 夜間維護")
            .WithStep(1, "第一段", new string('A', 64))
            .WithStep(2, "第二段", new string('B', 64));

        var snapshot = SqlAgentJobSearchSnapshot.TryLoad(
            server.SourceFor(), includeCommands: true, CancellationToken.None, maxCommandCharacters: 64);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.CommandsComplete);

        // 擋住的那一段是空字串（「這一輪沒收到」），不是 null（「沒有問」）。
        Assert.Equal(new string('A', 64), snapshot.Jobs[0].Steps[0].Command);
        Assert.Equal("", snapshot.Jobs[0].Steps[1].Command);
    }

    /// <summary>沒有撈第二段時命令是 null，不是空字串。</summary>
    /// <remarks>
    /// 兩者混成一種的話，「這個步驟的命令真的是空的」與「這一輪沒有問」分不出來，
    /// 而下游會對後者做本文比對並得到「沒有命中」。
    /// </remarks>
    [Fact]
    public void 沒有撈命令時命令是null()
    {
        var snapshot = SqlAgentJobSearchSnapshot.TryLoad(
            NewServer().SourceFor(), includeCommands: false, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IncludesCommands);
        Assert.Null(snapshot.Jobs[0].Steps[0].Command);
    }

    /// <summary>參數契約違反是程式錯誤，要一路浮到平台邊界去留下完整堆疊。</summary>
    [Fact]
    public void 參數違約仍然擲出例外()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlAgentJobSearchProvider(null!));
        Assert.Throws<ArgumentNullException>(
            () => SqlAgentJobSearchSnapshot.TryLoad(null!, includeCommands: true, CancellationToken.None));
    }

    /// <summary>連線本身開不起來也是同一條降級路徑。</summary>
    [Fact]
    public async Task 連不上時降級而不是擲出例外()
    {
        var server = NewServer();
        server.FailsOnOpen = true;

        RecordingSearchSink? sink = null;
        var reported = SqlCatalogSearchIndexTests.Capture(
            () => sink = RunAsync(server, new SearchQuery("Loan")).GetAwaiter().GetResult());

        Assert.Contains(reported, line => line.Contains("開啟 SQL Agent 作業連線"));
        Assert.Empty(sink!.Hits);
        Assert.True(sink.IsUnavailable);
        Assert.Contains("SQL Agent 作業", sink.UnavailableReason);
    }

    /// <summary>這個來源宣告自己的兩顆 pill，不借用目錄那一組。</summary>
    [Fact]
    public void 宣告自己的分類()
    {
        var provider = new SqlAgentJobSearchProvider(NewServer().SourceFor());

        Assert.Equal("agent-job", provider.Id);
        Assert.Equal(
            new[] { "agent-job.job", "agent-job.step" },
            provider.Categories.Select(category => category.Id).ToArray());

        // 每一顆都認領得回這個 provider，而且與目錄那一組不共用群組。
        Assert.All(provider.Categories, category => Assert.Equal("agent-job", category.ProviderId));
        Assert.All(
            provider.Categories,
            category => Assert.Equal(SqlAgentJobSearchCategories.GroupId, category.GroupId));
    }

    /// <summary>命中的那幾段字；高亮畫錯位置看起來像是比對錯了。</summary>
    private static string Flatten(SearchHit hit) =>
        string.Concat(hit.SnippetSpans.Select(span => hit.Snippet.Substring(span.Start, span.Length)));

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

    private static async Task<RecordingSearchSink> RunAsync(
        FakeAgentServer server,
        SearchQuery query,
        int acceptLimit = int.MaxValue,
        SqlAgentJobSearchSnapshotCache? cache = null,
        string databaseName = "Library")
    {
        var provider = new SqlAgentJobSearchProvider(server.SourceFor(databaseName), cache);
        var sink = new RecordingSearchSink(acceptLimit);

        await provider.SearchAsync(query, sink, CancellationToken.None);

        return sink;
    }
}
