using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Lists;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;
using SqlAssist.Ssms22.Search;
using SqlAssist.Ssms22.UI;
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
    public void 過濾選項照provider宣告的群與排序()
    {
        var options = SqlSearchBrowserModel.CategoryOptions(new[]
        {
            // 收納桶排在最後，而它在宣告清單裡的位置由當初加進去的時間決定；照 SortOrder 走才排得對。
            new SearchCategory("catalog", "catalog.other", "Other", 1000, "catalog.other"),
            new SearchCategory("catalog", "catalog.view", "View", 1, "catalog.objects"),
            new SearchCategory("catalog", "catalog.table", "Table", 0, "catalog.objects"),
            new SearchCategory("agent-job", "agent-job.job", "作業", 0, "agent-job"),
        });

        // 同一個 provider 的幾群連在一起，群內與群間都照 SortOrder；收納桶因此落在自己來源的最後，
        // 而不是因為宣告得早就排到物件種類前面。
        Assert.Equal(
            new[] { "catalog.table", "catalog.view", "catalog.other", "agent-job.job" },
            options.Select(option => option.Id).ToArray());
        Assert.Equal(
            new[] { "catalog.objects", "catalog.objects", "catalog.other", "agent-job" },
            options.Select(option => option.GroupId).ToArray());
    }

    [Fact]
    public void 段落標題取自provider的顯示字()
    {
        var options = SqlSearchBrowserModel.CategoryOptions(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table"),
            new StubProvider("agent-job", "agent-job.job", "作業"),
        });

        // 標題是 provider 的名字，不是群的 Id：群是 provider 自己切的，使用者要分的是來源這一層。
        Assert.Equal(new[] { "catalog", "agent-job" }, options.Select(option => option.GroupLabel).ToArray());
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
    public void 還沒選伺服器時不開始任何一輪()
    {
        var model = new SqlSearchBrowserModel { Text = "Loan" };

        Assert.Null(model.Begin(indexed: true));
        Assert.False(model.IsRunning);
        var surface = model.Surface(0);
        Assert.Equal(SqlSurfaceKind.Empty, surface.Kind);
        Assert.Equal("尚未選擇伺服器", surface.Title);
        Assert.Contains(SqlEditorConnectionText.ApplyAction, surface.Detail);
        // 死路要有出口：那一句話說不出「按這裡就好」。
        Assert.Equal(SqlSearchBrowserModel.ChooseServerAction, surface.ActionLabel);
        Assert.Equal(SqlListFooterKind.Hidden, model.Footer(0).Kind);
    }

    [Fact]
    public void 選的伺服器連不上時說的是那一台()
    {
        // 選了伺服器卻沒有目錄，是那一台斷了；下一步是換一台，與還沒選時同一顆按鈕。
        var model = new SqlSearchBrowserModel { Text = "Loan" };
        model.Scope.SetServerSelected("LIBSQL01", selected: true);

        var surface = model.Surface(0);
        Assert.Equal(SqlSurfaceKind.Unreadable, surface.Kind);
        Assert.Equal(SqlSurfaceState.UnreadableTitle, surface.Title);
        Assert.Equal("連不上 LIBSQL01。那一台可能已經中斷，換一台再搜。", surface.Detail);
        Assert.Equal(SqlSearchBrowserModel.ChooseServerAction, surface.ActionLabel);
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
        Assert.Equal(SqlSurfaceKind.None, model.Surface(0).Kind);

        var round = model.Begin(indexed: true)!;
        Assert.False(model.IsPending);
        Assert.True(model.IsRunning);
        model.End(round);
        Assert.False(model.IsRunning);
    }

    [Fact]
    public void 載入表面只在還沒有任何一列時出現()
    {
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        // 建索引那一輪邊跑邊列：已經列出來的結果要看得到，進度在清單上方那一條。
        var indexing = model.Begin(indexed: false)!;
        Assert.True(model.IsIndexing);
        Assert.Equal(SqlSurfaceKind.Loading, model.Surface(0).Kind);
        Assert.Equal(SqlSurfaceKind.None, model.Surface(12).Kind);
        model.End(indexing);

        var indexed = model.Begin(indexed: true)!;
        Assert.False(model.IsIndexing);
        Assert.Equal(SqlSurfaceKind.None, model.Surface(12).Kind);
        Assert.Equal(SqlSurfaceKind.Loading, model.Surface(0).Kind);
        model.End(indexed);
        Assert.NotEqual(SqlSurfaceKind.Loading, model.Surface(0).Kind);
    }

    /// <summary>
    /// 完整時也要明說：沒說與完整在畫面上一模一樣，而使用者拿清單估工時。
    /// </summary>
    [Fact]
    public void 完整搜尋時頁尾說搜了哪些並掛成功語氣()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table", "Loan", "LoanDetail") { Databases = new[] { "Library", "LibArchive" } },
            new StubProvider("agent-job", "agent-job.job", "作業") { Source = "SQL Agent 作業" },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        var footer = model.Footer(2);
        Assert.Equal(SqlListFooterKind.End, footer.Kind);
        Assert.Equal("找到 2 項", footer.Summary);
        Assert.Equal("已完整搜尋 2 個資料庫、SQL Agent 作業", footer.Hint);
        Assert.Equal(SqlListFooterTone.Success, footer.Tone);
        Assert.Null(footer.ActionLabel);
        Assert.True(model.IsComplete);
        Assert.Equal(SqlSurfaceKind.None, model.Surface(2).Kind);
    }

    /// <summary>「沒有相符項目」只在完整時是答案，而且把搜了哪些一起說出來。</summary>
    [Fact]
    public void 完整而沒有結果時空白畫面說搜了哪些()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table") { Databases = new[] { "Library" } },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        Assert.Equal(SqlListFooterKind.Hidden, model.Footer(0).Kind);
        var blank = model.Surface(0);
        Assert.Equal(SqlSurfaceKind.Empty, blank.Kind);
        Assert.Equal("沒有相符項目", blank.Title);
        Assert.Equal("已完整搜尋 1 個資料庫", blank.Detail);
    }

    /// <summary>
    /// 一部分沒搜到時說缺了哪幾個、為什麼，而且「沒有相符項目」要掛警示。
    /// </summary>
    /// <remarks>
    /// 原本的「部分結果；縮小範圍或加長關鍵字」說不出缺了哪一個，而加長關鍵字一個候選都不會少。
    /// </remarks>
    [Fact]
    public void 不完整時說出缺了哪幾個與原因()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table", "Loan")
            {
                Databases = new[] { "Library" },
                Offline = new[] { "LibArchive" },
                Unreadable = 3,
            },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        var footer = model.Footer(1);
        Assert.Equal(SqlListFooterTone.Warning, footer.Tone);
        Assert.Equal(
            "搜尋不完整：Library 有 3 個物件的本文讀不到（加密或沒有 VIEW DEFINITION）；LibArchive：OFFLINE",
            footer.Hint);
        Assert.False(model.IsComplete);

        // 讀不到不是失敗：不送失敗通知，對一個多半讀不到的來源那等於每次搜尋都在報錯。
        Assert.Equal("", model.Failure);
    }

    [Fact]
    public void 不完整而沒有結果時空白畫面掛警示()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table") { Databases = new[] { "Library" }, Offline = new[] { "LibArchive" } },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        var surface = model.Surface(0);
        Assert.Equal(SqlSurfaceKind.Incomplete, surface.Kind);
        Assert.Equal("沒有相符項目，但搜尋不完整", surface.Title);
        Assert.Equal("LibArchive：OFFLINE", surface.Detail);
    }

    [Fact]
    public void 缺口超過三項時其餘用數字帶過()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table", "Loan")
            {
                Databases = new[] { "Library" },
                Offline = new[] { "LibArchive", "LibMirror", "LibReporting", "LibStaging" },
            },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        Assert.Equal(
            "搜尋不完整：LibArchive：OFFLINE；LibMirror：OFFLINE；LibReporting：OFFLINE；另有 1 項",
            model.Footer(1).Hint);
    }

    [Fact]
    public void 清單列不下時照實說總數()
    {
        var names = Enumerable.Range(0, 3).Select(index => "Loan" + index).ToArray();
        var aggregator = new SearchAggregator(
            new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", names) }, maxHits: 2);
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        Assert.Equal("找到 3 項，列出前 2 項", model.Footer(2).Summary);
    }

    [Fact]
    public void 沒有輸入時不開一輪也不說沒有結果()
    {
        var model = new SqlSearchBrowserModel { HasConnection = true };

        // 空輸入開一輪的代價是把整個資料庫（含定義本文）索引一次，而使用者只是打開了視窗。
        Assert.Null(model.Begin(indexed: false));
        Assert.False(model.IsRunning);
        var prompt = model.Surface(0);
        Assert.Equal(SqlSurfaceKind.Empty, prompt.Kind);
        Assert.Equal("輸入關鍵字", prompt.Title);
        Assert.Equal("搜尋這個資料庫的物件名稱、資料行與定義本文。", prompt.Detail);
    }

    [Fact]
    public void 重搜中與整輪失敗時頁尾說清楚清單是哪一份()
    {
        var aggregator = new SearchAggregator(
            new ISearchProvider[] { new StubProvider("catalog", "catalog.table", "Table", "Loan", "LoanDetail") });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);
        Assert.Equal("", model.Failure);

        // 換了條件：清單還是上一輪的兩列，頁尾留著筆數，不蓋住它們。
        model.Text = "LoanDetail";
        model.Invalidate();
        var waiting = model.Footer(2);
        Assert.Equal(SqlListFooterKind.Loading, waiting.Kind);
        Assert.Equal(SqlSearchBrowserModel.SearchingLabel, waiting.ActionLabel);

        var next = model.Begin(indexed: true)!;
        Assert.Equal(SqlListFooterKind.Loading, model.Footer(2).Kind);

        // 整輪失敗而清單還留著上一輪的列：要說那是上一輪的，否則會被當成這一次的答案。
        model.Fail(next, "搜尋失敗：連線中斷。");
        model.End(next);
        var failed = model.Footer(2);
        Assert.Equal("這一輪搜尋失敗", failed.Summary);
        Assert.Contains("上一輪", failed.Hint);
        Assert.Contains("連線中斷", failed.Hint);
        Assert.Equal(SqlListFooterTone.Warning, failed.Tone);
        Assert.Equal("搜尋失敗：連線中斷。", model.Failure);
        Assert.False(model.IsComplete);
    }

    [Fact]
    public void 來源失敗寫進頁尾而不是讓清單整份消失()
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
        model.End(round);
        Assert.Single(results.Hits);
        Assert.Contains("memory", model.Footer(1).Hint);
        Assert.Equal(SqlListFooterTone.Warning, model.Footer(1).Tone);
        Assert.Contains("memory", model.Failure);
    }

    /// <summary>使用者按了停止：清單留著停止前找到的，頁尾與空白畫面都說已停止。</summary>
    [Fact]
    public async Task 停止的那一輪說已停止()
    {
        using var stop = new CancellationTokenSource();
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table", "Loan") { Databases = new[] { "Library" }, CancelAfterReport = stop },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };

        var round = model.Begin(indexed: true)!;
        var results = await Task.Run(() => aggregator.SearchAsync(round.Query, stop.Token));
        Assert.True(results.IsCanceled);
        Assert.True(model.Accept(round, results));
        model.End(round);

        var footer = model.Footer(1);
        Assert.Equal("已停止；清單只含停止前找到的結果。", footer.Hint);
        Assert.Equal(SqlListFooterTone.Warning, footer.Tone);
        Assert.Equal("已停止；清單只含停止前找到的結果。", model.CoverageSummary);
        Assert.Equal(SqlSurfaceKind.Incomplete, model.Surface(0).Kind);
        Assert.Equal("已停止搜尋", model.Surface(0).Title);
    }

    /// <summary>一個目標都沒搜到時說讀不到，不說「沒有相符項目」。</summary>
    [Fact]
    public void 一個目標都沒搜到時說讀不到()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("agent-job", "agent-job.job", "作業") { Source = "SQL Agent 作業", SourceUnavailable = SearchUnavailableKind.Unknown },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(aggregator, round.Query)));
        model.End(round);

        Assert.Equal(SqlListFooterKind.Hidden, model.Footer(0).Kind);
        var unavailable = model.Surface(0);
        // 抬頭是「這一輪讀不到」：provider 說不出「就是權限」。斷言權限的那一版會在伺服器斷線的
        // 那一次叫使用者去查一個好好的權限設定。
        Assert.Equal(SqlSurfaceKind.Unreadable, unavailable.Kind);
        Assert.Equal(SqlSurfaceState.UnreadableTitle, unavailable.Title);
        Assert.Equal("SQL Agent 作業 讀不到", unavailable.Detail);
        Assert.Equal("", model.Failure);
    }

    /// <summary>
    /// 每一個讀不到的目標都說得出就是權限時，抬頭換成「權限不足」。
    /// </summary>
    /// <remarks>
    /// 門檻是「每一個」，不是「其中之一」：一個說權限、另一個連不上時，使用者照「權限不足」去要了權限，
    /// 連不上的那個下一輪還是讀不到，而畫面上看不出他要錯了東西。
    /// </remarks>
    [Fact]
    public void 全部都是權限時抬頭換成權限不足而混著時不換()
    {
        var denied = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("agent-job", "agent-job.job", "作業") { Source = "SQL Agent 作業", SourceUnavailable = SearchUnavailableKind.Denied },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };

        var round = model.Begin(indexed: true)!;
        Assert.True(model.Accept(round, Search(denied, round.Query)));
        model.End(round);

        Assert.Equal(SqlSurfaceKind.Denied, model.Surface(0).Kind);
        Assert.Equal(SqlSurfaceState.DeniedTitle, model.Surface(0).Title);
        Assert.Equal("SQL Agent 作業 沒有權限", model.Surface(0).Detail);

        var mixed = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("agent-job", "agent-job.job", "作業") { Source = "SQL Agent 作業", SourceUnavailable = SearchUnavailableKind.Denied },
            new StubProvider("replication", "replication.article", "發行項") { Source = "複寫", SourceUnavailable = SearchUnavailableKind.Unknown },
        });
        var second = model.Begin(indexed: true)!;
        Assert.True(model.Accept(second, Search(mixed, second.Query)));
        model.End(second);

        Assert.Equal(SqlSurfaceKind.Unreadable, model.Surface(0).Kind);
    }

    /// <summary>整輪失敗時抬頭是「這一輪讀不到」，即使上一輪說過權限。</summary>
    [Fact]
    public void 整輪失敗之後不再說權限不足()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("agent-job", "agent-job.job", "作業") { Source = "SQL Agent 作業", SourceUnavailable = SearchUnavailableKind.Denied },
        });
        var model = new SqlSearchBrowserModel { HasConnection = true, Text = "Branch" };

        var first = model.Begin(indexed: true)!;
        Assert.True(model.Accept(first, Search(aggregator, first.Query)));
        model.End(first);
        Assert.Equal(SqlSurfaceKind.Denied, model.Surface(0).Kind);

        var second = model.Begin(indexed: true)!;
        model.Fail(second, "「catalog」這一輪失敗：連線已關閉");
        model.End(second);

        var surface = model.Surface(0);

        Assert.Equal(SqlSurfaceKind.Unreadable, surface.Kind);
        Assert.Equal(SqlSurfaceState.UnreadableTitle, surface.Title);
    }

    [Fact]
    public async Task 進度說正在建立索引或正在搜尋與剩下多久()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table") { Databases = new[] { "Library", "LibArchive" }, Gate = gate },
        });

        var run = aggregator.Start(new SearchQuery("Loan"), CancellationToken.None);
        SpinWait.SpinUntil(() => run.Progress().Total == 2, TimeSpan.FromSeconds(10));
        var progress = run.Progress();

        Assert.Equal("正在建立搜尋索引 · 0/2", SqlSearchBrowserModel.ProgressText(progress, indexing: true, remaining: null));
        Assert.Equal("正在搜尋 · 0/2 · 約 20 秒",
            SqlSearchBrowserModel.ProgressText(progress, indexing: false, TimeSpan.FromSeconds(20)));
        Assert.Equal("正在搜尋 · 0/2 · 約 2 分鐘",
            SqlSearchBrowserModel.ProgressText(progress, indexing: false, TimeSpan.FromSeconds(95)));

        gate.SetResult(true);
        await run.Completion;

        // 還不知道有幾個目標（正在列資料庫）時說正在列，而不是「0/0」。
        var empty = new SearchAggregator(Array.Empty<ISearchProvider>()).Start(new SearchQuery("Loan"), CancellationToken.None);
        Assert.Equal("正在列出資料庫…", SqlSearchBrowserModel.ProgressText(empty.Progress(), indexing: true, remaining: null));
    }

    [Fact]
    public void 選項比對位置與範圍原樣寫進查詢()
    {
        var model = new SqlSearchBrowserModel
        {
            HasConnection = true,
            Text = "PUBL_CODE",
            MatchOptions = TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord,
            Targets = SearchTargets.Name | SearchTargets.Column,
        };
        model.UseCategories(Categories());
        Assert.True(model.Scope.SetDatabaseSelected("LibArchive", selected: true));
        Assert.True(model.SetCategorySelected("catalog.table", selected: true));

        var round = model.Begin(indexed: true)!;

        Assert.Equal(TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord, round.Query.Options);
        Assert.Equal(new[] { "LibArchive" }, round.Query.Scope.Databases.ToArray());
        Assert.Empty(round.Query.Scope.Servers);
        Assert.Equal(new[] { "catalog.table" }, round.Query.Categories.ToArray());
        Assert.Equal("PUBL_CODE", round.Query.Text);

        // 少掉 Text 的那一輪必須真的傳下去；掃回來再丟的話，第一次搜尋最貴的那一段一毫秒都沒省到。
        Assert.Equal(SearchTargets.Name | SearchTargets.Column, round.Query.Targets);
        Assert.False(round.Query.IncludesTarget(SearchMatchTarget.Text));
    }

    [Fact]
    public void 篩選摘要一個寫名字多個寫數量()
    {
        var model = new SqlSearchBrowserModel();
        model.UseCategories(Categories());

        Assert.Equal(SqlSearchBrowserModel.AllCategoriesLabel, model.CategorySummary());
        // 資料庫沒勾就是「全部」，與面板第一列同一句；有沒有連線都不換字。
        Assert.Equal(SqlSearchBrowserModel.AllDatabasesLabel, model.DatabaseSummary());

        model.SetCategorySelected("catalog.table", selected: true);
        Assert.Equal("Table", model.CategorySummary());

        model.SetCategorySelected("catalog.view", selected: true);
        model.SetCategorySelected("catalog.procedure", selected: true);
        // 三個名字串起來會把按鈕撐到吃掉搜尋框；完整名單留在面板與 Tooltip。
        // 量詞不能省：只剩一個數字時，使用者分不出那是幾種還是幾個。
        Assert.Equal("3 種", model.CategorySummary());

        // 伺服器單選，所以摘要永遠是一個名字；還沒選時說「未選擇」。
        Assert.Equal(SqlSearchBrowserModel.NoServerLabel, model.ServerSummary());
        model.Scope.SetServerSelected("LIBSQL01", selected: true);
        Assert.Equal("LIBSQL01", model.ServerSummary());

        model.Scope.SetDatabaseSelected("LibArchive", selected: true);
        Assert.Equal("LibArchive", model.DatabaseSummary());
        model.Scope.SetDatabaseSelected("LibReporting", selected: true);
        Assert.Equal("2 個", model.DatabaseSummary());
    }

    [Fact]
    public void 英文介面的篩選摘要與頁尾跟著語言()
    {
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            var model = new SqlSearchBrowserModel();
            model.UseCategories(Categories());

            Assert.Equal("All", model.CategorySummary());
            Assert.Equal("Not selected", model.ServerSummary());

            model.SetCategorySelected("catalog.table", selected: true);
            model.SetCategorySelected("catalog.view", selected: true);
            Assert.Equal("2 kinds", model.CategorySummary());

            // 排序選項是一份靜態清單，顯示字要每次取值才跟得上語言。
            Assert.Equal("Relevance", SqlSearchSortOption.For(SqlSearchSort.Relevance).Label);

            var waiting = new SqlSearchBrowserModel { HasConnection = true, Text = "Loan" };
            waiting.Invalidate();
            Assert.Equal("Results: 1,234", waiting.Footer(1234).Summary);
            Assert.Equal("Searching…", waiting.Footer(1234).ActionLabel);
        }
    }

    [Fact]
    public void 指名的伺服器不進查詢範圍()
    {
        // SearchScope.Servers 是給連結伺服器（四段式名稱）的，provider 看到它就整輪不回結果。
        // 換一台物件總管上的伺服器換的是整份目錄，兩者混用的症狀是搜什麼都沒有。
        var model = new SqlSearchBrowserModel { Text = "Loan", HasConnection = true };
        model.Scope.SetServerSelected("LIBSQL01", selected: true);

        var round = model.Begin(indexed: true);

        Assert.NotNull(round);
        Assert.Empty(round!.Query.Scope.Servers);
        // 資料庫沒勾就是「全部」：範圍原樣是空的，由 provider 照「不限制」搜每一個進得去的。
        Assert.Empty(round.Query.Scope.Databases);
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

        Assert.True(model.Scope.SetDatabaseSelected("LibArchive", selected: true));
        // 同一台上 LibArchive 與 libarchive 是同一個；兩份都留著等於把它索引兩次。
        Assert.False(model.Scope.SetDatabaseSelected("libarchive", selected: true));
        Assert.True(model.Scope.IsDatabaseSelected("LIBARCHIVE"));
        Assert.True(model.Scope.SetDatabaseSelected("libarchive", selected: false));
        Assert.Empty(model.Scope.Databases);
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
            MatchOptions = TextMatchOptions.MatchCasing,
        };

        var restored = new SqlSearchBrowserModel();
        Assert.True(restored.RestoreMatchState(saved.MatchStateToken));
        Assert.Equal(SearchTargets.Name | SearchTargets.Column, restored.Targets);
        Assert.Equal(TextMatchOptions.MatchCasing, restored.MatchOptions);
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
    [InlineData("7")]
    public void 認不得的比對方式整組維持預設(string token)
    {
        var model = new SqlSearchBrowserModel();

        Assert.False(model.RestoreMatchState(token));

        // 半套還原與「使用者上次真的這樣設」在畫面上一模一樣，所以一項都不能動。
        Assert.Equal(SearchTargets.All, model.Targets);
        Assert.Equal(TextMatchOptions.None, model.MatchOptions);
    }

    /// <summary>跑完一輪。走 Task.Run 離開測試執行器的同步內容，不在其上同步等待。</summary>
    private static SearchResults Search(SearchAggregator aggregator, SearchQuery query) =>
        Task.Run(() => aggregator.SearchAsync(query, CancellationToken.None)).GetAwaiter().GetResult();

    /// <summary>只回報固定幾個名稱的來源；這一組測試要的是世代與完整度，不是比對品質。</summary>
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

        internal bool Throw { get; set; }

        /// <summary>比完的資料庫目標。</summary>
        internal string[] Databases { get; set; } = Array.Empty<string>();

        /// <summary>讀不到的資料庫目標，狀態是 OFFLINE。</summary>
        internal string[] Offline { get; set; } = Array.Empty<string>();

        /// <summary>第一個資料庫有幾個物件的本文讀不到。</summary>
        internal int Unreadable { get; set; }

        /// <summary>不為 null 時宣告一個伺服器層級的來源目標。</summary>
        internal string? Source { get; set; }

        /// <summary>來源目標讀不到時的種類；null 表示比完了。</summary>
        internal SearchUnavailableKind? SourceUnavailable { get; set; }

        /// <summary>推完結果之後取消這一輪；模擬使用者按停止。</summary>
        internal CancellationTokenSource? CancelAfterReport { get; set; }

        /// <summary>宣告目標之後等這扇門；邊跑邊看進度的測試用。</summary>
        internal TaskCompletionSource<bool>? Gate { get; set; }

        public string Id { get; }

        public string DisplayName => Id;

        public IReadOnlyList<SearchCategory> Categories { get; }

        public async Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("這個來源這一輪連不上。");

            var databases = Databases.Select(name => sink.AddTarget(name, SearchTargetKind.Database)).ToArray();
            foreach (var name in Offline) sink.AddTarget(name, SearchTargetKind.Database).Unavailable(detail: "OFFLINE");
            var source = Source is null ? null : sink.AddTarget(Source, SearchTargetKind.Source);

            if (Gate is not null) await Gate.Task.ConfigureAwait(false);

            foreach (var name in _names)
            {
                if (!name.StartsWith(query.Text, StringComparison.OrdinalIgnoreCase)) continue;

                sink.TryReport(new SearchHit(Id, _categoryId, SearchMatchTarget.Name, name, name, name.Length,
                    snippet: name, snippetSpans: new[] { new MatchSpan(0, query.Text.Length) }));
            }

            if (CancelAfterReport is { } stop)
            {
                stop.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (databases.Length > 0) databases[0].AddUnreadableText(Unreadable);
            foreach (var database in databases) database.Complete();

            if (source is null) return;
            if (SourceUnavailable is { } kind) source.Unavailable(kind);
            else source.Complete();
        }
    }
}
