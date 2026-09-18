using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 目錄物件搜尋：比對、片段、過濾、預算與失敗降級。
/// </summary>
/// <remarks>
/// 一律不連資料庫，走 <see cref="FakeCatalogServer"/>。
/// </remarks>
public sealed class SqlCatalogSearchProviderTests
{
    [Fact]
    public async Task 物件名稱命中帶著高亮區段()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Copy", "U")
            .WithObject(2, "dbo", "Cat_BookCopy", "U");

        var sink = await RunAsync(server, new SearchQuery("Copy"));

        var scattered = Assert.Single(sink.Hits, hit => hit.Title == "[dbo].[Cat_BookCopy]");
        var span = Assert.Single(scattered.SnippetSpans);

        // 片段就是名稱本體，區段的索引落在它上面——沒有這一份就沒有地方放高亮。
        Assert.Equal("Cat_BookCopy", scattered.Snippet);
        Assert.Equal(8, span.Start);
        Assert.Equal(4, span.Length);
        Assert.Equal(SearchHitClass.Name, scattered.HitClass);
        Assert.Equal("catalog.table", scattered.CategoryId);
    }

    /// <summary>詞首命中排在詞中命中前面；同一組輸入不該由掃描順序決定先後。</summary>
    [Fact]
    public async Task 詞首命中分數高於詞中命中()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Cat_BookCopy", "U")
            .WithObject(2, "dbo", "Copy", "U");

        var sink = await RunAsync(server, new SearchQuery("Copy"));

        var exact = Assert.Single(sink.Hits, hit => hit.Title == "[dbo].[Copy]");
        var scattered = Assert.Single(sink.Hits, hit => hit.Title == "[dbo].[Cat_BookCopy]");

        Assert.True(exact.Score > scattered.Score, $"{exact.Score} 應大於 {scattered.Score}");
    }

    [Fact]
    public async Task 資料行命中掛在資料行分類上並指回所屬物件()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(7, "dbo", "PUBLISHER", "U")
            .WithColumn(7, "PUBL_CODE");

        var sink = await RunAsync(server, new SearchQuery("PUBL_CODE"));

        var hit = Assert.Single(sink.Hits, h => h.CategoryId == SqlCatalogSearchCategories.ColumnCategoryId);

        Assert.Equal("[dbo].[PUBLISHER].[PUBL_CODE]", hit.Title);
        Assert.Equal("PUBL_CODE", hit.Snippet);

        var target = Assert.IsType<SqlCatalogSearchTarget>(hit.ActivatePayload);
        Assert.Equal("PUBL_CODE", target.ColumnName);
        Assert.Equal("PUBLISHER", target.Name);
        Assert.Equal(7, target.ObjectId);
        Assert.Equal("Library", target.DatabaseName);

        // 路徑指向物件：四段式名稱的第一段是連結伺服器，把資料行接成第四段
        // 會讓下游把資料庫名讀成伺服器名。
        Assert.Equal("Library", hit.Path!.DatabaseName);
        Assert.Equal("PUBLISHER", hit.Path.Name);
    }

    /// <summary>去重鍵與路徑都帶得到資料庫名稱。</summary>
    /// <remarks>
    /// 兩個資料庫裡各有一張 <c>Loan</c> 是常態，少了資料庫那一段，其中一列會被
    /// 去重吃掉，而使用者看不出少了哪一個。
    /// </remarks>
    [Fact]
    public async Task 去重鍵與路徑帶得到資料庫名稱()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithColumn(1, "CopyNo");

        var sink = await RunAsync(server, new SearchQuery("Loan"));

        var hit = Assert.Single(sink.Hits);

        Assert.Equal("[Library].[dbo].[Loan]", hit.DedupeKey);
        Assert.Equal("Library", hit.Path!.DatabaseName);
        Assert.Equal("dbo", hit.Path.SchemaName);
        Assert.Equal("Loan", hit.Path.Name);
    }

    [Fact]
    public async Task 資料行的去重鍵含資料行名稱()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithColumn(1, "CopyNo");

        var sink = await RunAsync(server, new SearchQuery("CopyNo"));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("[Library].[dbo].[Loan].[CopyNo]", hit.DedupeKey);
    }

    [Fact]
    public async Task 本文命中裁出命中所在的那一行()
    {
        var hit = await SingleBodyHitAsync(
            "CREATE VIEW dbo.Lib_Tag AS\nSELECT CopyNo, CopyNo AS c FROM dbo.Loan\nWHERE Branch = 1;",
            "CopyNo");

        Assert.Equal("SELECT CopyNo, CopyNo AS c FROM dbo.Loan", hit.Snippet);
        Assert.Equal(new[] { 7, 15 }, hit.SnippetSpans.Select(span => span.Start));
        AssertSpansPointAtMatches(hit, "CopyNo");

        // 分數是「提到幾次」；與名稱那一組不同尺度沒關係，兩組分開排。
        Assert.Equal(2, hit.Score);
        Assert.Equal(SearchHitClass.Body, hit.HitClass);
    }

    /// <summary>命中在整份本文的第一個字元：往回找換行的那一步不能越界。</summary>
    [Fact]
    public async Task 命中在行首的片段從行首開始()
    {
        var hit = await SingleBodyHitAsync("CopyNo = 1\nFROM dbo.Loan", "CopyNo");

        Assert.Equal("CopyNo = 1", hit.Snippet);
        Assert.Equal(0, Assert.Single(hit.SnippetSpans).Start);
        AssertSpansPointAtMatches(hit, "CopyNo");
    }

    [Fact]
    public async Task 命中在行尾的片段到行尾為止()
    {
        var hit = await SingleBodyHitAsync("SELECT dbo.Loan.CopyNo\nFROM dbo.Loan", "CopyNo");

        Assert.Equal("SELECT dbo.Loan.CopyNo", hit.Snippet);
        Assert.Equal(16, Assert.Single(hit.SnippetSpans).Start);
        AssertSpansPointAtMatches(hit, "CopyNo");
    }

    /// <remarks>
    /// CRLF 的 <c>\r</c> 留著會在片段尾巴畫出一個看不見的字元，而它會被算進區段範圍。
    /// </remarks>
    [Fact]
    public async Task 片段不帶回車字元()
    {
        var hit = await SingleBodyHitAsync("SELECT CopyNo\r\nFROM dbo.Loan", "CopyNo");

        Assert.Equal("SELECT CopyNo", hit.Snippet);
        AssertSpansPointAtMatches(hit, "CopyNo");
    }

    /// <summary>長到放不下的那一行要裁窗，而區段位移要跟著裁切後的字串走。</summary>
    [Fact]
    public async Task 超長的行裁成一段窗而位移仍然對得上()
    {
        var hit = await SingleBodyHitAsync(
            "SELECT " + new string('x', 200) + "CopyNo FROM dbo.Loan", "CopyNo");

        Assert.True(hit.Snippet.Length <= 160, $"片段長度 {hit.Snippet.Length}");
        AssertSpansPointAtMatches(hit, "CopyNo");
    }

    [Fact]
    public async Task 區分大小寫時本文只收逐字相同的命中()
    {
        var server = NewBodyServer("SELECT CopyNo FROM dbo.Loan");

        var sensitive = await RunAsync(server, new SearchQuery("copyno", options: SearchOptions.MatchCasing));
        Assert.Empty(sensitive.Hits);

        var insensitive = await RunAsync(NewBodyServer("SELECT CopyNo FROM dbo.Loan"), new SearchQuery("copyno"));
        Assert.Single(insensitive.Hits);
    }

    /// <remarks>
    /// 名稱走模糊比對，而模糊比對本身不分大小寫——v1 刻意如此：識別字在多數定序下
    /// 本來就不分大小寫，逐字比對會讓 PUBLISHER 打成 publisher 就一筆都不剩。
    /// </remarks>
    [Fact]
    public async Task 區分大小寫不影響名稱命中()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "PUBLISHER", "U");

        var sink = await RunAsync(server, new SearchQuery("publisher", options: SearchOptions.MatchCasing));

        Assert.Single(sink.Hits);
    }

    [Fact]
    public async Task 只取整個字時不收黏在識別字裡的命中()
    {
        var wholeWord = await RunAsync(
            NewBodyServer("SELECT CopyNoTotal FROM dbo.Loan"),
            new SearchQuery("CopyNo", options: SearchOptions.WholeWord));

        Assert.Empty(wholeWord.Hits);

        var anywhere = await RunAsync(
            NewBodyServer("SELECT CopyNoTotal FROM dbo.Loan"), new SearchQuery("CopyNo"));

        Assert.Single(anywhere.Hits);
    }

    /// <summary>詞界照識別字的形狀認，<c>#</c> 與 <c>@</c> 算邊界。</summary>
    [Fact]
    public async Task 只取整個字時仍然收得到暫存表與變數()
    {
        var sink = await RunAsync(
            NewBodyServer("INSERT INTO #CopyNo SELECT 1;"),
            new SearchQuery("CopyNo", options: SearchOptions.WholeWord));

        Assert.Single(sink.Hits);
    }

    /// <summary>
    /// 分類過濾在這一層就生效，被過濾掉的候選連算都不算。
    /// </summary>
    /// <remarks>
    /// 靠聚合器那道最後防線的話，勾掉九成分類的那一輪仍然要付十成的預算，
    /// 而預算用盡時被砍掉的是使用者真的要的那一成。
    /// </remarks>
    [Fact]
    public async Task 分類過濾在provider就生效而且不算進預算()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithObject(2, "dbo", "Lib_Tag", "V")
            .WithColumn(1, "CopyNo");

        var sink = await RunAsync(
            server, new SearchQuery("Lib_Tag", categories: new[] { "catalog.view" }));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("catalog.view", hit.CategoryId);

        // 只有那一個檢視被檢查過：資料表與資料行整組跳過。
        Assert.Equal(1, sink.Examined);
    }

    /// <summary>
    /// <see cref="ISearchSink.TryReport"/> 回 false 之後不再往下掃。
    /// </summary>
    /// <remarks>
    /// 只看結果筆數分不出來：多推的那幾筆會被靜靜丟掉，而畫面上一模一樣。
    /// 分得出來的是檢查過的候選數。
    /// </remarks>
    [Fact]
    public async Task 被叫停之後立刻停止掃描()
    {
        var server = new FakeCatalogServer();
        var database = server.Add("Library");

        for (var index = 0; index < 50; index++)
        {
            database.WithObject(index + 1, "dbo", $"Loan{index:D2}", "U");
        }

        var sink = await RunAsync(server, new SearchQuery("Loan"), acceptLimit: 1);

        Assert.Single(sink.Hits);
        Assert.Equal(2, sink.Reports);
        Assert.Equal(2, sink.Examined);
        Assert.True(sink.IsTruncated);
        Assert.Equal("[Library].[dbo].[Loan01]", sink.Checkpoint);
    }

    [Fact]
    public async Task 取消時擲出取消例外()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var provider = new SqlCatalogSearchProvider(server.SourceFor("Library"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync(new SearchQuery("Loan"), new RecordingSearchSink(), cancellation.Token));
    }

    /// <summary>
    /// 一個資料庫失敗不讓其他資料庫的結果消失，而且失敗不進快取。
    /// </summary>
    [Fact]
    public async Task 一個資料庫失敗不影響其他資料庫()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");
        server.Add("LibArchive").WithObject(1, "dbo", "Loan", "U").FailsOnOpen = true;

        var cache = new SqlCatalogSearchIndexCache();
        var query = new SearchQuery("Loan", scope: new SearchScope(null, new[] { "Library", "LibArchive" }));

        var reported = SqlCatalogSearchIndexTests.Capture(() =>
            RunAsync(server, query, cache: cache).GetAwaiter().GetResult());

        // 這一段是為了在同一個 Capture 範圍內量到回報；結果由下面幾行檢查。
        Assert.NotEmpty(reported);

        var sink = await RunAsync(server, query, cache: cache);

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("Library", hit.Path!.DatabaseName);
        Assert.True(sink.IsTruncated);

        // 失敗不進快取：第二輪仍然重試那一個，成功的那一個則是快取命中。
        Assert.False(cache.TryGet(server.SourceFor("LibArchive").CacheKey, out _));
        Assert.True(cache.TryGet(server.SourceFor("Library").CacheKey, out _));
        Assert.Equal(3, cache.Builds);
    }

    /// <summary>
    /// 跨資料庫換目錄，查不到就沒有結果。
    /// </summary>
    /// <remarks>
    /// <b>絕不</b>退回拿目前連線裡同名的物件回答——那比什麼都不做糟，
    /// 什麼都不做至少是沉默。
    /// </remarks>
    [Fact]
    public async Task 指名的資料庫查不到時不退回本機同名物件()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var sink = await RunAsync(
            server,
            new SearchQuery("Loan", scope: new SearchScope(null, new[] { "LibArchive" })));

        Assert.Empty(sink.Hits);
        Assert.True(sink.IsTruncated);
    }

    [Fact]
    public async Task 指名別的資料庫時換目錄去查()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");
        server.Add("LibArchive").WithObject(1, "dbo", "LoanDetail", "U");

        var sink = await RunAsync(
            server,
            new SearchQuery("Loan", scope: new SearchScope(null, new[] { "LibArchive" })));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("[LibArchive].[dbo].[LoanDetail]", hit.DedupeKey);
    }

    /// <summary>
    /// v1 沒有連結伺服器的索引，指名伺服器時整輪不回結果，也不開連線。
    /// </summary>
    /// <remarks>
    /// 拿本機的東西當成對面那台的答案，正是跨伺服器那一條明文禁止的退回。
    /// </remarks>
    [Fact]
    public async Task 指名伺服器時不回結果也不連線()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var sink = await RunAsync(
            server,
            new SearchQuery("Loan", scope: new SearchScope(new[] { "LibMirror" }, null)));

        Assert.Empty(sink.Hits);
        Assert.Equal(0, server.Opened);
    }

    /// <summary>
    /// 定義本文收不完時，這一輪要說出來。
    /// </summary>
    /// <remarks>
    /// 使用者要看得出差別：安靜地少一半結果，看起來與「這個字串在這個資料庫裡
    /// 不存在」一模一樣。
    /// </remarks>
    [Fact]
    public async Task 本文收不完時這一輪標記為沒掃完()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT CopyNo FROM dbo.Loan")
            .WithObject(2, "dbo", "Lib_Reader", "V", "SELECT CopyNo FROM dbo.Branch");

        // 第一份就把上限用完，第二份只剩名稱。
        var cache = new SqlCatalogSearchIndexCache(maxDefinitionBytes: 54);
        var sink = await RunAsync(server, new SearchQuery("CopyNo"), cache: cache);

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("[dbo].[Lib_Tag]", hit.Title);
        Assert.True(sink.IsTruncated);
    }

    /// <summary>空輸入是「列一份預設清單」，不是把整個資料庫倒出來。</summary>
    [Fact]
    public async Task 空輸入只列物件不列資料行()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U", "SELECT CopyNo FROM dbo.Copy")
            .WithColumn(1, "CopyNo");

        var sink = await RunAsync(server, new SearchQuery(string.Empty));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal(SearchHitClass.Name, hit.HitClass);
        Assert.Equal("[dbo].[Loan]", hit.Title);
        Assert.False(sink.IsTruncated);
    }

    /// <summary>同一個資料庫只建一次索引；鍵走 <c>SqlConnectionCacheKey</c>。</summary>
    [Fact]
    public async Task 同一個資料庫的第二輪搜尋不重建索引()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var cache = new SqlCatalogSearchIndexCache();

        await RunAsync(server, new SearchQuery("Loan"), cache: cache);
        await RunAsync(server, new SearchQuery("Lib"), cache: cache);

        Assert.Equal(1, cache.Builds);
        Assert.Equal(1, server.Opened);
    }

    /// <summary>回報的分類一定出自自己宣告的那一份清單。</summary>
    [Fact]
    public async Task 回報的分類出自自己宣告的清單()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var provider = new SqlCatalogSearchProvider(server.SourceFor("Library"));
        var sink = new RecordingSearchSink();

        await provider.SearchAsync(new SearchQuery("o"), sink, CancellationToken.None);

        var declared = provider.Categories.Select(category => category.Id).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(sink.Hits);
        Assert.All(sink.Hits, hit =>
        {
            Assert.Equal(SqlCatalogSearchProvider.ProviderId, hit.ProviderId);
            Assert.Contains(hit.CategoryId, declared);
        });
    }

    private static void AssertSpansPointAtMatches(SearchHit hit, string text)
    {
        Assert.NotEmpty(hit.SnippetSpans);

        foreach (var span in hit.SnippetSpans)
        {
            Assert.Equal(
                text,
                hit.Snippet.Substring(span.Start, span.Length));
        }
    }

    private static async Task<SearchHit> SingleBodyHitAsync(
        string definition, string text, SearchOptions options = SearchOptions.None)
    {
        var sink = await RunAsync(NewBodyServer(definition), new SearchQuery(text, options: options));

        return Assert.Single(sink.Hits, hit => hit.HitClass == SearchHitClass.Body);
    }

    /// <summary>只有一個檢視、名稱不會被搜到，命中一定來自定義本文。</summary>
    private static FakeCatalogServer NewBodyServer(string definition)
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Lib_Tag", "V", definition);
        return server;
    }

    private static async Task<RecordingSearchSink> RunAsync(
        FakeCatalogServer server,
        SearchQuery query,
        int acceptLimit = int.MaxValue,
        SqlCatalogSearchIndexCache? cache = null,
        string databaseName = "Library")
    {
        var provider = new SqlCatalogSearchProvider(server.SourceFor(databaseName), cache);
        var sink = new RecordingSearchSink(acceptLimit);

        await provider.SearchAsync(query, sink, CancellationToken.None);

        return sink;
    }
}
