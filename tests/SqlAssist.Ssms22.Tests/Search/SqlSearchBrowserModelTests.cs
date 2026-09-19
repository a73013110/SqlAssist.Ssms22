using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;
using SqlAssist.Ssms22.Search;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

public sealed class SqlSearchBrowserModelTests
{
    [Fact]
    public void 過濾選項由聚合器宣告的分類產生並去重()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table"),
            // 第二個來源的分類直接接在後面；UI 一個字串都不寫死。
            new StubProvider("memory", "memory.favorite", "Favorite"),
        });

        var options = SqlSearchBrowserModel.CategoryOptions(aggregator.Categories);

        // 沒有「全部」這一項：多選的空集合就是全部，多一顆互斥的「全部」等於兩種語意放在同一份清單。
        Assert.Equal(new[] { "Table", "Favorite" }, options.Select(option => option.Label).ToArray());
        Assert.Equal(new[] { "catalog.table", "memory.favorite" }, options.Select(option => option.Id).ToArray());
    }

    [Fact]
    public void 相同分類Id只產生一個選項()
    {
        var options = SqlSearchBrowserModel.CategoryOptions(new[]
        {
            new SearchCategory("catalog", "catalog.table", "Table"),
            new SearchCategory("memory", "catalog.table", "資料表"),
        });

        Assert.Single(options);
        Assert.Equal("Table", options[0].Label);
    }

    [Fact]
    public void 每一輪世代遞增且落後的結果整份丟棄()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", "Loan") });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loa" };

        var first = model.Begin(indexed: true)!;
        var firstResults = Search(aggregator, first.Query);

        model.Text = "Loan";
        var second = model.Begin(indexed: true)!;
        var secondResults = Search(aggregator, second.Query);

        Assert.Equal(first.Generation + 1, second.Generation);
        // 世代落後的那一份即使已經算完也不得蓋掉新的清單。
        Assert.False(model.Accept(first, firstResults));
        Assert.True(model.Accept(second, secondResults));
    }

    [Fact]
    public void 聚合器判定過期的結果不被採用()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", "Loan") });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var stale = model.Begin(indexed: true)!;
        var fresh = model.Begin(indexed: true)!;

        // 先跑新的一輪，讓聚合器把舊世代標成過期。
        Search(aggregator, fresh.Query);
        var staleResults = Search(aggregator, stale.Query);

        Assert.True(staleResults.IsStale);
        Assert.False(model.Accept(stale, staleResults));
    }

    [Fact]
    public void 沒有連線時不開始任何一輪()
    {
        var model = new SqlSearchBrowserModel { Text = "Loan" };

        Assert.Null(model.Begin(indexed: true));
        Assert.False(model.IsRunning);
        Assert.Equal(
            "尚未連線。在 SQL 查詢視窗連上資料庫，或在物件總管連上伺服器之後，這裡才有東西可以搜。",
            model.EmptyState(0));
        Assert.Equal("", model.Status());
    }

    [Fact]
    public void 指名的伺服器連不上時說的是那一台而不是叫人去開查詢視窗()
    {
        // 指名了伺服器卻沒有目錄，是那一台斷了；叫使用者去開查詢視窗只會讓他做一件
        // 解決不了的事，而他真正要做的是換一台或回到查詢視窗。
        var model = new SqlSearchBrowserModel { Text = "Loan", Server = "LIBSQL01" };

        Assert.Equal("連不上 LIBSQL01。物件總管上那一台可能已經中斷，換一台或回到查詢視窗。", model.EmptyState(0));
    }

    [Fact]
    public void 去彈跳期間維持待送出狀態直到這一輪真的開始()
    {
        var model = new SqlSearchBrowserModel { HasConnection = true };

        model.Text = "Loan";
        model.Invalidate();
        Assert.True(model.IsPending);
        Assert.False(model.IsRunning);
        // 還沒有結果就不能先說「沒有相符項目」。
        Assert.Equal("", model.EmptyState(0));

        var round = model.Begin(indexed: true)!;
        Assert.False(model.IsPending);
        Assert.True(model.IsRunning);
        model.End(round);
        Assert.False(model.IsRunning);
    }

    [Fact]
    public void 建索引那一輪顯示載入表面而已經有列的續輪不遮住結果()
    {
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var indexing = model.Begin(indexed: false)!;
        Assert.True(model.IsIndexing);
        Assert.True(model.ShowLoading(12));
        model.End(indexing);

        var indexed = model.Begin(indexed: true)!;
        Assert.False(model.IsIndexing);
        Assert.False(model.ShowLoading(12));
        Assert.True(model.ShowLoading(0));
        model.End(indexed);
        Assert.False(model.ShowLoading(0));
    }

    [Fact]
    public void 狀態文字只回報筆數部分結果與失敗()
    {
        var aggregator = new SearchAggregator(
            new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", "Loan", "LoanDetail") });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        var results = Search(aggregator, round.Query);
        Assert.True(model.Accept(round, results));
        model.End(round);

        Assert.Equal("找到 2 項", model.Status());
        Assert.Equal("", model.EmptyState(2));
    }

    [Fact]
    public void 部分結果與空結果各有自己的說法()
    {
        var aggregator = new SearchAggregator(
            new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", "Loan") { Truncate = true } });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var partial = model.Begin(indexed: true)!;
        Assert.True(model.Accept(partial, Search(aggregator, partial.Query)));
        model.End(partial);
        Assert.Contains("部分結果", model.Status());

        var empty = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };
        var none = new SearchAggregator(new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table") });
        var round = empty.Begin(indexed: true)!;
        Assert.True(empty.Accept(round, Search(none, round.Query)));
        empty.End(round);

        Assert.Equal("", empty.Status());
        Assert.Equal("沒有相符項目。", empty.EmptyState(0));
    }

    [Fact]
    public void 沒有輸入時不開一輪也不說沒有結果()
    {
        var model = new SqlSearchBrowserModel { HasConnection = true };

        // 空輸入開一輪的代價是把整個資料庫（含定義本文）索引一次，而使用者只是打開了視窗。
        Assert.Null(model.Begin(indexed: false));
        Assert.False(model.IsRunning);
        Assert.False(model.ShowLoading(0));
        Assert.Equal("輸入關鍵字，搜尋這個資料庫的物件名稱、資料行與定義本文。", model.EmptyState(0));
    }

    [Fact]
    public void 狀態的種類換了才算換一種說法()
    {
        var aggregator = new SearchAggregator(
            new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", "Loan", "LoanDetail") });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };
        Assert.Equal(SqlSearchStatusTone.None, model.Tone);

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);
        Assert.Equal(SqlSearchStatusTone.Result, model.Tone);

        var partial = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };
        var truncating = new SearchAggregator(
            new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", "Loan") { Truncate = true } });
        var next = partial.Begin(indexed: true)!;
        Assert.True(partial.Accept(next, Search(truncating, next.Query)));
        Assert.Equal(SqlSearchStatusTone.Partial, partial.Tone);

        partial.Fail(next, "搜尋失敗：連線中斷。");
        Assert.Equal(SqlSearchStatusTone.Failure, partial.Tone);
    }

    [Fact]
    public void 來源失敗寫進狀態列而不是讓清單整份消失()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table", "Loan"),
            new StubProvider("memory", "memory.favorite", "Favorite") { Throw = true },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        var results = Search(aggregator, round.Query);

        Assert.True(model.Accept(round, results));
        Assert.Single(results.Hits);
        Assert.Contains("memory", model.Status());
    }

    /// <summary>
    /// 「有一個來源讀不到」與「掃到一半停了」是兩句話。
    /// </summary>
    /// <remarks>
    /// 兩者都讓 IsPartial 為真，但泛用那一句叫使用者縮小範圍或加長關鍵字，
    /// 而那對「msdb 沒有權限」完全沒有用——他會先照那一句試三次。
    /// </remarks>
    [Fact]
    public void 讀不到的來源有自己的說法而不是泛用的部分結果()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table", "Loan"),
            new StubProvider("agent-job", "agent-job.job", "作業")
            {
                Unavailable = "作業這一輪讀不到（多半是這個登入對 msdb 沒有權限），這個來源沒有結果。",
            },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        // 那一句話原樣來自 provider；這一層一個字都不加，也不認得任何一個 provider 的常數。
        Assert.Equal("找到 1 項。作業這一輪讀不到（多半是這個登入對 msdb 沒有權限），這個來源沒有結果。",
            model.Status());

        // 讀不到不是失敗：頁尾不該變成紅字，對一個多半讀不到的來源那等於每次搜尋都在報錯。
        Assert.Equal(SqlSearchStatusTone.Partial, model.Tone);
    }

    /// <summary>一筆都沒有時，「讀不到」那一句仍然要說。</summary>
    /// <remarks>
    /// 不說的話，畫面上與「這台伺服器上真的沒有這個作業」一模一樣。
    /// </remarks>
    [Fact]
    public void 一筆都沒有時仍然說得出哪一個來源讀不到()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("agent-job", "agent-job.job", "作業")
            {
                Unavailable = "作業這一輪讀不到（多半是這個登入對 msdb 沒有權限），這個來源沒有結果。",
            },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        Assert.StartsWith("作業這一輪讀不到", model.Status());
        Assert.Equal("沒有相符項目。", model.EmptyState(0));
    }

    /// <summary>
    /// 幾個來源同時讀不到時仍然是一行，而且第一句原樣留著。
    /// </summary>
    /// <remarks>
    /// 這一層與 provider 無關，所以「下一個權限常常不足的來源」不必在這裡加任何東西；
    /// 加得到的那一版，漏掉的那一個只會安靜地退回泛用的「部分結果」。
    /// </remarks>
    [Fact]
    public void 兩個來源讀不到時只貼第一句其餘用數字帶過()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("agent-job", "agent-job.job", "作業") { Unavailable = "作業讀不到。" },
            new StubProvider("replication", "replication.article", "發行項")
            {
                Unavailable = "複寫讀不到。",
            },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        Assert.Equal("作業讀不到。（另有 1 個來源這一輪也讀不到）", model.Status());
        Assert.Equal(SqlSearchStatusTone.Partial, model.Tone);
    }

    /// <summary>
    /// 沒掃完與讀不到同時發生時，讀不到那一句優先。
    /// </summary>
    /// <remarks>
    /// 泛用的「縮小範圍或加長關鍵字」對一個讀不到的來源完全沒有用，而兩句都貼上去的話，
    /// 使用者會先照第一句試三次。
    /// </remarks>
    [Fact]
    public void 同時沒掃完與讀不到時由讀不到那一句說明()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table", "Loan") { Truncate = true },
            new StubProvider("agent-job", "agent-job.job", "作業") { Unavailable = "作業讀不到。" },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        Assert.Equal("找到 1 項。作業讀不到。", model.Status());
        Assert.DoesNotContain("縮小範圍", model.Status());
    }

    [Fact]
    public void 選項比對位置與範圍原樣寫進查詢()
    {
        var model = new SqlSearchBrowserModel
        {
            HasConnection = true,
            Text = "PUBL_CODE",
            MatchCasing = true,
            WholeWord = true,
            Targets = SearchTargets.Name | SearchTargets.Column,
        };
        model.UseCategories(Categories());
        Assert.True(model.SetDatabaseSelected("LibArchive", selected: true));
        Assert.True(model.SetCategorySelected("catalog.table", selected: true));

        var round = model.Begin(indexed: true)!;

        Assert.Equal(SearchOptions.MatchCasing | SearchOptions.WholeWord, round.Query.Options);
        Assert.Equal(new[] { "LibArchive" }, round.Query.Scope.Databases.ToArray());
        Assert.Empty(round.Query.Scope.Servers);
        Assert.Equal(new[] { "catalog.table" }, round.Query.Categories.ToArray());
        Assert.Equal("PUBL_CODE", round.Query.Text);

        // 少掉 Text 的那一輪必須真的傳下去；掃回來再丟的話，第一次搜尋最貴的那一段一毫秒都沒省到。
        Assert.Equal(SearchTargets.Name | SearchTargets.Column, round.Query.Targets);
        Assert.False(round.Query.IncludesTarget(SearchMatchTarget.Text));
    }

    [Fact]
    public void 篩選摘要一個寫名字多個寫數字()
    {
        var model = new SqlSearchBrowserModel();
        model.UseCategories(Categories());

        Assert.Equal(SqlSearchBrowserModel.AllCategoriesLabel, model.CategorySummary());
        Assert.Equal(SqlSearchBrowserModel.CurrentConnectionLabel, model.DatabaseSummary());

        model.SetCategorySelected("catalog.table", selected: true);
        Assert.Equal("Table", model.CategorySummary());

        model.SetCategorySelected("catalog.view", selected: true);
        model.SetCategorySelected("catalog.procedure", selected: true);
        // 三個名字串起來會把按鈕撐到吃掉搜尋框；完整名單留在 chip 列與 Tooltip。
        Assert.Equal("3", model.CategorySummary());

        model.SetDatabaseSelected("LibArchive", selected: true);
        Assert.Equal("LibArchive", model.DatabaseSummary());
        model.SetDatabaseSelected("LibReporting", selected: true);
        Assert.Equal("2", model.DatabaseSummary());

        // 伺服器單選，所以摘要永遠是一個名字；沒指名時說的是「跟著查詢視窗」而不是
        // 資料庫那一顆的「目前連線」——兩句話講的是不同的東西。
        Assert.Equal(SqlSearchBrowserModel.ActiveEditorServerLabel, model.ServerSummary());
        model.Server = "LIBSQL01";
        Assert.Equal("LIBSQL01", model.ServerSummary());
    }

    [Fact]
    public void 指名伺服器會多一顆排在最前面的chip且清掉它就回到查詢視窗()
    {
        var model = new SqlSearchBrowserModel { Server = "LIBSQL01" };
        model.UseCategories(Categories());
        model.SetCategorySelected("catalog.table", selected: true);
        model.SetDatabaseSelected("LibArchive", selected: true);

        // 伺服器是範圍最外面那一圈，排在最前面：換掉它，清單上每一筆的來源都變了。
        Assert.Equal(
            new[] { "伺服器: LIBSQL01", "種類: Table", "資料庫: LibArchive" },
            model.Chips().Select(chip => chip.Label).ToArray());

        Assert.True(model.Remove(model.Chips().Single(chip => chip.Label == "伺服器: LIBSQL01")));
        Assert.Null(model.Server);
        Assert.Equal(SqlSearchBrowserModel.ActiveEditorServerLabel, model.ServerSummary());
    }

    [Fact]
    public void 指名的伺服器不進查詢範圍()
    {
        // SearchScope.Servers 是給連結伺服器（四段式名稱）的，provider 看到它就整輪不回結果。
        // 換一台物件總管上的伺服器換的是整份目錄，兩者混用的症狀是搜什麼都沒有。
        var model = new SqlSearchBrowserModel { Text = "Loan", HasConnection = true, Server = "LIBSQL01" };

        var round = model.Begin(indexed: true);

        Assert.NotNull(round);
        Assert.Empty(round!.Query.Scope.Servers);
    }

    [Fact]
    public void 預設狀態沒有任何chip而清掉一顆只清那一個條件()
    {
        var model = new SqlSearchBrowserModel { MatchCasing = true };
        model.UseCategories(Categories());
        model.SetCategorySelected("catalog.view", selected: true);
        model.SetCategorySelected("catalog.table", selected: true);
        model.SetDatabaseSelected("LibArchive", selected: true);

        // 順序照分類的宣告順序，不照使用者勾選的先後：勾選順序會讓同一組條件每次排出不同的 chip。
        Assert.Equal(
            new[] { "種類: Table", "種類: View", "資料庫: LibArchive", "大小寫" },
            model.Chips().Select(chip => chip.Label).ToArray());

        Assert.True(model.Remove(model.Chips().Single(chip => chip.Label == "種類: Table")));
        Assert.Equal(
            new[] { "種類: View", "資料庫: LibArchive", "大小寫" },
            model.Chips().Select(chip => chip.Label).ToArray());

        Assert.True(model.Remove(model.Chips().Single(chip => chip.Label == "大小寫")));
        Assert.False(model.MatchCasing);

        model.ClearCategories();
        model.ClearDatabases();
        // 沒篩選就不佔那一列：空的 chip 列整列收起。
        Assert.Empty(model.Chips());
    }

    [Fact]
    public void 分類清單換掉時勾在上面而已經不存在的那幾個要一起走()
    {
        var model = new SqlSearchBrowserModel();
        model.UseCategories(Categories());
        model.SetCategorySelected("catalog.view", selected: true);

        model.UseCategories(new[] { new SqlSearchCategoryOption("catalog.table", "Table") });

        // 留著的話，每一輪都以一個沒有 provider 認領的 Id 過濾，結果永遠是空的而畫面上看不出為什麼。
        Assert.Empty(model.CategoryIds);
    }

    [Fact]
    public void 排序只換順序相關度原樣交回聚合器排好的那一份()
    {
        var model = new SqlSearchBrowserModel();
        model.UseCategories(Categories());

        var hits = new[]
        {
            Hit("catalog.view", "[dbo].[vLoan]", 90),
            Hit("catalog.table", "[dbo].[Loan]", 50),
            Hit("catalog.table", "[dbo].[Branch]", 70),
        };

        Assert.Same(hits, model.Arrange(hits));

        model.Sort = SqlSearchSort.Name;
        Assert.Equal(
            new[] { "[dbo].[Branch]", "[dbo].[Loan]", "[dbo].[vLoan]" },
            model.Arrange(hits).Select(hit => hit.SortKey).ToArray());

        // 種類的先後是分類的宣告順序，不是 Id 的字母序；種類內再依限定名稱。
        model.Sort = SqlSearchSort.Kind;
        Assert.Equal(
            new[] { "[dbo].[Branch]", "[dbo].[Loan]", "[dbo].[vLoan]" },
            model.Arrange(hits).Select(hit => hit.SortKey).ToArray());
        Assert.Equal(
            new[] { "catalog.table", "catalog.table", "catalog.view" },
            model.Arrange(hits).Select(hit => hit.CategoryId).ToArray());
    }

    [Fact]
    public void 系統資料庫與使用者資料庫分成兩段()
    {
        Assert.True(SqlSearchBrowserModel.IsSystemDatabase("master"));
        Assert.True(SqlSearchBrowserModel.IsSystemDatabase("TempDB"));
        Assert.False(SqlSearchBrowserModel.IsSystemDatabase("LibArchive"));
    }

    [Fact]
    public void 資料庫名稱以不分大小寫比對()
    {
        var model = new SqlSearchBrowserModel();

        Assert.True(model.SetDatabaseSelected("LibArchive", selected: true));
        // 同一台上 LibArchive 與 libarchive 是同一個；兩份都留著等於把它索引兩次。
        Assert.False(model.SetDatabaseSelected("libarchive", selected: true));
        Assert.True(model.IsDatabaseSelected("LIBARCHIVE"));
        Assert.True(model.SetDatabaseSelected("libarchive", selected: false));
        Assert.Empty(model.Databases);
    }

    private static IReadOnlyList<SqlSearchCategoryOption> Categories() => new[]
    {
        new SqlSearchCategoryOption("catalog.table", "Table"),
        new SqlSearchCategoryOption("catalog.view", "View"),
        new SqlSearchCategoryOption("catalog.procedure", "Procedure"),
    };

    private static SearchHit Hit(string categoryId, string title, int score) =>
        new("catalog", categoryId, SearchMatchTarget.Name, title, categoryId + title, score);

    [Fact]
    public void 重新整理之後選回原來那一列否則預覽第一筆()
    {
        var model = new SqlSearchBrowserModel { HasConnection = true };
        var keys = new[] { "Name [dbo].[Loan]", "Name [dbo].[LoanDetail]", "Body [dbo].[Copy]" };

        model.RememberSelection("Name [dbo].[LoanDetail]");
        Assert.Equal(1, model.ResolveSelection(keys, hasSelection: false));

        // 沒有要還原的列時預覽第一筆，但不搶已有的選取。
        Assert.Equal(0, model.ResolveSelection(keys, hasSelection: false));
        Assert.Null(model.ResolveSelection(keys, hasSelection: true));

        model.RememberSelection("Name [dbo].[Branch]");
        Assert.Null(model.ResolveSelection(keys, hasSelection: true));
    }

    [Fact]
    public void 比對方式收成一個字串並原樣還原()
    {
        var saved = new SqlSearchBrowserModel
        {
            Targets = SearchTargets.Name | SearchTargets.Column,
            MatchCasing = true,
        };

        var restored = new SqlSearchBrowserModel();
        Assert.True(restored.RestoreMatchState(saved.MatchStateToken));
        Assert.Equal(SearchTargets.Name | SearchTargets.Column, restored.Targets);
        Assert.True(restored.MatchCasing);
        Assert.False(restored.WholeWord);
    }

    [Theory]
    // 沒有記錄、段數不對、不是數字、認不得的位元，以及一個部位都不掃的比對位置。
    [InlineData("")]
    [InlineData("7|1")]
    [InlineData("7|1|0|0")]
    [InlineData("x|1|0")]
    [InlineData("-1|1|0")]
    [InlineData("8|1|0")]
    [InlineData("0|1|0")]
    [InlineData("7|2|0")]
    [InlineData("7|1|")]
    public void 認不得的比對方式整組維持預設(string token)
    {
        var model = new SqlSearchBrowserModel { MatchCasing = false, WholeWord = false };

        Assert.False(model.RestoreMatchState(token));

        // 半套還原與「使用者上次真的這樣設」在畫面上一模一樣，所以一項都不能動。
        Assert.Equal(SearchTargets.All, model.Targets);
        Assert.False(model.MatchCasing);
        Assert.False(model.WholeWord);
    }

    /// <summary>跑完一輪。走 Task.Run 離開測試執行器的同步內容，不在其上同步等待。</summary>
    private static SearchResults Search(SearchAggregator aggregator, SearchQuery query) =>
        Task.Run(() => aggregator.SearchAsync(query, CancellationToken.None)).GetAwaiter().GetResult();

    /// <summary>只回報固定幾個名稱的來源；這一組測試要的是世代與狀態，不是比對品質。</summary>
    private sealed class StubProvider : ISearchProvider
    {
        private readonly string _categoryId;
        private readonly string[] _names;

        internal StubProvider(string id, string categoryId, string displayName, params string[] names)
        {
            Id = id;
            _categoryId = categoryId;
            _names = names;
            Categories = new[] { new SearchCategory(id, categoryId, displayName) };
        }

        internal bool Truncate { get; set; }

        /// <summary>截斷時交出去的續掃位置。</summary>
        internal string Checkpoint { get; set; } = "last";

        internal bool Throw { get; set; }

        /// <summary>不為 null 時走「這一輪讀不到」，帶著這一句話。</summary>
        internal string? Unavailable { get; set; }

        public string Id { get; }

        public string DisplayName => Id;

        public IReadOnlyList<SearchCategory> Categories { get; }

        public Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("這個來源這一輪連不上。");

            foreach (var name in _names)
            {
                if (!name.StartsWith(query.Text, StringComparison.OrdinalIgnoreCase)) continue;

                sink.TryReport(new SearchHit(Id, _categoryId, SearchMatchTarget.Name, name, name, name.Length,
                    snippet: name, snippetSpans: new[] { new MatchSpan(0, query.Text.Length) }));
            }

            sink.ReportExamined(_names.Length);
            if (Truncate) sink.ReportTruncated(Checkpoint);
            if (Unavailable is not null) sink.ReportUnavailable(Unavailable);
            return Task.CompletedTask;
        }
    }
}
