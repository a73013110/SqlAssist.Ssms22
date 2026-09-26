using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.Search;

/// <summary>
/// 同時問所有 provider，把結果併成一份排好序、去過重的清單，並收齊每一個目標的完整度。
/// </summary>
/// <remarks>
/// 這一層只做四件事：失敗隔離、統一排名、去重、世代。任何一件散到 provider 去，
/// 症狀都是同一種——每加一個來源就多一份規則，而清單上的順序開始取決於誰先回來。
///
/// <b>沒有掃描預算。</b>原本的候選數、筆數與時間上限讓「全部資料庫 × 全部種類」那一輪在比到
/// 定義本文之前就用完額度，本文命中一筆都收不到，使用者據此以為字串不存在。現在每一輪都掃完，
/// 延遲靠取消控制（使用者多打一個字，這一輪就停）；唯一的上限是清單列得出幾筆
/// （<see cref="MaxHits"/>），裁的是排名之後的尾巴，筆數照實回報。
/// </remarks>
public sealed class SearchAggregator
{
    /// <summary>清單最多列幾筆；比這多時照實說總數。</summary>
    /// <remarks>
    /// 清單是虛擬化、分批套上的，幾千列不是問題；再多只是讓使用者捲不完，而總數仍然照實說。
    /// </remarks>
    public const int DefaultMaxHits = 5000;

    private static readonly SearchHitComparer Ranking = new();

    private readonly IReadOnlyList<ISearchProvider> _providers;

    /// <summary>目前已知最新的一輪；落後的結果整份丟棄。</summary>
    private long _generation = -1;

    [Localizable(false)]
    public SearchAggregator(IEnumerable<ISearchProvider> providers, int maxHits = DefaultMaxHits)
    {
        if (providers is null) throw new ArgumentNullException(nameof(providers));
        if (maxHits < 1) throw new ArgumentOutOfRangeException(nameof(maxHits));

        var copy = new List<ISearchProvider>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            if (provider is null) throw new ArgumentException("provider 不可為 null。", nameof(providers));

            // Id 重複會讓去重與同分排序失去唯一的打破平手依據，而且失敗摘要指不出是誰。
            if (!seen.Add(SearchArgument.Identifier(provider.Id, nameof(providers))))
                throw new ArgumentException($"provider Id 重複：{provider.Id}。", nameof(providers));

            copy.Add(provider);
        }

        _providers = copy.ToArray();
        MaxHits = maxHits;
        Categories = _providers.SelectMany(provider => provider.Categories).ToArray();
    }

    public IReadOnlyList<ISearchProvider> Providers => _providers;

    /// <summary>清單最多列幾筆。</summary>
    public int MaxHits { get; }

    /// <summary>所有來源宣告的分類，依 provider 順序串接；UI 產生過濾 pill 用。</summary>
    public IReadOnlyList<SearchCategory> Categories { get; }

    /// <summary>跑完一輪並回傳排好序的結果；要邊跑邊看進度的呼叫端用 <see cref="Start"/>。</summary>
    public Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
        Start(query, cancellationToken).Completion;

    /// <summary>
    /// 開始一輪；回傳的那一份可以邊跑邊問進度與已經到的命中。
    /// </summary>
    /// <remarks>
    /// <b>取消不擲出。</b>取消是打字驅動搜尋的正常流程，讓每一個呼叫端包一層 try/catch
    /// 只會把正常的事寫成錯誤路徑。取消時 <see cref="SearchRun.Completion"/> 回傳已經收到的
    /// 部分，<see cref="SearchResults.IsCanceled"/> 為 true；provider 擲出的
    /// <see cref="OperationCanceledException"/> 在這裡被接住，也不計入失敗摘要。
    ///
    /// <b>落後的世代整份丟棄。</b><see cref="SearchQuery.Generation"/> 比已經開始過的那一輪小時
    /// 直接回 <see cref="SearchResults.IsStale"/>，連 provider 都不叫；正在跑的舊世代
    /// 則在下一次 <see cref="ISearchSink.TryReport"/> 收到 false 而停下來。
    /// </remarks>
    public SearchRun Start(SearchQuery query, CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));

        var run = new SearchRun(this, query, cancellationToken);

        run.Begin(TryAdvanceGeneration(query.Generation)
            ? RunAsync(run, query, cancellationToken)
            : Task.FromResult(SearchResults.Stale(query.Generation)));

        return run;
    }

    internal bool IsCurrent(long generation) => Volatile.Read(ref _generation) == generation;

    private async Task<SearchResults> RunAsync(SearchRun run, SearchQuery query, CancellationToken cancellationToken)
    {
        var sinks = new ProviderSink[_providers.Count];
        var runs = new Task<SearchProviderFailure?>[_providers.Count];

        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            var sink = new ProviderSink(run, provider.Id);
            sinks[index] = sink;
            runs[index] = RunProviderAsync(provider, query, sink, cancellationToken);
        }

        var failures = await Task.WhenAll(runs).ConfigureAwait(false);

        // 等的期間可能又開了新的一輪；這一份即使已經算完也不該蓋掉新的。
        if (!IsCurrent(query.Generation)) return SearchResults.Stale(query.Generation);

        return Combine(query, sinks, failures, cancellationToken);
    }

    /// <summary>
    /// 一個 provider 的執行，例外收成資料。
    /// </summary>
    /// <remarks>
    /// 靜態且不重擲：讓例外從 <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
    /// 冒出去，一個來源掛掉就會連帶取消整份結果，而其他來源明明已經算完了。
    /// 它宣告過、還沒說結局的目標在收尾時記成已取消，不會被當成完整。
    /// </remarks>
    private static async Task<SearchProviderFailure?> RunProviderAsync(
        ISearchProvider provider, SearchQuery query, ProviderSink sink, CancellationToken cancellationToken)
    {
        try
        {
            await provider.SearchAsync(query, sink, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return new SearchProviderFailure(provider.Id, provider.DisplayName, exception);
        }
    }

    private SearchResults Combine(
        SearchQuery query, ProviderSink[] sinks, SearchProviderFailure?[] failures, CancellationToken cancellationToken)
    {
        var collected = new List<SearchHit>();
        var targets = new List<SearchTargetStatus>();

        // 依 provider 順序收集，排序才有可重現的輸入順序：OrderBy 是穩定排序，
        // 而 provider 完成的先後是賽跑。
        foreach (var sink in sinks) sink.Drain(collected, targets);

        var ranked = Rank(collected);
        var total = ranked.Count;

        if (ranked.Count > MaxHits) ranked.RemoveRange(MaxHits, ranked.Count - MaxHits);

        var reported = new List<SearchProviderFailure>();

        foreach (var failure in failures)
        {
            if (failure is not null) reported.Add(failure);
        }

        return new SearchResults(
            query.Generation, ranked, total, isStale: false, cancellationToken.IsCancellationRequested, reported, targets);
    }

    /// <summary>排名並去重；邊跑邊看的那一份與最後的結果走同一條。</summary>
    internal static List<SearchHit> Rank(IEnumerable<SearchHit> hits) => Deduplicate(hits.OrderBy(hit => hit, Ranking));

    /// <summary>
    /// 依 <see cref="SearchHit.DedupeKey"/> 把同一個東西的幾種命中併成一列。
    /// </summary>
    /// <remarks>
    /// 在排好序的序列上走一遍，第一個看到的那一份當代表，其餘掛到它的
    /// <see cref="SearchHit.Merged"/> 上——也就是「代表的是排名最高的那一份」，
    /// 而且平手時由排序決定，不是先到的那一份（先到是賽跑的結果，會讓同一組輸入留下不同的代表）。
    ///
    /// 去重鍵<b>只有</b> <see cref="SearchHit.DedupeKey"/>，不含
    /// <see cref="SearchHit.MatchTarget"/>，也不含資料行名稱：同一張資料表被名稱、三個資料行與
    /// 定義本文命中時，那仍然是同一張表——五列指向同一個地方、點下去做同一件事，
    /// 而使用者看到的是清單上重複的五行。代表由 <see cref="SearchHitComparer"/> 決定，
    /// 它先比部位再比分數，所以名稱那一份在前。
    ///
    /// 被併掉的那幾筆<b>不丟</b>：一張只靠資料行命中的表，丟掉之後畫面上說不出它是靠哪幾行
    /// 進來的，而那正是使用者要找的東西。呈現與預覽讀的是
    /// <see cref="SearchHit.Matches"/>，Core 不決定要畫幾顆膠囊。
    ///
    /// 刻意<b>不</b>拿幾邊的分數取最大值或相加：名稱那邊是
    /// <see cref="SqlAssist.Core.Matching.FuzzyMatcher"/> 的詞首加成，本文那邊是出現次數，
    /// 兩個尺度湊出來的數字排出的順序沒有意義。代表那一份的分數就是這一列的分數。
    ///
    /// 併進來的清單一定是攤平的：候選本身的 <see cref="SearchHit.Merged"/> 在這一步之前
    /// 都是空的（provider 填不了它），所以不必遞迴。
    /// </remarks>
    private static List<SearchHit> Deduplicate(IEnumerable<SearchHit> ordered)
    {
        var kept = new List<SearchHit>();
        var merged = new List<List<SearchHit>?>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var hit in ordered)
        {
            if (at.TryGetValue(hit.DedupeKey, out var index))
            {
                // 併進來的那幾筆已經是排好序的，照順序 Add 就維持了排名先後。
                (merged[index] ??= new List<SearchHit>()).Add(hit);
                continue;
            }

            at.Add(hit.DedupeKey, kept.Count);
            kept.Add(hit);
            merged.Add(null);
        }

        for (var index = 0; index < kept.Count; index++)
        {
            if (merged[index] is { } group) kept[index] = kept[index].WithMerged(group);
        }

        return kept;
    }

    /// <summary>世代只增不減；相同世代可以重跑（例如手動重新整理）。</summary>
    private bool TryAdvanceGeneration(long generation)
    {
        while (true)
        {
            var current = Volatile.Read(ref _generation);
            if (generation < current) return false;
            if (generation == current) return true;
            if (Interlocked.CompareExchange(ref _generation, generation, current) == current) return true;
        }
    }

    /// <summary>
    /// 排名比較：先分組，再分數，最後才是打破平手的一串 ordinal 鍵。
    /// </summary>
    /// <remarks>
    /// 分組走 <see cref="SearchMatchTargets.GroupOrder"/> 而不是列舉值：
    /// <see cref="SearchMatchTarget.Column"/> 是後來從物件種類那條軸搬過來的，接在列舉最後
    /// 才不會改掉既有的值，但它在畫面上屬於名稱那一族。
    ///
    /// 平手鍵一路比到 <see cref="SearchHit.DedupeKey"/>，是因為只比到限定名稱時，
    /// 同一個名稱底下的兩筆（不同 provider、不同分類）順序仍然由賽跑決定。
    /// </remarks>
    private sealed class SearchHitComparer : IComparer<SearchHit>
    {
        public int Compare(SearchHit? left, SearchHit? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return 1;
            if (right is null) return -1;

            var byTarget = left.MatchTarget.GroupOrder().CompareTo(right.MatchTarget.GroupOrder());
            if (byTarget != 0) return byTarget;

            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0) return byScore;

            var bySortKey = string.CompareOrdinal(left.SortKey, right.SortKey);
            if (bySortKey != 0) return bySortKey;

            var byProvider = string.CompareOrdinal(left.ProviderId, right.ProviderId);
            if (byProvider != 0) return byProvider;

            var byCategory = string.CompareOrdinal(left.CategoryId, right.CategoryId);
            return byCategory != 0 ? byCategory : string.CompareOrdinal(left.DedupeKey, right.DedupeKey);
        }
    }

    /// <summary>
    /// 一個 provider 專用的 sink：自己的命中與目標，收尾時依 provider 順序倒進結果。
    /// </summary>
    /// <remarks>
    /// 每個 provider 一份，排序的輸入與目標清單的順序因此由 provider 的宣告順序決定，
    /// 不由誰先回來決定。邊跑邊看的命中另外記在 <see cref="SearchRun"/> 的到達順序上。
    /// </remarks>
    private sealed class ProviderSink : ISearchSink
    {
        private readonly object _gate = new();
        private readonly List<SearchHit> _hits = new();
        private readonly List<SearchTarget> _targets = new();
        private readonly SearchRun _run;
        private readonly string _providerId;

        internal ProviderSink(SearchRun run, string providerId)
        {
            _run = run;
            _providerId = providerId;
        }

        public bool TryReport(SearchHit hit)
        {
            if (hit is null) throw new ArgumentNullException(nameof(hit));

            // 已經有更新的一輪開始了，或這一輪被取消：結果反正會被丟棄，讓 provider 立刻停下來
            // 才不會繼續佔著連線與 CPU 去算沒有人會看的東西。
            if (!_run.IsLive) return false;

            // 分類過濾的最後一道。provider 自己濾掉最好，漏掉時 UI 不該看到不在 pill 裡的種類。
            // 被濾掉的不代表要停，所以仍然回 true。
            if (!_run.Query.MatchesCategory(hit.CategoryId)) return true;

            lock (_gate) _hits.Add(hit);
            _run.Arrive(hit);
            return true;
        }

        public SearchTarget AddTarget(string name, SearchTargetKind kind)
        {
            var target = new SearchTarget(_providerId, name, kind);
            lock (_gate) _targets.Add(target);
            _run.Track(target);
            return target;
        }

        internal void Drain(List<SearchHit> hits, List<SearchTargetStatus> targets)
        {
            lock (_gate)
            {
                hits.AddRange(_hits);
                foreach (var target in _targets) targets.Add(target.Snapshot(final: true));
            }
        }
    }
}
