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
        Assert.True(results.IsComplete);
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

    /// <summary>
    /// 被併掉的那幾筆掛在代表身上，依排名排好，而且自己不再帶著別人。
    /// </summary>
    /// <remarks>
    /// 丟掉它們的那一版，一張只靠三個資料行命中的表在畫面上說不出是哪三行，
    /// 而那正是使用者搜這個字串要找的東西。攤平是為了讓呈現那一層不必遞迴。
    /// </remarks>
    [Fact]
    public async Task 併掉的命中掛在代表身上並攤平()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog",
                Hit("catalog", "Loan", 20, SearchMatchTarget.Text, dedupeKey: "dbo.Loan"),
                Hit("catalog", "Loan", 90, dedupeKey: "dbo.Loan"),
                Hit("catalog", "Loan", 70, SearchMatchTarget.Column, dedupeKey: "dbo.Loan")));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        var hit = Assert.Single(results.Hits);
        Assert.Equal(SearchMatchTarget.Name, hit.MatchTarget);
        Assert.Equal(
            new[] { SearchMatchTarget.Column, SearchMatchTarget.Text },
            hit.Merged.Select(merged => merged.MatchTarget));
        Assert.All(hit.Merged, merged => Assert.Empty(merged.Merged));

        // 代表自己排第一；呈現那一層讀的是這一份，漏掉它的症狀是只被名稱命中的物件顯示成沒有命中。
        Assert.Equal(
            new[] { SearchMatchTarget.Name, SearchMatchTarget.Column, SearchMatchTarget.Text },
            hit.Matches.Select(match => match.MatchTarget));
    }

    /// <summary>沒有被併的那一列不帶任何東西，但仍然算一處命中。</summary>
    [Fact]
    public async Task 沒有被併的命中自己就是全部()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", Hit("catalog", "Loan", 90, dedupeKey: "dbo.Loan")));

        var hit = Assert.Single((await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None)).Hits);

        Assert.Empty(hit.Merged);
        Assert.Same(hit, Assert.Single(hit.Matches));
    }

    /// <summary>
    /// 去重鍵不同就不合併，即使名稱一模一樣。
    /// </summary>
    /// <remarks>
    /// 兩個資料庫裡各有一張 <c>Loan</c> 是常態；併成一列的話其中一個永遠不出現，
    /// 而使用者看不出少了哪一個。
    /// </remarks>
    [Fact]
    public async Task 去重鍵不同就不合併()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog",
                Hit("catalog", "Loan", 90, dedupeKey: "Library.dbo.Loan"),
                Hit("catalog", "Loan", 80, dedupeKey: "Archive.dbo.Loan")));

        var results = await aggregator.SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.Equal(2, results.Hits.Count);
        Assert.All(results.Hits, hit => Assert.Empty(hit.Merged));
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

    /// <summary>
    /// 沒有預算：來源推多少就收多少，不會在比到定義本文之前就被叫停。
    /// </summary>
    /// <remarks>
    /// 原本每個來源最多一百筆、兩萬個候選、四百毫秒；「全部資料庫 × 全部種類」那一輪在名稱與
    /// 資料行上就把額度花完，本文命中一筆都收不到，而畫面上與「不存在」一模一樣。
    /// </remarks>
    [Fact]
    public async Task 沒有預算時來源推多少收多少()
    {
        var hits = Enumerable.Range(0, 300).Select(index => Hit("catalog", "Loan" + index, index)).ToArray();
        var provider = new FakeSearchProvider("catalog", hits);

        var results = await Aggregate(provider).SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.All(provider.Accepted, Assert.True);
        Assert.Equal(300, results.Hits.Count);
        Assert.True(results.IsComplete);
    }

    [Fact]
    public async Task 清單上限在排名之後才裁並照實說總數()
    {
        var aggregator = new SearchAggregator(
            new[]
            {
                new FakeSearchProvider("catalog",
                    Hit("catalog", "Branch", 10),
                    Hit("catalog", "Copy", 90),
                    Hit("catalog", "Loan", 50))
            },
            maxHits: 2);

        var results = await aggregator.SearchAsync(new SearchQuery("o"), CancellationToken.None);

        Assert.Equal(new[] { "Copy", "Loan" }, results.Hits.Select(hit => hit.Title));
        Assert.Equal(3, results.TotalHits);
        Assert.True(results.IsListTruncated);

        // 列不下不是沒搜到：搜尋本身仍然完整。
        Assert.True(results.IsComplete);
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
            sink.AddTarget("LibCatalog", SearchTargetKind.Database);
            sink.TryReport(Hit("catalog", "Loan", 90));
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            sink.TryReport(Hit("catalog", "Copy", 80));
        });

        var results = await Aggregate(provider).SearchAsync(new SearchQuery("l"), cancellation.Token);

        Assert.Equal("Loan", Assert.Single(results.Hits).Title);
        Assert.True(results.IsCanceled);
        Assert.False(results.IsComplete);
        Assert.Empty(results.Failures);
        Assert.Equal(SearchTargetState.Canceled, Assert.Single(results.Targets).State);
    }

    [Fact]
    public async Task 取消之後推結果會收到停止訊號()
    {
        using var cancellation = new CancellationTokenSource();
        var accepted = new List<bool>();
        var provider = new FakeSearchProvider("catalog", async (query, sink, cancellationToken) =>
        {
            await Task.Yield();
            accepted.Add(sink.TryReport(Hit("catalog", "Loan", 90)));
            cancellation.Cancel();
            accepted.Add(sink.TryReport(Hit("catalog", "Copy", 80)));
        });

        await Aggregate(provider).SearchAsync(new SearchQuery("l"), cancellation.Token);

        Assert.Equal(new[] { true, false }, accepted);
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
                sink.AddTarget("LibArchive", SearchTargetKind.Database);
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

        // 擲出例外之前宣告的目標沒有結局，記成已取消，不會被當成比完了。
        Assert.Equal(SearchTargetState.Canceled, Assert.Single(results.Targets).State);
        Assert.False(results.IsComplete);
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

        // 過期與「找不到」是兩件事：過期不算取消，UI 應該留著上一份清單。
        Assert.False(late.IsCanceled);
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

        // 被濾掉的不算沒搜到，也不是叫 provider 停下來。
        Assert.True(tablesOnly.IsComplete);
    }

    /// <summary>「沒找到」只有在每一個目標都說得出自己比完了才成立。</summary>
    [Fact]
    public async Task 每一個目標都比完才算完整()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", async (query, sink, cancellationToken) =>
        {
            await Task.Yield();
            sink.AddTarget("LibCatalog", SearchTargetKind.Database).Complete();
            sink.AddTarget("LibArchive", SearchTargetKind.Database).Complete();
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.Equal(new[] { "LibCatalog", "LibArchive" }, results.Targets.Select(target => target.Name));
        Assert.All(results.Targets, target => Assert.Equal(SearchTargetState.Complete, target.State));
        Assert.True(results.IsComplete);
    }

    /// <summary>
    /// provider 忘了說結局的目標記成已取消，不會被當成完整。
    /// </summary>
    /// <remarks>
    /// 預設值若是「完整」，漏寫一行的 provider 會讓畫面說「已完整搜尋」，而那個資料庫一個字都沒比。
    /// </remarks>
    [Fact]
    public async Task 沒說結局的目標記成已取消()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.AddTarget("LibCatalog", SearchTargetKind.Database);
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.Equal(SearchTargetState.Canceled, Assert.Single(results.Targets).State);
        Assert.False(results.IsComplete);
        Assert.False(results.IsCanceled);
    }

    [Fact]
    public async Task 讀不到的目標帶著種類與補充出去()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog", Hit("catalog", "Loan", 90)),
            new FakeSearchProvider("agent-job", (query, sink, cancellationToken) =>
            {
                sink.AddTarget("SQL Agent", SearchTargetKind.Source).Unavailable(SearchUnavailableKind.Denied);
                return Task.CompletedTask;
            }),
            new FakeSearchProvider("archive", (query, sink, cancellationToken) =>
            {
                sink.AddTarget("LibArchive", SearchTargetKind.Database).Unavailable(detail: "OFFLINE");
                return Task.CompletedTask;
            }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        var agent = Assert.Single(results.Targets, target => target.ProviderId == "agent-job");
        Assert.Equal(SearchTargetState.Unavailable, agent.State);
        Assert.Equal(SearchUnavailableKind.Denied, agent.UnavailableKind);
        Assert.Null(agent.Detail);

        var archive = Assert.Single(results.Targets, target => target.ProviderId == "archive");
        Assert.Equal(SearchUnavailableKind.Unknown, archive.UnavailableKind);
        Assert.Equal("OFFLINE", archive.Detail);

        // 讀不到不是失敗，另一個來源的結果照樣回得來，但這一輪不完整。
        Assert.Empty(results.Failures);
        Assert.Equal("Loan", Assert.Single(results.Hits).Title);
        Assert.False(results.IsComplete);
    }

    /// <summary>結局只收第一次；留後到的那一句會把一個沒比過的資料庫說成比完了。</summary>
    [Fact]
    public async Task 目標的結局只收第一次()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            var target = sink.AddTarget("LibArchive", SearchTargetKind.Database);
            target.Unavailable(SearchUnavailableKind.Denied);
            target.Complete();
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.Equal(SearchTargetState.Unavailable, Assert.Single(results.Targets).State);
    }

    /// <summary>本文讀不到的物件（加密、沒有 VIEW DEFINITION）與「本文裡沒有這個字」在清單上一模一樣。</summary>
    [Fact]
    public async Task 本文讀不到的物件讓完整度不成立()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            var target = sink.AddTarget("LibCatalog", SearchTargetKind.Database);
            target.AddUnreadableText(2);
            target.AddUnreadableText(1);
            target.Complete();
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);
        var status = Assert.Single(results.Targets);

        Assert.Equal(SearchTargetState.Complete, status.State);
        Assert.Equal(3, status.UnreadableText);
        Assert.False(status.IsComplete);
        Assert.False(results.IsComplete);
    }

    /// <summary>
    /// 一個目標讀不到不讓這個來源停下來。
    /// </summary>
    /// <remarks>
    /// 目錄那一邊是每個資料庫一條執行緒；讓 sink 因此停收的症狀是使用者勾了五個資料庫、
    /// 斷了第一個，剩下四個連掃都沒掃。
    /// </remarks>
    [Fact]
    public async Task 讀不到之後這個來源照樣推得進結果()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.AddTarget("LibArchive", SearchTargetKind.Database).Unavailable();
            Assert.True(sink.TryReport(Hit("catalog", "Loan", 90)));
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.Equal("Loan", Assert.Single(results.Hits).Title);
        Assert.False(results.IsComplete);
    }

    [Fact]
    public async Task 邊跑邊看得到進度與已經到的命中()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var aggregator = Aggregate(new FakeSearchProvider("catalog", async (query, sink, cancellationToken) =>
        {
            await Task.Yield();
            var catalog = sink.AddTarget("LibCatalog", SearchTargetKind.Database);
            var archive = sink.AddTarget("LibArchive", SearchTargetKind.Database);
            catalog.ReportProgress(0.5);
            sink.TryReport(Hit("catalog", "Loan", 90));
            reported.SetResult(true);
            await gate.Task;
            catalog.Complete();
            archive.Complete();
        }));

        var run = aggregator.Start(new SearchQuery("Loan"), CancellationToken.None);
        await reported.Task;

        var progress = run.Progress();
        Assert.Equal(2, progress.Total);
        Assert.Equal(0, progress.Finished);
        Assert.True(progress.IsDeterminate);
        Assert.Equal(0.25, progress.Fraction, 3);
        Assert.Equal("Loan", Assert.Single(run.ArrivedSince(0)).Title);
        Assert.Empty(run.ArrivedSince(1));

        gate.SetResult(true);
        var results = await run.Completion;

        Assert.True(results.IsComplete);
        Assert.Equal(1, run.Progress().Fraction, 3);
    }

    [Fact]
    public async Task 進度超出範圍時夾回去()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.AddTarget("LibCatalog", SearchTargetKind.Database).ReportProgress(1.7);
            sink.AddTarget("LibArchive", SearchTargetKind.Database).ReportProgress(-3);
            return Task.CompletedTask;
        }));

        var run = aggregator.Start(new SearchQuery("Loan"), CancellationToken.None);
        await run.Completion;

        Assert.Equal(new[] { 1.0, 0.0 }, run.Progress().Targets.Select(target => target.Progress));
    }

    [Fact]
    public async Task 空名稱的目標是程式錯誤()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.AddTarget("", SearchTargetKind.Database);
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.IsType<ArgumentException>(Assert.Single(results.Failures).Exception);
        Assert.False(results.IsComplete);
    }

    [Fact]
    public async Task 沒有來源時回傳空的完整結果()
    {
        var results = await Aggregate().SearchAsync(new SearchQuery("loan"), CancellationToken.None);

        Assert.Empty(results.Hits);
        Assert.True(results.IsComplete);
        Assert.False(results.IsStale);
        Assert.Empty(results.Targets);
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

    private static SearchAggregator Aggregate(params ISearchProvider[] providers) => new(providers);

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
