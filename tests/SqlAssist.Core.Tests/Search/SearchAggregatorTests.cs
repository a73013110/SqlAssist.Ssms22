using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Search;
using Xunit;

namespace SqlAssist.Core.Tests.Search;

public sealed class SearchAggregatorTests
{
    [Fact]
    public async Task 多來源併發聚合的順序可重現()
    {
        var expected = Array.Empty<string>();

        // 每一輪都重新建立來源，讓委派真正跨過排程；完成先後每一輪都可能不同。
        for (var round = 0; round < 25; round++)
        {
            var aggregator = Aggregate(
                new FakeSearchProvider("catalog", Hit("catalog", "Loan", 90), Hit("catalog", "LoanDetail", 70)),
                new FakeSearchProvider("memory", Hit("memory", "Copy", 90), Hit("memory", "Branch", 70)),
                new FakeSearchProvider("tabs", Hit("tabs", "Lib_Reader", 90, SearchMatchTarget.Text)));

            var results = await aggregator.SearchAsync(new SearchQuery("lo", round), CancellationToken.None);
            var order = results.Hits.Select(hit => hit.DedupeKey).ToArray();

            if (round == 0) expected = order;
            Assert.Equal(expected, order);
        }

        Assert.Equal(5, expected.Length);
    }

    [Fact]
    public async Task 名稱命中一律排在本文命中之前()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("body", Hit("body", "Loan", 10000, SearchMatchTarget.Text)),
            new FakeSearchProvider("name", Hit("name", "LoanDetail", 1)));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.Equal(
            new[] { SearchMatchTarget.Name, SearchMatchTarget.Text },
            results.Hits.Select(hit => hit.MatchTarget));
        Assert.False(results.IsPartial);
        Assert.Empty(results.Failures);
    }

    [Fact]
    public async Task 同一類內依分數由高到低()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog", Hit("catalog", "Branch", 10), Hit("catalog", "Copy", 80)),
            new FakeSearchProvider("memory", Hit("memory", "Loan", 50)));

        var results = await aggregator.SearchAsync(new SearchQuery("o"), CancellationToken.None);

        Assert.Equal(new[] { "Copy", "Loan", "Branch" }, results.Hits.Select(hit => hit.Title));
    }

    [Fact]
    public async Task 同分時依限定名稱穩定排序()
    {
        // 先推 Copy、後推 Branch，排序仍以限定名稱的 ordinal 決定。
        var aggregator = Aggregate(
            new FakeSearchProvider("z-late", Hit("z-late", "Copy", 42)),
            new FakeSearchProvider("a-early", Hit("a-early", "Branch", 42)));

        var results = await aggregator.SearchAsync(new SearchQuery("c"), CancellationToken.None);

        Assert.Equal(new[] { "dbo.Branch", "dbo.Copy" }, results.Hits.Select(hit => hit.SortKey));
    }

    [Fact]
    public async Task 同分且同名時依來源與分類打破平手()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("memory", Hit("memory", "Loan", 42, dedupeKey: "memory:Loan")),
            new FakeSearchProvider("catalog", Hit("catalog", "Loan", 42, dedupeKey: "catalog:Loan")));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.Equal(new[] { "catalog", "memory" }, results.Hits.Select(hit => hit.ProviderId));
    }

    [Fact]
    public async Task 去重保留分數高的那一份()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog", Hit("catalog", "Loan", 30, dedupeKey: "dbo.Loan")),
            new FakeSearchProvider("memory", Hit("memory", "Loan", 90, dedupeKey: "dbo.Loan")));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        var hit = Assert.Single(results.Hits);
        Assert.Equal("memory", hit.ProviderId);
        Assert.Equal(90, hit.Score);
    }

    /// <summary>
    /// 同一個物件被兩種部位命中時併成一列，留下排名較高的那一份。
    /// </summary>
    /// <remarks>
    /// 兩列指向同一個地方、點下去做同一件事，而使用者看到的是清單上重複的兩行。
    /// 留下的是名稱那一份：比較器先比部位再比分數。
    /// </remarks>
    [Fact]
    public async Task 同一個物件的兩種命中合併成一列()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog",
                Hit("catalog", "Loan", 90, dedupeKey: "dbo.Loan"),
                Hit("catalog", "Loan", 20, SearchMatchTarget.Text, dedupeKey: "dbo.Loan")));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        var hit = Assert.Single(results.Hits);
        Assert.Equal(SearchMatchTarget.Name, hit.MatchTarget);
        Assert.Equal(90, hit.Score);
    }

    /// <summary>資料行命中不會被它所屬物件的命中吃掉：去重鍵多一段資料行名稱。</summary>
    [Fact]
    public async Task 資料行命中不與物件命中合併()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog",
                Hit("catalog", "Loan", 90, dedupeKey: "dbo.Loan"),
                Hit("catalog", "Loan", 80, SearchMatchTarget.Column, dedupeKey: "dbo.Loan.CopyNo")));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.Equal(
            new[] { SearchMatchTarget.Name, SearchMatchTarget.Column },
            results.Hits.Select(hit => hit.MatchTarget));
    }

    /// <summary>
    /// 分組順序是名稱、資料行、定義本文，而且分數不跨組比較。
    /// </summary>
    /// <remarks>
    /// 照列舉值排的話本文命中會插在名稱與資料行中間，而使用者打 <c>CopyNo</c> 要的是
    /// 「叫這個名字的資料行在哪幾張表」。
    /// </remarks>
    [Fact]
    public async Task 部位的分組順序是名稱資料行本文()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("body", Hit("body", "Loan", 10000, SearchMatchTarget.Text)),
            new FakeSearchProvider("column", Hit("column", "CopyNo", 1, SearchMatchTarget.Column)),
            new FakeSearchProvider("name", Hit("name", "LoanDetail", 1)));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.Equal(
            new[] { SearchMatchTarget.Name, SearchMatchTarget.Column, SearchMatchTarget.Text },
            results.Hits.Select(hit => hit.MatchTarget));
    }

    [Fact]
    public async Task 筆數預算用盡時標記部分並要求來源停止()
    {
        var provider = new FakeSearchProvider("catalog",
            Hit("catalog", "Loan", 90),
            Hit("catalog", "LoanDetail", 80),
            Hit("catalog", "Copy", 70),
            Hit("catalog", "Branch", 60));

        var aggregator = Aggregate(new SearchBudget(maxHits: 100, maxHitsPerProvider: 2), provider);
        var results = await aggregator.SearchAsync(new SearchQuery("l"), CancellationToken.None);

        Assert.Equal(new[] { true, true, false }, provider.Accepted);
        Assert.Equal(2, results.Hits.Count);
        Assert.True(results.IsPartial);
        Assert.True(Assert.Single(results.Progress).IsTruncated);
    }

    [Fact]
    public async Task 候選預算用盡時要求來源停止()
    {
        var accepted = new List<bool>();
        var provider = new FakeSearchProvider("catalog", async (query, sink, cancellationToken) =>
        {
            await Task.Yield();
            accepted.Add(sink.TryReport(Hit("catalog", "Loan", 90)));
            sink.ReportExamined(4);
            Assert.False(sink.IsExhausted);
            sink.ReportExamined(1);
            Assert.True(sink.IsExhausted);
            accepted.Add(sink.TryReport(Hit("catalog", "Copy", 80)));
        });

        var aggregator = Aggregate(new SearchBudget(maxCandidatesPerProvider: 5), provider);
        var results = await aggregator.SearchAsync(new SearchQuery("l"), CancellationToken.None);

        Assert.Equal(new[] { true, false }, accepted);
        Assert.Single(results.Hits);
        Assert.True(results.IsPartial);
        Assert.Equal(5, Assert.Single(results.Progress).Examined);
    }

    [Fact]
    public async Task 時間預算用盡時要求來源停止()
    {
        // 注入計時來源，不靠真的睡覺：睡覺會讓這個測試同時變慢與不穩定。
        var elapsed = TimeSpan.Zero;
        var accepted = new List<bool>();
        var provider = new FakeSearchProvider("catalog", async (query, sink, cancellationToken) =>
        {
            await Task.Yield();
            accepted.Add(sink.TryReport(Hit("catalog", "Loan", 90)));
            elapsed = TimeSpan.FromMilliseconds(500);
            accepted.Add(sink.TryReport(Hit("catalog", "Copy", 80)));
        });

        var aggregator = new SearchAggregator(
            new[] { provider },
            new SearchBudget(maxDuration: TimeSpan.FromMilliseconds(100)),
            () => elapsed);

        var results = await aggregator.SearchAsync(new SearchQuery("l"), CancellationToken.None);

        Assert.Equal(new[] { true, false }, accepted);
        Assert.Single(results.Hits);
        Assert.True(results.IsPartial);
    }

    [Fact]
    public async Task 總數上限在排名之後才裁並算部分結果()
    {
        var aggregator = Aggregate(
            new SearchBudget(maxHits: 2),
            new FakeSearchProvider("catalog",
                Hit("catalog", "Branch", 10),
                Hit("catalog", "Copy", 90),
                Hit("catalog", "Loan", 50)));

        var results = await aggregator.SearchAsync(new SearchQuery("o"), CancellationToken.None);

        Assert.Equal(new[] { "Copy", "Loan" }, results.Hits.Select(hit => hit.Title));
        Assert.True(results.IsPartial);
    }

    [Fact]
    public async Task 取消時回傳已收到的結果且不擲出()
    {
        // 這是刻意的行為：取消是打字驅動搜尋的正常流程，不是錯誤路徑。呼叫端不必包 try/catch；
        // provider 擲出的 OperationCanceledException 在聚合器裡被接住，也不計入失敗摘要。
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeSearchProvider("catalog", async (query, sink, cancellationToken) =>
        {
            await Task.Yield();
            sink.TryReport(Hit("catalog", "Loan", 90));
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            sink.TryReport(Hit("catalog", "Copy", 80));
        });

        var results = await Aggregate(provider).SearchAsync(new SearchQuery("l"), cancellation.Token);

        Assert.Equal("Loan", Assert.Single(results.Hits).Title);
        Assert.True(results.IsPartial);
        Assert.Empty(results.Failures);
        Assert.True(Assert.Single(results.Progress).IsTruncated);
    }

    [Fact]
    public async Task 單一來源擲例外時其他來源的結果仍在()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("broken", (query, sink, cancellationToken) =>
                throw new InvalidOperationException("連線已關閉")),
            new FakeSearchProvider("slow-broken", async (query, sink, cancellationToken) =>
            {
                await Task.Yield();
                sink.TryReport(Hit("slow-broken", "Branch", 10));
                throw new TimeoutException("逾時");
            }),
            new FakeSearchProvider("catalog", Hit("catalog", "Loan", 90)));

        var results = await aggregator.SearchAsync(new SearchQuery("l"), CancellationToken.None);

        // 同步擲出的與已經推過結果才擲出的都要被隔離，後者已經收到的那一筆不丟。
        Assert.Equal(new[] { "Loan", "Branch" }, results.Hits.Select(hit => hit.Title));
        Assert.Equal(
            new[] { "broken", "slow-broken" },
            results.Failures.Select(failure => failure.ProviderId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains("連線已關閉", results.Failures.Select(failure => failure.Message));
        Assert.True(results.IsPartial);
    }

    [Fact]
    public async Task 落後世代的查詢直接丟棄且不叫來源()
    {
        var provider = new FakeSearchProvider("catalog", Hit("catalog", "Loan", 90));
        var aggregator = Aggregate(provider);

        var current = await aggregator.SearchAsync(new SearchQuery("loan", 5), CancellationToken.None);
        var late = await aggregator.SearchAsync(new SearchQuery("loa", 3), CancellationToken.None);

        Assert.False(current.IsStale);
        Assert.Single(current.Hits);
        Assert.True(late.IsStale);
        Assert.Empty(late.Hits);

        // 過期與「找不到」是兩件事：過期不算部分結果，UI 應該留著上一份清單。
        Assert.False(late.IsPartial);
        Assert.Equal(3, late.Generation);

        // 只被叫過一次：落後的那一輪連 provider 都沒進去。
        Assert.Single(provider.Accepted);
    }

    [Fact]
    public async Task 執行中的舊世代收到停止訊號且結果整份丟棄()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new List<bool>();

        var provider = new FakeSearchProvider("catalog", async (query, sink, cancellationToken) =>
        {
            await Task.Yield();

            // 新的一輪不等門，否則兩輪會互鎖。
            if (query.Generation != 1) return;

            accepted.Add(sink.TryReport(Hit("catalog", "Loan", 90)));
            first.SetResult(true);
            await gate.Task;
            accepted.Add(sink.TryReport(Hit("catalog", "Copy", 80)));
        });

        var aggregator = Aggregate(provider);
        var stale = aggregator.SearchAsync(new SearchQuery("loan", 1), CancellationToken.None);
        await first.Task;

        var fresh = await aggregator.SearchAsync(new SearchQuery("loan copy", 2), CancellationToken.None);
        gate.SetResult(true);
        var discarded = await stale;

        Assert.Equal(new[] { true, false }, accepted);
        Assert.True(discarded.IsStale);
        Assert.Empty(discarded.Hits);
        Assert.False(fresh.IsStale);
    }

    [Fact]
    public async Task 分類過濾只留下勾選的種類()
    {
        var provider = new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.TryReport(Hit("catalog", "Loan", 90, categoryId: "catalog.table"));
            sink.TryReport(Hit("catalog", "CopyNo", 80, categoryId: "catalog.column"));
            return Task.CompletedTask;
        });

        var aggregator = Aggregate(provider);
        var all = await aggregator.SearchAsync(new SearchQuery("o", 1), CancellationToken.None);
        var tablesOnly = await aggregator.SearchAsync(
            new SearchQuery("o", 2, categories: new[] { "catalog.table" }), CancellationToken.None);

        Assert.Equal(2, all.Hits.Count);
        Assert.Equal("Loan", Assert.Single(tablesOnly.Hits).Title);

        // 被濾掉的不算部分結果，也不是叫 provider 停下來。
        Assert.False(tablesOnly.IsPartial);
    }

    [Fact]
    public async Task 沒有來源時回傳空的完整結果()
    {
        var results = await Aggregate().SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.Empty(results.Hits);
        Assert.False(results.IsPartial);
        Assert.False(results.IsStale);
        Assert.Empty(results.Progress);
    }

    [Fact]
    public void 來源識別字重複時拒絕建立()
    {
        Assert.Throws<ArgumentException>(() => Aggregate(
            new FakeSearchProvider("catalog", Hit("catalog", "Loan", 1)),
            new FakeSearchProvider("catalog", Hit("catalog", "Copy", 1))));
    }

    [Fact]
    public void 聚合器彙整所有來源宣告的分類()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog", (query, sink, cancellationToken) => Task.CompletedTask,
                new SearchCategory("catalog", "catalog.table", "資料表"),
                new SearchCategory("catalog", "catalog.column", "資料行")),
            new FakeSearchProvider("memory", (query, sink, cancellationToken) => Task.CompletedTask,
                new SearchCategory("memory", "memory.history", "執行紀錄")));

        Assert.Equal(
            new[] { "catalog.table", "catalog.column", "memory.history" },
            aggregator.Categories.Select(category => category.Id));
    }

    /// <summary>計時凍結在零，讓沒有在測時間預算的案例不會因為機器忙碌而變成部分結果。</summary>
    private static SearchAggregator Aggregate(params ISearchProvider[] providers) =>
        new(providers, null, () => TimeSpan.Zero);

    private static SearchAggregator Aggregate(SearchBudget budget, params ISearchProvider[] providers) =>
        new(providers, budget, () => TimeSpan.Zero);

    private static SearchHit Hit(
        string providerId,
        string name,
        int score,
        SearchMatchTarget matchTarget = SearchMatchTarget.Name,
        string? dedupeKey = null,
        string? categoryId = null)
    {
        Assert.True(SqlObjectPath.TryParseName(new[] { "dbo", name }, out var path));

        return new SearchHit(
            providerId,
            categoryId ?? providerId + ".default",
            matchTarget,
            name,
            dedupeKey ?? providerId + ":" + name,
            score,
            path,
            snippet: name,
            snippetSpans: new[] { new MatchSpan(0, 1) });
    }
}
