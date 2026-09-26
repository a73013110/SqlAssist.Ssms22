using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 目錄物件搜尋：比對、片段、兩條軸的過濾、預算與失敗降級。
/// </summary>
/// <remarks>
/// 一律不連資料庫，走 <see cref="FakeCatalogServer"/>。
/// </remarks>
[Collection(MetadataFailureCollection.Name)]
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
        Assert.Equal(SearchMatchTarget.Name, scattered.MatchTarget);
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

    /// <summary>
    /// 資料行命中掛在<b>它所屬物件</b>的分類上，差別在命中部位。
    /// </summary>
    /// <remarks>
    /// 做成自己一種分類的症狀是勾「只看資料表」時，資料表上的資料行命中整組消失——
    /// 而那一勾要的正是它。
    /// </remarks>
    [Fact]
    public async Task 資料行命中掛在所屬物件的分類上()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(7, "dbo", "PUBLISHER", "U")
            .WithColumn(7, "PUBL_CODE");

        var sink = await RunAsync(server, new SearchQuery("PUBL_CODE"));

        var hit = Assert.Single(sink.Hits, h => h.MatchTarget == SearchMatchTarget.Column);

        Assert.Equal("catalog.table", hit.CategoryId);

        // 標題是物件的限定名稱，不接資料行那一段：聚合器會把同一張表的幾個資料行命中
        // 併成一列，而那一列的抬頭不該是其中隨便一行的名字。命中的是哪幾行由片段回答。
        Assert.Equal("[dbo].[PUBLISHER]", hit.Title);
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

    /// <summary>勾「只看資料表」時，資料表上的資料行命中留著。</summary>
    [Fact]
    public async Task 只看資料表時資料行命中仍然在()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "PUBLISHER", "U")
            .WithObject(2, "dbo", "Lib_Tag", "V")
            .WithColumn(1, "PUBL_CODE")
            .WithColumn(2, "PUBL_CODE");

        var sink = await RunAsync(
            server, new SearchQuery("PUBL_CODE", categories: new[] { "catalog.table" }));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal(SearchMatchTarget.Column, hit.MatchTarget);
        Assert.Equal("[dbo].[PUBLISHER]", hit.Title);
        Assert.Equal("PUBL_CODE", hit.Snippet);
    }

    /// <summary>每一筆結果帶著「這是哪一個資料庫的」膠囊。</summary>
    /// <remarks>
    /// 膠囊由 provider 給，字串是中性代號，不是 SSMS 的圖示型別——Core 認識那個型別
    /// 等於把 VS 組件拉進 netstandard2.0 那一層。
    /// </remarks>
    [Fact]
    public async Task 命中帶著資料庫膠囊()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var sink = await RunAsync(server, new SearchQuery("Loan"));

        var badge = Assert.Single(Assert.Single(sink.Hits).Badges);
        Assert.Equal("Library", badge.Text);
        Assert.Equal(SearchBadge.DatabaseIcon, badge.IconToken);
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
    public async Task 資料行的去重鍵是它所屬物件那一份()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithColumn(1, "CopyNo")
            .WithColumn(1, "CopyNote");

        var sink = await RunAsync(server, new SearchQuery("CopyNo"));

        // 兩個資料行命中的是同一張表；鍵相同，聚合器才併得起來。接了資料行名稱的那一版
        // 讓同一張表在清單上出現兩列，而兩列點下去做同一件事。
        Assert.Equal(2, sink.Hits.Count);
        Assert.All(sink.Hits, hit => Assert.Equal("[Library].[dbo].[Loan]", hit.DedupeKey));
    }

    /// <summary>
    /// 同一個物件的名稱與本文命中寫出<b>同一個</b>去重鍵。
    /// </summary>
    /// <remarks>
    /// 聚合器靠這個鍵把兩筆併成一列；兩邊寫出不同的鍵，清單上就是重複的兩行，
    /// 而它們指向同一個地方、點下去做同一件事。
    /// </remarks>
    [Fact]
    public async Task 同一個物件的兩種命中共用去重鍵()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "V", "SELECT 1 FROM dbo.Loan;");

        var sink = await RunAsync(server, new SearchQuery("Loan"));

        Assert.Equal(
            new[] { SearchMatchTarget.Name, SearchMatchTarget.Text },
            sink.Hits.Select(hit => hit.MatchTarget));
        Assert.Single(sink.Hits.Select(hit => hit.DedupeKey).Distinct(StringComparer.Ordinal));
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
        Assert.Equal(SearchMatchTarget.Text, hit.MatchTarget);
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

    /// <summary>
    /// 條件約束的運算式也搜得到。
    /// </summary>
    /// <remarks>
    /// 使用者問的是「哪一條規則提到這個欄位」，而那句話只寫在
    /// <c>sys.check_constraints.definition</c> 上，不在 <c>sys.sql_modules</c> 裡。
    /// </remarks>
    [Fact]
    public async Task 條件約束的運算式搜得到()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithObject(11, "dbo", "CK_Loan_CopyNo", "C", "([CopyNo]>(0))");

        var sink = await RunAsync(server, new SearchQuery("CopyNo"));

        var hit = Assert.Single(sink.Hits, h => h.MatchTarget == SearchMatchTarget.Text);
        Assert.Equal(SqlCatalogSearchCategories.ConstraintCategoryId, hit.CategoryId);
        Assert.Equal("[dbo].[CK_Loan_CopyNo]", hit.Title);
    }

    /// <summary>序列、同義字與資料表型別各自掛在自己的分類上。</summary>
    [Fact]
    public async Task 序列同義字與資料表型別各有分類()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan_Seq", "SO")
            .WithObject(2, "dbo", "Loan_Syn", "SN")
            .WithObject(3, "dbo", "Loan_Type", "TT");

        var sink = await RunAsync(server, new SearchQuery("Loan"));

        Assert.Equal(
            new[] { "catalog.sequence", "catalog.synonym", "catalog.table-type" },
            sink.Hits.Select(hit => hit.CategoryId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task 區分大小寫時本文只收逐字相同的命中()
    {
        var server = NewBodyServer("SELECT CopyNo FROM dbo.Loan");

        var sensitive = await RunAsync(server, new SearchQuery("copyno", options: TextMatchOptions.MatchCasing));
        Assert.Empty(sensitive.Hits);

        var insensitive = await RunAsync(NewBodyServer("SELECT CopyNo FROM dbo.Loan"), new SearchQuery("copyno"));
        Assert.Single(insensitive.Hits);
    }

    /// <remarks>
    /// 一個修飾都沒開才走模糊比對：識別字在多數定序下本來就不分大小寫，
    /// 而打字找東西的人不會先想好大小寫。
    /// </remarks>
    [Fact]
    public async Task 沒開修飾時名稱不分大小寫()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "PUBLISHER", "U");

        var sink = await RunAsync(server, new SearchQuery("publisher"));

        Assert.Single(sink.Hits);
    }

    /// <summary>勾了大小寫之後，名稱與本文同一條規則：逐字相同才收。</summary>
    [Fact]
    public async Task 區分大小寫時名稱只收逐字相同的命中()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "PUBLISHER", "U");

        var sink = await RunAsync(server, new SearchQuery("publisher", options: TextMatchOptions.MatchCasing));

        Assert.Empty(sink.Hits);
    }

    /// <summary>資料行與物件名稱同一條規則；只改其中一處的症狀是同一個字串在兩個部位上收的筆數不一樣。</summary>
    [Fact]
    public async Task 只取整個字時資料行也照同一條規則()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithColumn(1, "CopyNote");

        var sink = await RunAsync(server, new SearchQuery("CopyNo", options: TextMatchOptions.WholeWord));

        Assert.Empty(sink.Hits);
    }

    /// <summary>
    /// 兩顆修飾都開著時，名稱不再收「字母湊得出來」的那一種命中。
    /// </summary>
    /// <remarks>
    /// 模糊比對允許字母散在候選各處，<c>DF_Lib_Reader_NoticeIsShown</c> 確實湊得出
    /// <c>finish</c> 的每一個字母。使用者把兩顆都開著、範圍也縮到只剩條件約束，卻仍然
    /// 看到這一筆——他關掉的東西一個都沒關掉。
    /// </remarks>
    [Fact]
    public async Task 開了修飾的名稱不再收散開的模糊命中()
    {
        var fuzzy = await RunAsync(NewConstraintServer(), new SearchQuery("finish"));
        Assert.Single(fuzzy.Hits);

        var strict = await RunAsync(
            NewConstraintServer(),
            new SearchQuery("finish", options: TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord));

        Assert.Empty(strict.Hits);
    }

    private static FakeCatalogServer NewConstraintServer()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "DF_Lib_Reader_NoticeIsShown", "D");
        return server;
    }

    /// <summary>
    /// 字面比對找的是「出現在名稱裡」，不是「整個名稱就是它」；<b>詞界</b>才是那個更嚴的條件。
    /// </summary>
    /// <remarks>
    /// 底線算識別字的一部分（與定義本文那一邊同一條規則），所以 <c>DF_Loan_CopyNo</c> 在
    /// 只勾大小寫時收得到、再勾整個字就收不到——與編輯器「全字」的慣例相同。
    /// </remarks>
    [Fact]
    public async Task 名稱的字面命中可以落在名稱中間()
    {
        var casing = await RunAsync(
            NewNamedServer("DF_Loan_CopyNo"), new SearchQuery("CopyNo", options: TextMatchOptions.MatchCasing));

        var hit = Assert.Single(casing.Hits);
        var span = Assert.Single(hit.SnippetSpans);

        // 高亮標的是他打的那個字整段，不是湊得出那個字的幾個字母。
        Assert.Equal("DF_Loan_CopyNo", hit.Snippet);
        Assert.Equal(8, span.Start);
        Assert.Equal(6, span.Length);

        var wholeWord = await RunAsync(
            NewNamedServer("DF_Loan_CopyNo"), new SearchQuery("CopyNo", options: TextMatchOptions.WholeWord));

        Assert.Empty(wholeWord.Hits);

        var exact = await RunAsync(
            NewNamedServer("CopyNo"), new SearchQuery("CopyNo", options: TextMatchOptions.WholeWord));

        Assert.Single(exact.Hits);
    }

    private static FakeCatalogServer NewNamedServer(string name)
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", name, "U");
        return server;
    }

    [Fact]
    public async Task 只取整個字時不收黏在識別字裡的命中()
    {
        var wholeWord = await RunAsync(
            NewBodyServer("SELECT CopyNoTotal FROM dbo.Loan"),
            new SearchQuery("CopyNo", options: TextMatchOptions.WholeWord));

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
            new SearchQuery("CopyNo", options: TextMatchOptions.WholeWord));

        Assert.Single(sink.Hits);
    }

    /// <summary>
    /// 不搜定義本文的那一輪，第二段查詢<b>連送都不送</b>。
    /// </summary>
    /// <remarks>
    /// 撈回來再丟掉的話，第一次搜尋最貴的那一段一毫秒都沒有省到，而使用者以為自己
    /// 關掉了它。分得出兩者的只有「那一條查詢有沒有被執行」，不是結果筆數。
    /// </remarks>
    [Fact]
    public async Task 不搜本文的那一輪不撈定義本文()
    {
        var server = NewBodyServer("SELECT CopyNo FROM dbo.Loan");

        var sink = await RunAsync(
            server,
            new SearchQuery("CopyNo", targets: SearchTargets.Name | SearchTargets.Column));

        Assert.Empty(sink.Hits);
        Assert.Equal(0, server.CountCommands("sys.sql_modules"));
    }

    /// <summary>只搜名稱時連資料行都不比對。</summary>
    [Fact]
    public async Task 只搜名稱時不比對資料行()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "PUBLISHER", "U")
            .WithColumn(1, "PUBL_CODE");

        var sink = await RunAsync(server, new SearchQuery("PUBL_CODE", targets: SearchTargets.Name));

        Assert.Empty(sink.Hits);

        // 名稱那一段掃完了，只是沒有一個物件叫這個名字：這一輪是完整的「沒有」。
        Assert.True(sink.IsComplete);
    }

    /// <summary>之後才勾上定義本文時只補第二段，第一段不重掃。</summary>
    [Fact]
    public async Task 補搜本文時只補第二段()
    {
        var server = NewBodyServer("SELECT CopyNo FROM dbo.Loan");
        var cache = new SqlCatalogSearchIndexCache();

        // 指名那一個資料庫：「全部」每一輪會多問一次清單，這裡量的是索引那幾條。
        await RunAsync(server, new SearchQuery("CopyNo", targets: SearchTargets.Name, scope: LibraryOnly), cache: cache);
        server.Commands.Clear();

        var sink = await RunAsync(server, new SearchQuery("CopyNo", 1, scope: LibraryOnly), cache: cache);

        Assert.Single(sink.Hits);
        Assert.Equal(1, server.CountCommands("sys.sql_modules"));
        Assert.Single(server.Commands);
    }

    /// <summary>分類過濾在這一層就生效，不靠聚合器那道最後防線。</summary>
    [Fact]
    public async Task 分類過濾在provider就生效()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithObject(2, "dbo", "Lib_Tag", "V")
            .WithColumn(1, "Lib_TagNo");

        var sink = await RunAsync(
            server, new SearchQuery("Lib_Tag", categories: new[] { "catalog.view" }));

        // 資料表的資料行名稱也對得上，但資料表不在這一輪的分類裡。
        var hit = Assert.Single(sink.Hits);
        Assert.Equal("catalog.view", hit.CategoryId);
    }

    /// <summary>分類把整個來源排除時不宣告目標、不建索引：這一輪本來就不必搜它。</summary>
    [Fact]
    public async Task 分類排除整個來源時不宣告目標也不連線()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();

        var sink = await RunAsync(server, new SearchQuery("Loan", categories: new[] { "agent-job.job" }));

        Assert.Empty(sink.Targets);
        Assert.Equal(0, server.Opened);
    }

    /// <summary>
    /// <see cref="ISearchSink.TryReport"/> 回 false 之後不再往下掃，也不說自己比完了。
    /// </summary>
    /// <remarks>
    /// 只看結果筆數分不出來：多推的那幾筆會被靜靜丟掉，而畫面上一模一樣。
    /// 分得出來的是被推了幾次。
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

        // 沒比完的目標不說結局；聚合器收尾時把它記成已取消，而不是完整。
        Assert.Equal(SearchTargetState.Running, sink.Target("Library").State);
    }

    /// <summary>
    /// 名稱、資料行與本文三種命中都帶著搜到它的那一台。
    /// </summary>
    /// <remarks>
    /// 清單比範圍活得久：換了查詢視窗之後，舊列點下去要找的仍是這一台。少帶一種的症狀
    /// 只在那一種命中上發作，而導航會拿換過之後那一台的同名物件回答。
    /// </remarks>
    [Fact]
    public async Task 三種命中都帶著搜到它的那一台()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithColumn(1, "LoanNo")
            .WithObject(2, "dbo", "Lib_Tag", "V", "SELECT LoanNo FROM dbo.Loan;");
        var origin = new SqlSearchOrigin("LIBSQL02");
        var provider = new SqlCatalogSearchProvider(server.SourceFor("Library"), origin);
        var sink = new RecordingSearchSink();

        await provider.SearchAsync(new SearchQuery("Loan"), sink, CancellationToken.None);

        Assert.Contains(sink.Hits, hit => hit.MatchTarget == SearchMatchTarget.Name);
        Assert.Contains(sink.Hits, hit => hit.MatchTarget == SearchMatchTarget.Column);
        Assert.Contains(sink.Hits, hit => hit.MatchTarget == SearchMatchTarget.Text);
        Assert.All(
            sink.Hits,
            hit => Assert.Same(origin, Assert.IsType<SqlCatalogSearchTarget>(hit.ActivatePayload).Origin));
    }

    [Fact]
    public void 沒有伺服器就不建立來源()
    {
        var server = new FakeCatalogServer();
        server.Add("Library");

        Assert.Throws<ArgumentNullException>(() => new SqlCatalogSearchProvider(server.SourceFor("Library"), null!));
    }

    [Fact]
    public async Task 取消時擲出取消例外()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var provider = new SqlCatalogSearchProvider(server.SourceFor("Library"), Origin);
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

        // 失敗回報指得出是哪一個資料庫，而且只有那一個。
        var line = Assert.Single(reported);
        Assert.Contains("LibArchive", line);

        var sink = await RunAsync(server, query, cache: cache);

        var hit = Assert.Single(sink.Hits);
        Assert.Equal("Library", hit.Path!.DatabaseName);

        // 連不上的那一個是「讀不到」，掃得完的那一個掃完了；兩者各自是一個目標。
        Assert.Equal(SearchTargetState.Complete, sink.Target("Library").State);
        var archive = sink.Target("LibArchive");
        Assert.Equal(SearchTargetState.Unavailable, archive.State);

        // 連不上沒有權限錯誤碼，所以種類說不出來。
        Assert.Equal(SearchUnavailableKind.Unknown, archive.UnavailableKind);

        // 失敗不進快取：第二輪仍然重試那一個，成功的那一個則是快取命中。
        Assert.False(cache.TryGet(server.SourceFor("LibArchive").CacheKey, out _));
        Assert.True(cache.TryGet(server.SourceFor("Library").CacheKey, out _));
        Assert.Equal(3, cache.Builds);
    }

    /// <summary>
    /// 伺服器給了權限錯誤碼時，種類與句子一起換成斷言。
    /// </summary>
    /// <remarks>
    /// 沒有這一條的話，「權限不足」那個抬頭永遠沒有生產者——狀態表面上那一支
    /// <c>SqlSurfaceState.Denied</c> 寫了也走不到。
    /// </remarks>
    [Fact]
    public async Task 權限錯誤碼讓這一輪說得出就是權限()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var archive = server.Add("LibArchive");
        archive.FailsOnOpen = true;
        archive.DeniesAccess = true;

        var query = new SearchQuery("Loan", scope: new SearchScope(null, new[] { "Library", "LibArchive" }));

        RecordingSearchSink? sink = null;
        SqlCatalogSearchIndexTests.Capture(() => sink = RunAsync(server, query).GetAwaiter().GetResult());

        var denied = sink!.Target("LibArchive");
        Assert.Equal(SearchTargetState.Unavailable, denied.State);
        Assert.Equal(SearchUnavailableKind.Denied, denied.UnavailableKind);

        // 讀得到的那一個照樣回得來。
        Assert.Single(sink.Hits);
    }

    /// <summary>
    /// 一個沒權限、另一個連不上：各自說各自的，不合成一句。
    /// </summary>
    /// <remarks>
    /// 合成一句的那一版要在「權限」與「說不出來」之間選一個，而選錯的那一次，使用者去要了權限，
    /// 連不上的那個下一輪還是讀不到。每個目標各有結局之後，這個取捨不存在了。
    /// 幾個資料庫是平行掃的，所以跑十次確定結局不由賽跑決定。
    /// </remarks>
    [Fact]
    public async Task 每個資料庫各自說出讀不到的原因()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var archive = server.Add("LibArchive");
        archive.FailsOnOpen = true;
        archive.DeniesAccess = true;

        server.Add("LibMirror").FailsOnOpen = true;

        var query = new SearchQuery(
            "Loan", scope: new SearchScope(null, new[] { "Library", "LibArchive", "LibMirror" }));

        for (var round = 0; round < 10; round++)
        {
            RecordingSearchSink? sink = null;
            SqlCatalogSearchIndexTests.Capture(() => sink = RunAsync(server, query).GetAwaiter().GetResult());

            Assert.Equal(SearchTargetState.Complete, sink!.Target("Library").State);
            Assert.Equal(SearchUnavailableKind.Denied, sink.Target("LibArchive").UnavailableKind);
            Assert.Equal(SearchUnavailableKind.Unknown, sink.Target("LibMirror").UnavailableKind);
        }
    }

    /// <summary>
    /// 沒有指名資料庫就是「全部」：這台上進得去的每一個都搜，不是只搜連線那一個。
    /// </summary>
    [Fact]
    public async Task 沒有指名資料庫時搜這台伺服器上的每一個()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");
        server.Add("LibArchive").WithObject(1, "dbo", "LoanDetail", "U");
        server.Add("master", isSystem: true);

        var sink = await RunAsync(server, new SearchQuery("Loan"), databaseName: "master");

        Assert.Equal(
            new[] { "[LibArchive].[dbo].[LoanDetail]", "[Library].[dbo].[Loan]" },
            sink.Hits.Select(hit => hit.DedupeKey).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(new[] { "LibArchive", "Library", "master" }, sink.Targets.Select(target => target.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.True(sink.IsComplete);
    }

    /// <summary>
    /// 「全部」裡進不去的資料庫照樣列進完整度，而且不去開它。
    /// </summary>
    /// <remarks>
    /// 直接略過的話，畫面會說「已完整搜尋」，而少了使用者以為有搜的那幾個。
    /// 離線的寫伺服器說的狀態；在線上卻進不去的就是權限。
    /// </remarks>
    [Fact]
    public async Task 全部裡進不去的資料庫列進完整度而不開連線()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");
        var archive = server.Add("LibArchive");
        archive.State = "OFFLINE";
        archive.IsAccessible = false;
        archive.FailsOnOpen = true;
        var mirror = server.Add("LibMirror");
        mirror.IsAccessible = false;
        mirror.FailsOnOpen = true;

        var sink = await RunAsync(server, new SearchQuery("Loan"));

        Assert.Single(sink.Hits);
        Assert.Equal(SearchTargetState.Complete, sink.Target("Library").State);

        var offline = sink.Target("LibArchive");
        Assert.Equal(SearchTargetState.Unavailable, offline.State);
        Assert.Equal(SearchUnavailableKind.Unknown, offline.UnavailableKind);
        Assert.Equal("OFFLINE", offline.Detail);

        var denied = sink.Target("LibMirror");
        Assert.Equal(SearchUnavailableKind.Denied, denied.UnavailableKind);
        Assert.Null(denied.Detail);

        // 問清單一次、Library 一次；進不去的兩個不開。
        Assert.Equal(2, server.Opened);
        Assert.False(sink.IsComplete);
    }

    /// <summary>
    /// 「全部」而清單問不到：照實說這一輪沒搜，不退回只搜連線那一個——那一份答案看起來完全正常，
    /// 只是少了使用者以為有搜的其他資料庫。
    /// </summary>
    [Fact]
    public async Task 全部而問不到資料庫清單時照實說而不退回連線那一個()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var provider = new SqlCatalogSearchProvider(server.SourceFor("Library"), Origin, listDatabases: (_, _) => null);
        var sink = new RecordingSearchSink();
        await provider.SearchAsync(new SearchQuery("Loan"), sink, CancellationToken.None);

        Assert.Empty(sink.Hits);
        var target = Assert.Single(sink.Targets);
        Assert.Equal(SearchTargetKind.Source, target.Kind);
        Assert.Equal(SearchTargetState.Unavailable, target.State);
        Assert.Contains("資料庫清單", target.Detail);
    }

    /// <summary>幾個資料庫的結果併在同一輪裡回來，各自帶著自己的資料庫膠囊。</summary>
    [Fact]
    public async Task 多個資料庫的結果都回得來()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");
        server.Add("LibArchive").WithObject(1, "dbo", "LoanDetail", "U");

        var sink = await RunAsync(
            server,
            new SearchQuery("Loan", scope: new SearchScope(null, new[] { "Library", "LibArchive" })));

        Assert.Equal(
            new[] { "[LibArchive].[dbo].[LoanDetail]", "[Library].[dbo].[Loan]" },
            sink.Hits.Select(hit => hit.DedupeKey).OrderBy(key => key, StringComparer.Ordinal));
        Assert.True(sink.IsComplete);
    }

    /// <summary>同一個名稱指名兩次只掃一次；去重是聚合器那一端付的錢。</summary>
    [Fact]
    public async Task 指名重複的資料庫只掃一次()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var sink = await RunAsync(
            server,
            new SearchQuery("Loan", scope: new SearchScope(null, new[] { "Library", "library" })));

        Assert.Single(sink.Hits);
        Assert.Single(sink.Targets);
        Assert.Equal(1, server.Opened);
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
        Assert.Equal(SearchTargetState.Unavailable, sink.Target("LibArchive").State);
    }

    /// <summary>
    /// 目標照指名的順序宣告；幾個資料庫平行掃，順序不由誰先回來決定。
    /// </summary>
    [Fact]
    public async Task 目標照指名的順序宣告()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");

        var query = new SearchQuery(
            "Loan", scope: new SearchScope(null, new[] { "LibArchive", "LibMirror", "Library" }));

        for (var round = 0; round < 10; round++)
        {
            var sink = await RunAsync(server, query);

            Assert.Single(sink.Hits);
            Assert.Equal(new[] { "LibArchive", "LibMirror", "Library" }, sink.Targets.Select(target => target.Name));
            Assert.Equal(
                new[] { SearchTargetState.Unavailable, SearchTargetState.Unavailable, SearchTargetState.Complete },
                sink.Targets.Select(target => target.State));
        }
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
    /// 沒有連結伺服器的索引，指名伺服器時整輪不回結果，也不開連線。
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
    /// 定義本文超過上限時改由伺服器端比對，一筆都不漏。
    /// </summary>
    /// <remarks>
    /// 原本的作法是只留前面那一部分並說「本文只掃到一部分」；使用者照樣拿清單當答案。
    /// 記憶體是快取，比對的完整度不能跟著它打折。
    /// </remarks>
    [Fact]
    public async Task 本文超過上限時改由伺服器端比對而不漏()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT CopyNo FROM dbo.Loan")
            .WithObject(2, "dbo", "Lib_Reader", "V", "SELECT copyno FROM dbo.Branch");

        // 第一份就把上限用完：這個資料庫的本文整份改到伺服器端。
        var cache = new SqlCatalogSearchIndexCache(maxDefinitionBytes: 54);
        var sink = await RunAsync(server, new SearchQuery("CopyNo", scope: LibraryOnly), cache: cache);

        Assert.Equal(
            new[] { "[dbo].[Lib_Reader]", "[dbo].[Lib_Tag]" },
            sink.Hits.Select(hit => hit.Title).OrderBy(title => title, StringComparer.Ordinal));
        Assert.True(sink.IsComplete);
        Assert.Equal(1, server.CountCommands(SqlCatalogSearchQueries.PatternParameterName));

        // 伺服器只做粗篩，比對規則由讀取端照使用者的修飾再比一次：開了大小寫就只剩逐字相同的那一個。
        var exact = await RunAsync(
            server, new SearchQuery("CopyNo", 1, TextMatchOptions.MatchCasing, scope: LibraryOnly), cache: cache);
        Assert.Equal("[dbo].[Lib_Tag]", Assert.Single(exact.Hits, hit => hit.MatchTarget == SearchMatchTarget.Text).Title);
    }

    /// <summary>
    /// 讀不到的本文（加密、沒有 VIEW DEFINITION）要數出來，而且只數這一輪的分類。
    /// </summary>
    /// <remarks>
    /// 略過的症狀是搜 <c>sp_executesql</c> 時一個加密的預存程序安靜地不出現，而畫面說「沒有相符項目」。
    /// </remarks>
    [Fact]
    public async Task 讀不到的本文數得出來而且只數這一輪的分類()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT CopyNo FROM dbo.Loan")
            .WithObject(2, "dbo", "Lib_Reader", "P", "EXEC sp_executesql N'SELECT 1'")
            .Encrypt(2);

        var cache = new SqlCatalogSearchIndexCache();
        var all = await RunAsync(server, new SearchQuery("sp_executesql", scope: LibraryOnly), cache: cache);

        Assert.Empty(all.Hits);
        Assert.Equal(1, all.Target("Library").UnreadableText);
        Assert.False(all.IsComplete);

        var viewsOnly = await RunAsync(
            server, new SearchQuery("sp_executesql", 1, categories: new[] { "catalog.view" }, scope: LibraryOnly), cache: cache);

        Assert.Equal(0, viewsOnly.Target("Library").UnreadableText);
        Assert.True(viewsOnly.IsComplete);
    }

    /// <summary>本文在伺服器端比對時，讀不到的本文照樣數得出來。</summary>
    [Fact]
    public async Task 伺服器端比對也數得出讀不到的本文()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT CopyNo FROM dbo.Loan")
            .WithObject(2, "dbo", "Lib_Reader", "P", "EXEC sp_executesql N'SELECT CopyNo'")
            .Encrypt(2);

        var cache = new SqlCatalogSearchIndexCache(maxDefinitionBytes: 10);
        var sink = await RunAsync(server, new SearchQuery("CopyNo", scope: LibraryOnly), cache: cache);

        Assert.Equal("[dbo].[Lib_Tag]", Assert.Single(sink.Hits).Title);
        Assert.Equal(1, sink.Target("Library").UnreadableText);
    }

    /// <summary>伺服器端比對失敗時，名稱照樣算數，但這個資料庫的本文要說沒比完。</summary>
    [Fact]
    public async Task 伺服器端比對失敗時說本文沒比完()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "CopyNoMap", "V", "SELECT CopyNo FROM dbo.Loan");

        var provider = new SqlCatalogSearchProvider(
            server.SourceFor("Library"), Origin, new SqlCatalogSearchIndexCache(maxDefinitionBytes: 10),
            findOnServer: (_, _, _) => null);
        var sink = new RecordingSearchSink();

        await provider.SearchAsync(new SearchQuery("CopyNo", scope: LibraryOnly), sink, CancellationToken.None);

        Assert.Equal("[dbo].[CopyNoMap]", Assert.Single(sink.Hits).Title);
        var target = sink.Target("Library");
        Assert.Equal(SearchTargetState.Complete, target.State);
        Assert.True(target.IsTextIncomplete);
        Assert.False(sink.IsComplete);
    }

    /// <summary>空輸入是「列一份預設清單」，不是把整個資料庫倒出來。</summary>
    [Fact]
    public async Task 空輸入只列物件不列資料行也不建第二段()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U", "SELECT CopyNo FROM dbo.Copy")
            .WithColumn(1, "CopyNo");

        var sink = await RunAsync(server, new SearchQuery(string.Empty));

        var hit = Assert.Single(sink.Hits);
        Assert.Equal(SearchMatchTarget.Name, hit.MatchTarget);
        Assert.Equal("[dbo].[Loan]", hit.Title);
        Assert.True(sink.IsComplete);

        // 使用者只是打開了視窗，還沒說要搜什麼；本文那一段連撈都不必撈。
        Assert.Equal(0, server.CountCommands("sys.sql_modules"));
    }

    /// <summary>同一個資料庫只建一次索引；鍵走 <c>SqlConnectionCacheKey</c>。</summary>
    [Fact]
    public async Task 同一個資料庫的第二輪搜尋不重建索引()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var cache = new SqlCatalogSearchIndexCache();

        // 指名那一個資料庫：「全部」每一輪會多開一條連線問清單，這裡量的是索引。
        await RunAsync(server, new SearchQuery("Loan", scope: LibraryOnly), cache: cache);
        await RunAsync(server, new SearchQuery("Lib", scope: LibraryOnly), cache: cache);

        Assert.Equal(1, cache.Builds);
        Assert.Equal(1, server.Opened);
    }

    /// <summary>回報的分類一定出自自己宣告的那一份清單。</summary>
    [Fact]
    public async Task 回報的分類出自自己宣告的清單()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var provider = new SqlCatalogSearchProvider(server.SourceFor("Library"), Origin);
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
        string definition, string text, TextMatchOptions options = TextMatchOptions.None)
    {
        var sink = await RunAsync(NewBodyServer(definition), new SearchQuery(text, options: options));

        return Assert.Single(sink.Hits, hit => hit.MatchTarget == SearchMatchTarget.Text);
    }

    /// <summary>只有一個檢視、名稱不會被搜到，命中一定來自定義本文。</summary>
    private static FakeCatalogServer NewBodyServer(string definition)
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Lib_Tag", "V", definition);
        return server;
    }

    private static readonly SqlSearchOrigin Origin = new("LIBSQL01");

    private static readonly SearchScope LibraryOnly = new(null, new[] { "Library" });

    private static async Task<RecordingSearchSink> RunAsync(
        FakeCatalogServer server,
        SearchQuery query,
        int acceptLimit = int.MaxValue,
        SqlCatalogSearchIndexCache? cache = null,
        string databaseName = "Library")
    {
        var provider = new SqlCatalogSearchProvider(server.SourceFor(databaseName), Origin, cache);
        var sink = new RecordingSearchSink(acceptLimit);

        await provider.SearchAsync(query, sink, CancellationToken.None);

        return sink;
    }
}
