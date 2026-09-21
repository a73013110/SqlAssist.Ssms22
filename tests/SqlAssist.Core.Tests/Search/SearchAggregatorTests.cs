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

    /// <summary>
    /// 「讀不到」是第一類訊號，不是續掃位置上的一個約定字串。
    /// </summary>
    /// <remarks>
    /// 呈現那一層要說的兩句話完全相反：「沒掃完」叫使用者縮小範圍或加長關鍵字，
    /// 「讀不到」叫他去看權限。分不出來的症狀是他先照前一句試三次。
    /// </remarks>
    [Fact]
    public async Task 讀不到的來源帶著自己的那一句話出去()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("catalog", Hit("catalog", "Loan", 90)),
            new FakeSearchProvider("agent-job", (query, sink, cancellationToken) =>
            {
                sink.ReportUnavailable("SQL Agent 作業這一輪讀不到（多半是這個登入對 msdb 沒有權限）。");
                return Task.CompletedTask;
            }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        var unavailable = Assert.Single(results.Progress, entry => entry.IsUnavailable);
        Assert.Equal("agent-job", unavailable.ProviderId);
        Assert.Contains("msdb", unavailable.UnavailableReason);

        // 讀不到的來源沒有「掃到哪裡」可言；把它記成截斷的話，兩句話又混回同一件事。
        Assert.False(unavailable.IsTruncated);
        Assert.Null(unavailable.Checkpoint);

        // 這一輪確實少了一個來源，所以是部分的——而另一個來源的結果照樣回得來。
        Assert.True(results.IsPartial);
        Assert.Equal("Loan", Assert.Single(results.Hits).Title);
    }

    /// <summary>
    /// 讀不到與擲例外是兩件事，而且不得互相冒充。
    /// </summary>
    /// <remarks>
    /// 例外走 <see cref="SearchProviderFailure"/>，工具窗的頁尾會變成紅字；對一個本來就
    /// 多半讀不到的來源，那等於每一次搜尋都在報錯。反過來把真的例外降級成「讀不到」，
    /// 則會讓程式錯誤安靜地變成一句「多半是權限不足」。
    /// </remarks>
    [Fact]
    public async Task 讀不到不算失敗而失敗不算讀不到()
    {
        var aggregator = Aggregate(
            new FakeSearchProvider("agent-job", (query, sink, cancellationToken) =>
            {
                sink.ReportUnavailable("msdb 讀不到。");
                return Task.CompletedTask;
            }),
            new FakeSearchProvider("broken", (query, sink, cancellationToken) =>
                throw new InvalidOperationException("連線已關閉")) { DisplayName = "資料庫物件" });

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        var failure = Assert.Single(results.Failures);
        Assert.Equal("broken", failure.ProviderId);
        // 畫面上寫的是顯示名稱：Id 是跨版本不得更名的識別字，使用者沒有在介面上見過它。
        Assert.Equal("資料庫物件", failure.DisplayName);

        var byProvider = results.Progress.ToDictionary(entry => entry.ProviderId, StringComparer.Ordinal);

        Assert.True(byProvider["agent-job"].IsUnavailable);
        Assert.False(byProvider["agent-job"].IsTruncated);

        // 擲出例外的那一個沒掃完，但沒有人說得出它「讀不到什麼」。
        Assert.False(byProvider["broken"].IsUnavailable);
        Assert.Null(byProvider["broken"].UnavailableReason);
        Assert.True(byProvider["broken"].IsTruncated);
    }

    /// <summary>
    /// 一個目標讀不到不讓這個來源停下來。
    /// </summary>
    /// <remarks>
    /// 目錄那一邊是每個資料庫一條執行緒；讓 sink 因此進入用盡狀態的症狀是使用者勾了
    /// 五個資料庫、斷了第一個，剩下四個連掃都沒掃。
    /// </remarks>
    [Fact]
    public async Task 讀不到之後這個來源照樣推得進結果()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.ReportUnavailable("「LibArchive」這一輪讀不到。");

            Assert.False(sink.IsExhausted);
            Assert.True(sink.TryReport(Hit("catalog", "Loan", 90)));
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.Equal("Loan", Assert.Single(results.Hits).Title);
        Assert.True(Assert.Single(results.Progress).IsUnavailable);
        Assert.True(results.IsPartial);
    }

    /// <summary>同一輪說第二次時留著第一句；後到的覆蓋先到的話，那句話由賽跑決定。</summary>
    [Fact]
    public async Task 同一個來源說第二次時留著第一句()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.ReportUnavailable("先說的那一句。");
            sink.ReportUnavailable("後說的那一句。");
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.Equal("先說的那一句。", Assert.Single(results.Progress).UnavailableReason);
    }

    /// <summary>
    /// 種類跟著那一句話一起出去；「權限不足」那個抬頭的唯一依據。
    /// </summary>
    /// <remarks>
    /// 只有一句給人看的話的那一版，呈現那一層要嘛一律說「這一輪讀不到」（權限問題永遠
    /// 說不出口），要嘛去比對字串（每多一個來源就多一條 <c>if</c>，漏掉的那一個安靜地
    /// 退回泛用那一句）。
    /// </remarks>
    [Fact]
    public async Task 讀不到的種類跟著那一句話一起出去()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.ReportUnavailable("「LibArchive」這一輪讀不到（這個登入對它沒有權限）。",
                SearchUnavailableKind.Denied);
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);
        var progress = Assert.Single(results.Progress);

        Assert.True(progress.IsUnavailable);
        Assert.Equal(SearchUnavailableKind.Denied, progress.UnavailableKind);
        Assert.True(progress.IsDenied);
    }

    /// <summary>不說種類的 provider 拿到 <c>Unknown</c>，不是「沒有讀不到」。</summary>
    [Fact]
    public async Task 不說種類時是說不出來而不是沒有讀不到()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.ReportUnavailable("「LibArchive」這一輪讀不到。");
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);
        var progress = Assert.Single(results.Progress);

        Assert.True(progress.IsUnavailable);
        Assert.Equal(SearchUnavailableKind.Unknown, progress.UnavailableKind);

        // 兩個屬性同值而意思不同，所以要問得出差別：讀不到但說不出是哪一種。
        Assert.False(progress.IsDenied);
    }

    /// <summary>
    /// 同一個 provider 說了兩次而種類不同時退回 <c>Unknown</c>，不猜。
    /// </summary>
    /// <remarks>
    /// 句子留第一句而種類退回，兩條規則刻意相反：句子是給人看的，留哪一句都說得通；
    /// 種類是一句斷言，而「一個沒權限、一個連不上」的下一步不是「去要權限」。
    /// 留第一個說的那一版，交出去的斷言由賽跑決定。
    /// </remarks>
    [Fact]
    public async Task 同一個來源說了兩種原因時退回說不出來()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.ReportUnavailable("先說的那一句。", SearchUnavailableKind.Denied);
            sink.ReportUnavailable("後說的那一句。", SearchUnavailableKind.Unknown);
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);
        var progress = Assert.Single(results.Progress);

        Assert.Equal("先說的那一句。", progress.UnavailableReason);
        Assert.Equal(SearchUnavailableKind.Unknown, progress.UnavailableKind);
    }

    /// <summary>兩次說的種類一樣就留著；退回只在說法真的分岔時發生。</summary>
    [Fact]
    public async Task 兩次說的種類一樣時留著()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.ReportUnavailable("「LibArchive」沒有權限。", SearchUnavailableKind.Denied);
            sink.ReportUnavailable("「LibMirror」沒有權限。", SearchUnavailableKind.Denied);
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        Assert.Equal(SearchUnavailableKind.Denied, Assert.Single(results.Progress).UnavailableKind);
    }

    /// <summary>說不出原因的「讀不到」與泛用的「部分結果」在畫面上一模一樣，所以不准。</summary>
    [Fact]
    public async Task 沒有原因的讀不到是程式錯誤()
    {
        var aggregator = Aggregate(new FakeSearchProvider("catalog", (query, sink, cancellationToken) =>
        {
            sink.ReportUnavailable("");
            return Task.CompletedTask;
        }));

        var results = await aggregator.SearchAsync(new SearchQuery("Loan"), CancellationToken.None);

        // 參數違約照樣被聚合器隔離成一次來源失敗（那是所有例外的路徑），但它是失敗，
        // 不是安靜地記成一次沒有原因的「讀不到」。
        Assert.IsType<ArgumentException>(Assert.Single(results.Failures).Exception);
        Assert.False(Assert.Single(results.Progress).IsUnavailable);
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
