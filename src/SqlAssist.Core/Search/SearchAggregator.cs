using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.Search;

/// <summary>
/// 同時問所有 provider，把結果併成一份排好序、去過重的清單。
/// </summary>
/// <remarks>
/// 這一層只做四件事：失敗隔離、統一排名、去重、預算與世代。任何一件散到 provider 去，
/// 症狀都是同一種——每加一個來源就多一份規則，而清單上的順序開始取決於誰先回來。
/// </remarks>
public sealed class SearchAggregator
{
    private static readonly SearchHitComparer Ranking = new();

    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly Func<TimeSpan> _clock;

    /// <summary>目前已知最新的一輪；落後的結果整份丟棄。</summary>
    private long _generation = -1;

    /// <param name="clock">
    /// 單調遞增的計時來源，只用來算預算；預設是這個聚合器自己的 <see cref="Stopwatch"/>。
    /// 可注入是為了讓時間預算測得出來——靠真的睡覺來測，測試會同時變慢與不穩定。
    /// </param>
    public SearchAggregator(IEnumerable<ISearchProvider> providers, SearchBudget? budget = null, Func<TimeSpan>? clock = null)
    {
        if (providers is null) throw new ArgumentNullException(nameof(providers));

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
        Budget = budget ?? SearchBudget.Default;

        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _clock = () => stopwatch.Elapsed;
        }
        else
        {
            _clock = clock;
        }

        Categories = _providers.SelectMany(provider => provider.Categories).ToArray();
    }

    public IReadOnlyList<ISearchProvider> Providers => _providers;

    public SearchBudget Budget { get; }

    /// <summary>所有來源宣告的分類，依 provider 順序串接；UI 產生過濾 pill 用。</summary>
    public IReadOnlyList<SearchCategory> Categories { get; }

    /// <summary>
    /// 跑完一輪並回傳排好序的結果。
    /// </summary>
    /// <remarks>
    /// <b>取消不擲出。</b>取消是打字驅動搜尋的正常流程（使用者又按了一個鍵），
    /// 讓每一個呼叫端包一層 try/catch 只會把正常的事寫成錯誤路徑。取消時回傳已經收到的
    /// 部分結果，<see cref="SearchResults.IsPartial"/> 為 true；provider 擲出的
    /// <see cref="OperationCanceledException"/> 在這裡被接住，也不計入失敗摘要。
    ///
    /// <b>落後的世代整份丟棄。</b><see cref="SearchQuery.Generation"/> 比已經開始過的那一輪小時
    /// 直接回 <see cref="SearchResults.IsStale"/>，連 provider 都不叫；正在跑的舊世代
    /// 則在下一次 <see cref="ISearchSink.TryReport"/> 收到 false 而停下來。
    /// </remarks>
    public async Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));

        if (!TryAdvanceGeneration(query.Generation)) return SearchResults.Stale(query.Generation);

        var started = _clock();
        var sinks = new BudgetedSink[_providers.Count];
        var runs = new Task<SearchProviderFailure?>[_providers.Count];

        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            var sink = new BudgetedSink(this, provider.Id, query, started);
            sinks[index] = sink;
            runs[index] = RunAsync(provider, query, sink, cancellationToken);
        }

        var failures = await Task.WhenAll(runs).ConfigureAwait(false);

        // 等的期間可能又開了新的一輪；這一份即使已經算完也不該蓋掉新的。
        if (Volatile.Read(ref _generation) != query.Generation) return SearchResults.Stale(query.Generation);

        return Combine(query, sinks, failures, cancellationToken);
    }

    /// <summary>
    /// 一個 provider 的執行，例外收成資料。
    /// </summary>
    /// <remarks>
    /// 靜態且不重擲：讓例外從 <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
    /// 冒出去，一個來源掛掉就會連帶取消整份結果，而其他來源明明已經算完了。
    /// </remarks>
    private static async Task<SearchProviderFailure?> RunAsync(
        ISearchProvider provider, SearchQuery query, BudgetedSink sink, CancellationToken cancellationToken)
    {
        try
        {
            await provider.SearchAsync(query, sink, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            // 取消不是故障，但這個來源確實沒掃完。
            sink.MarkTruncated();
            return null;
        }
        catch (Exception exception)
        {
            sink.MarkTruncated();
            return new SearchProviderFailure(provider.Id, provider.DisplayName, exception);
        }
    }

    private SearchResults Combine(
        SearchQuery query, BudgetedSink[] sinks, SearchProviderFailure?[] failures, CancellationToken cancellationToken)
    {
        var collected = new List<SearchHit>();
        var progress = new SearchProviderProgress[sinks.Length];
        var isPartial = cancellationToken.IsCancellationRequested;

        // 依 provider 順序收集，排序才有可重現的輸入順序：OrderBy 是穩定排序，
        // 而 provider 完成的先後是賽跑。
        for (var index = 0; index < sinks.Length; index++)
        {
            progress[index] = sinks[index].Drain(collected);

            // 讀不到也算部分：這一輪確實少了東西，而「少了一個來源」與「掃到一半停了」
            // 的差別由呼叫端讀 IsUnavailable 分開，不是靠這個布林。
            isPartial |= progress[index].IsTruncated || progress[index].IsUnavailable;
        }

        var ranked = Deduplicate(collected.OrderBy(hit => hit, Ranking));

        if (ranked.Count > Budget.MaxHits)
        {
            ranked.RemoveRange(Budget.MaxHits, ranked.Count - Budget.MaxHits);
            isPartial = true;
        }

        var reported = new List<SearchProviderFailure>();

        foreach (var failure in failures)
        {
            if (failure is not null) reported.Add(failure);
        }

        return new SearchResults(
            query.Generation, ranked, isPartial || reported.Count > 0, isStale: false, reported, progress);
    }

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
    /// 一個 provider 專用的 sink：自己的額度、自己的清單。
    /// </summary>
    /// <remarks>
    /// 每個 provider 一個實例而不是共用一個，除了可重現（見 <see cref="SearchBudget"/>）之外，
    /// 也讓鎖只在單一來源內競爭；共用一把鎖的話，最吵的那個來源會拖慢其他來源的回報。
    /// </remarks>
    private sealed class BudgetedSink : ISearchSink
    {
        private readonly object _gate = new();
        private readonly List<SearchHit> _hits = new();
        private readonly SearchAggregator _owner;
        private readonly SearchQuery _query;
        private readonly TimeSpan _started;
        private readonly string _providerId;
        private int _examined;
        private bool _truncated;
        private string? _checkpoint;
        private string? _unavailableReason;
        private SearchUnavailableKind _unavailableKind;

        internal BudgetedSink(SearchAggregator owner, string providerId, SearchQuery query, TimeSpan started)
        {
            _owner = owner;
            _providerId = providerId;
            _query = query;
            _started = started;
        }

        public bool IsExhausted
        {
            get
            {
                lock (_gate) return IsExhaustedCore();
            }
        }

        public bool TryReport(SearchHit hit)
        {
            if (hit is null) throw new ArgumentNullException(nameof(hit));

            lock (_gate)
            {
                if (IsExhaustedCore()) return false;

                // 分類過濾的最後一道。provider 自己濾掉最好，漏掉時 UI 不該看到不在 pill 裡的種類。
                // 被濾掉的不算額度也不代表要停，所以仍然回 true。
                if (!_query.MatchesCategory(hit.CategoryId)) return true;

                _hits.Add(hit);
                return true;
            }
        }

        public void ReportExamined(int candidates)
        {
            if (candidates < 0) throw new ArgumentOutOfRangeException(nameof(candidates));

            lock (_gate)
            {
                // 飽和加法：候選數是 provider 自己數的，溢位會讓計數變成負的而把預算變成無上限。
                _examined = candidates > int.MaxValue - _examined ? int.MaxValue : _examined + candidates;
            }
        }

        public void ReportTruncated(string? checkpoint = null)
        {
            lock (_gate)
            {
                _truncated = true;
                if (checkpoint is not null) _checkpoint = checkpoint;
            }
        }

        public void ReportUnavailable(string reason, SearchUnavailableKind kind = SearchUnavailableKind.Unknown)
        {
            SearchArgument.Reason(reason, nameof(reason));

            lock (_gate)
            {
                if (_unavailableReason is null)
                {
                    _unavailableReason = reason;
                    _unavailableKind = kind;
                }
                else if (_unavailableKind != kind)
                {
                    // 同一個 provider 說了兩次而種類不同（一個目標沒權限、另一個連不上）：
                    // 退回 Unknown，不猜。留第一個說的那一版等於斷言由賽跑決定，而斷成
                    // 「權限不足」的那一次會叫使用者去查一個好好的權限設定。
                    _unavailableKind = SearchUnavailableKind.Unknown;
                }

                // 句子與種類的合併規則相反：第一句留著（同一個 provider 可以把目標拆成
                // 幾條執行緒，目錄那一邊正是每個資料庫一條，後到的覆蓋先到的話交出去的
                // 句子由賽跑決定），而種類退到說得準的那一級。
            }

            // 刻意不碰 _truncated，也不讓 IsExhausted 變真：讀不到的是其中一個目標，
            // 而這個 provider 還有別的目標要掃。整輪算不算部分結果由 Combine 決定。
        }

        internal void MarkTruncated()
        {
            lock (_gate) _truncated = true;
        }

        /// <summary>把這個來源收到的結果倒進共用清單，並結算它的進度。</summary>
        internal SearchProviderProgress Drain(List<SearchHit> destination)
        {
            lock (_gate)
            {
                destination.AddRange(_hits);
                return new SearchProviderProgress(
                    _providerId, _examined, _hits.Count, _truncated, _checkpoint, _unavailableReason,
                    _unavailableKind);
            }
        }

        /// <summary>呼叫端必須持有 <see cref="_gate"/>。</summary>
        private bool IsExhaustedCore()
        {
            if (_truncated) return true;

            var budget = _owner.Budget;

            if (_hits.Count >= budget.MaxHitsPerProvider ||
                _examined >= budget.MaxCandidatesPerProvider ||
                _owner._clock() - _started >= budget.MaxDuration)
            {
                _truncated = true;
                return true;
            }

            // 已經有更新的一輪開始了：這一輪的結果反正會被丟棄，讓 provider 立刻停下來
            // 才不會繼續佔著連線與 CPU 去算沒有人會看的東西。這一條不標記部分結果，
            // 因為過期的結果整份不會出現。
            return Volatile.Read(ref _owner._generation) != _query.Generation;
        }
    }
}
