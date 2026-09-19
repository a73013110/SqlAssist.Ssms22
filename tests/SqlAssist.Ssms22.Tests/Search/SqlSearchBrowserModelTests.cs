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
    public void 分類pill由聚合器宣告的分類產生並去重()
    {
        var aggregator = new SearchAggregator(new ISearchProvider[]
        {
            new StubProvider("catalog", "catalog.table", "Table"),
            // 第二個來源的分類直接接在後面；UI 一個字串都不寫死。
            new StubProvider("memory", "memory.favorite", "Favorite"),
        });

        var options = SqlSearchBrowserModel.CategoryOptions(aggregator.Categories);

        Assert.Equal(new[] { SqlSearchBrowserModel.AllCategoriesLabel, "Table", "Favorite" },
            options.Select(option => option.Label).ToArray());
        Assert.Null(options[0].Id);
        Assert.Equal(new[] { "catalog.table", "memory.favorite" }, options.Skip(1).Select(option => option.Id).ToArray());
    }

    [Fact]
    public void 相同分類Id只產生一顆pill()
    {
        var options = SqlSearchBrowserModel.CategoryOptions(new[]
        {
            new SearchCategory("catalog", "catalog.table", "Table"),
            new SearchCategory("memory", "catalog.table", "資料表"),
        });

        Assert.Equal(2, options.Count);
        Assert.Equal("Table", options[1].Label);
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
        Assert.Equal("尚未連線。在 SQL 查詢視窗連上資料庫之後，這裡才有東西可以搜。", model.EmptyState(0));
        Assert.Equal("", model.Status());
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

    [Fact]
    public void 選項與範圍原樣寫進查詢()
    {
        var model = new SqlSearchBrowserModel
        {
            HasConnection = true,
            Text = "PUBL_CODE",
            MatchCasing = true,
            WholeWord = true,
            Database = "LibArchive",
            CategoryId = "catalog.column",
        };

        var round = model.Begin(indexed: true)!;

        Assert.Equal(SearchOptions.MatchCasing | SearchOptions.WholeWord, round.Query.Options);
        Assert.Equal(new[] { "LibArchive" }, round.Query.Scope.Databases.ToArray());
        Assert.Empty(round.Query.Scope.Servers);
        Assert.Equal(new[] { "catalog.column" }, round.Query.Categories.ToArray());
        Assert.Equal("PUBL_CODE", round.Query.Text);
    }

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

        internal bool Throw { get; set; }

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
            if (Truncate) sink.ReportTruncated("last");
            return Task.CompletedTask;
        }
    }
}
