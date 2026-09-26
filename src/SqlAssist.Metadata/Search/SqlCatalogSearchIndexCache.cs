using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 以連線快取鍵為鍵的搜尋索引快取，有位元組預算；建索引是與搜尋輪次分開的背景工作。
/// </summary>
/// <remarks>
/// <b>只索引呼叫端明確指名的資料庫。</b>把每一個 <c>HAS_DBACCESS</c> 進得去的資料庫都
/// 先建一份索引的話，共用主機上等於幾十輪全表掃描與幾十份常駐索引，而其中九成九
/// 不會有人搜。
///
/// <b>建索引不跟著搜尋輪次取消。</b>使用者每打一個字就取消一輪，而第一次建索引以秒計；
/// 跟著取消的話，建到一半的索引每一個字都被丟掉重來，大資料庫永遠建不完。所以建置是這裡
/// 自己的工作（<see cref="SqlCatalogSearchIndexBuild"/>），輪次只是在等它；同一個資料庫同時
/// 只有一份在建，後到的輪次接著等同一份。只有 <see cref="Clear"/>（換連線）與
/// <see cref="CancelBuilds"/>（使用者按停止）會真的停下建置。
///
/// 同時建幾份有上限（<see cref="MaxConcurrentBuilds"/>）：「全部資料庫」那一輪一次開幾十條
/// 連線做全表掃描，伺服器與執行緒集區都吃不消，而排著的那幾個也不會因此比較快。
///
/// 上限是<b>位元組</b>而不是份數：一份索引的大小差到三個數量級。滿了先把最久沒用到的那幾份的
/// <b>定義本文</b>讓出去（改由伺服器端比對，見 <see cref="SqlCatalogSearchIndex.TextOnServer"/>），
/// 名稱那一段留著；還不夠才整份淘汰，但<b>永遠留下至少一份</b>。只整份淘汰的那一版，
/// 資料庫多到放不下時每一輪都互相擠掉、每打一個字都在重建全表。
///
/// 鍵一律問 <see cref="ISqlConnectionSource.CacheKey"/>，不自己拼字串：拼法一旦與
/// <see cref="SqlConnectionCacheKey"/> 分岔，同一個資料庫就會拿到兩份索引。
/// </remarks>
public sealed class SqlCatalogSearchIndexCache
{
    /// <summary>所有索引加起來最多佔這麼多位元組。</summary>
    /// <remarks>
    /// 超過時不是少搜，而是把最舊那幾份的本文改到伺服器端比對，所以這個數字只決定
    /// 「多少個資料庫的本文比對不必來回伺服器」，不影響結果。
    /// </remarks>
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    /// <summary>同時建幾份索引。</summary>
    public const int MaxConcurrentBuilds = 4;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlCatalogSearchIndexBuild> _builds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxConcurrentBuilds);
    private readonly long _maxDefinitionBytes;
    private CancellationTokenSource _lifetime = new();
    private long _clock;
    private long _bytes;
    private int _buildCount;

    public SqlCatalogSearchIndexCache(
        long maxBytes = DefaultMaxBytes,
        long maxDefinitionBytes = SqlCatalogSearchIndex.DefaultMaxDefinitionBytes)
    {
        if (maxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        if (maxDefinitionBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDefinitionBytes));
        }

        MaxBytes = maxBytes;
        _maxDefinitionBytes = maxDefinitionBytes;
    }

    /// <summary>位元組預算。</summary>
    public long MaxBytes { get; }

    /// <summary>目前留著的索引大約佔多少位元組。</summary>
    public long Bytes
    {
        get
        {
            lock (_gate) return _bytes;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>向資料庫要過幾次索引（建立、補定義本文與重新整理都算）。</summary>
    /// <remarks>
    /// 重建與否測得出來，靠的是這個數字而不是結果筆數：結果一樣的兩輪，一輪掃了全表、
    /// 一輪命中快取，在結果上一模一樣。
    /// </remarks>
    public int Builds
    {
        get
        {
            lock (_gate) return _buildCount;
        }
    }

    /// <summary>
    /// 拿這個來源的索引：手上有就是已完成的那一份，正在建就接著等，都沒有就開始建。
    /// </summary>
    /// <param name="includeDefinitions">
    /// 這一輪要不要定義本文。手上那一份只有第一段而這裡要第二段時，只補第二段，
    /// 不重掃第一段；正在建的那一份沒有第二段時，接在它後面補。
    /// </param>
    /// <remarks>
    /// <b>失敗不進快取。</b>否則連線恢復之後仍然拿到空的，而空的索引與
    /// 「這個資料庫真的沒有東西」在畫面上長得一模一樣。
    /// </remarks>
    public SqlCatalogSearchIndexBuild Build(ISqlConnectionSource connectionSource, bool includeDefinitions)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        var key = connectionSource.CacheKey;

        lock (_gate)
        {
            if (TryUse(key, includeDefinitions, out var ready)) return SqlCatalogSearchIndexBuild.Completed(ready!);

            if (_builds.TryGetValue(key, out var running) && (running.IncludesDefinitions || !includeDefinitions))
            {
                return running;
            }

            var build = new SqlCatalogSearchIndexBuild(includeDefinitions);
            _builds[key] = build;
            build.Start(RunAsync(key, connectionSource, build, running, _lifetime.Token));
            return build;
        }
    }

    /// <summary>
    /// 在建索引的同一組名額裡跑一件對伺服器的重工作（伺服器端比對本文）。
    /// </summary>
    /// <remarks>
    /// 與建索引共用名額：本文改到伺服器端的資料庫每一輪都要掃一次 <c>sys.sql_modules</c>，
    /// 「全部資料庫」那一輪若有幾十個這種資料庫，不限量的話就是每打一個字幾十條全表掃描同時送出。
    /// </remarks>
    public async Task<TResult> RunLimitedAsync<TResult>(Func<TResult> work, CancellationToken cancellationToken)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// 同步等那一份；資料庫說不行時回傳 null。
    /// </summary>
    /// <param name="cancellationToken">只取消<b>等待</b>，不取消建置本身。</param>
    /// <param name="unavailableKind">回傳 null 時是哪一種讀不到。</param>
    public SqlCatalogSearchIndex? GetOrBuild(
        ISqlConnectionSource connectionSource,
        bool includeDefinitions,
        CancellationToken cancellationToken,
        out SearchUnavailableKind unavailableKind)
    {
        var result = Build(connectionSource, includeDefinitions).Wait(cancellationToken);
        unavailableKind = result.UnavailableKind;
        return result.Index;
    }

    /// <summary>不問原因的那一版；只有「有沒有拿到」重要時用。</summary>
    public SqlCatalogSearchIndex? GetOrBuild(
        ISqlConnectionSource connectionSource, bool includeDefinitions, CancellationToken cancellationToken) =>
        GetOrBuild(connectionSource, includeDefinitions, cancellationToken, out _);

    /// <summary>
    /// 把手上每一份都標成過期；下一輪沿著版本戳只重撈變更過的東西。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Clear"/> 是兩件事。使用者按「重新整理」說的是「我知道它舊了」，不是
    /// 「這些東西全部不能用了」——保住沒有變更過的資料行與定義本文之後，改了一個預存程序
    /// 再按一次重新整理，付的是一條物件查詢加那一個程序的本文，而不是整個資料庫。
    /// </remarks>
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.IsStale = true;
            }
        }
    }

    /// <summary>已經建好的那一份；沒有時回傳 false。</summary>
    public bool TryGet(string cacheKey, out SqlCatalogSearchIndex? index)
    {
        if (cacheKey is null)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(cacheKey, out var cached))
            {
                index = cached.Index;
                return true;
            }

            index = null;
            return false;
        }
    }

    /// <summary>
    /// 手上這一份可以直接用嗎：有、而且沒有被標成過期。
    /// </summary>
    /// <remarks>
    /// 給呼叫端決定「這一輪要不要顯示建索引的進度」用的。答錯的代價只是多轉一圈或少轉一圈，
    /// 不影響結果——但把「標成過期、下一輪要重新整理」的那一份算成可以直接用，
    /// 症狀是使用者按了重新整理之後畫面靜止好幾秒，看起來像是按鈕沒有反應。
    /// </remarks>
    public bool IsFresh(string cacheKey)
    {
        if (cacheKey is null)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }

        lock (_gate)
        {
            return _entries.TryGetValue(cacheKey, out var cached) && !cached.IsStale;
        }
    }

    /// <summary>
    /// 停下正在建的每一份；已經建好的留著。
    /// </summary>
    /// <remarks>
    /// 使用者按停止時用：他要的是「現在別再掃了」，不是「把已經建好的也丟掉」。
    /// </remarks>
    public void CancelBuilds()
    {
        CancellationTokenSource previous;

        lock (_gate)
        {
            previous = _lifetime;
            _lifetime = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>
    /// 整批丟掉，連同正在建的。
    /// </summary>
    /// <remarks>
    /// 留給「換了一條連線」那種手上這一份根本不屬於這台伺服器的情形。使用者按「重新整理」
    /// 走的是 <see cref="Invalidate"/>——那一條保得住沒有變更過的東西。
    /// </remarks>
    public void Clear()
    {
        CancelBuilds();

        lock (_gate)
        {
            _entries.Clear();
            _bytes = 0;
        }
    }

    private async Task<SqlCatalogSearchIndexResult> RunAsync(
        string key,
        ISqlConnectionSource connectionSource,
        SqlCatalogSearchIndexBuild build,
        SqlCatalogSearchIndexBuild? prior,
        CancellationToken cancellationToken)
    {
        // 讓 Build 先回到呼叫端：它握著 _gate，這一段不能同步跑到要拿同一把鎖的地方。
        await Task.Yield();

        try
        {
            // 同一個資料庫只有一份在建：前一份沒有本文而這一份要，等它建完再補第二段。
            if (prior is not null)
            {
                try
                {
                    await prior.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                SqlCatalogSearchIndex? existing;
                bool stale;

                lock (_gate)
                {
                    // 排隊的那段時間，別的輪次可能已經建好了。
                    if (TryUse(key, build.IncludesDefinitions, out var ready)) return new SqlCatalogSearchIndexResult(ready, default);

                    var cached = _entries.TryGetValue(key, out var entry) ? entry : null;
                    existing = cached?.Index;
                    stale = cached?.IsStale ?? false;
                    _buildCount++;
                }

                var result = await Task.Run(
                    () => BuildCore(connectionSource, existing, stale, build.IncludesDefinitions, build.Report, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                Store(key, result.Index);
                return result;
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_builds.TryGetValue(key, out var current) && ReferenceEquals(current, build)) _builds.Remove(key);
            }
        }
    }

    /// <summary>
    /// 手上這一份要走哪一條：從頭建、只補第二段，還是沿著版本戳重新整理。
    /// </summary>
    /// <remarks>
    /// 已經標記過期的那一份不從頭建：整份重建會把上一輪已經撈回來、而且一個字都沒變的
    /// 定義本文再撈一次，而那是整條路徑上最貴的一段。
    /// </remarks>
    private SqlCatalogSearchIndexResult BuildCore(
        ISqlConnectionSource connectionSource,
        SqlCatalogSearchIndex? existing,
        bool stale,
        bool includeDefinitions,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        SearchUnavailableKind kind;
        SqlCatalogSearchIndex? index;

        if (existing is null)
        {
            index = SqlCatalogSearchIndex.TryBuild(
                connectionSource, includeDefinitions, cancellationToken, out kind, _maxDefinitionBytes,
                progress: progress);
        }
        else if (!stale)
        {
            index = existing.TryAddDefinitions(
                connectionSource, cancellationToken, out kind, _maxDefinitionBytes, progress: progress);
        }
        else
        {
            // 手上已經有第二段（或本文改在伺服器端比對）時，重新整理照樣帶著走：這一輪雖然只問名稱，
            // 下一輪要本文時才不會因為剛剛丟掉而重撈一次。
            index = SqlCatalogSearchIndex.TryRefresh(
                existing, connectionSource,
                includeDefinitions || existing.Definitions is not null || existing.TextOnServer,
                cancellationToken, out kind, _maxDefinitionBytes, progress: progress);
        }

        return new SqlCatalogSearchIndexResult(index, kind);
    }

    /// <summary>呼叫端必須持有 <see cref="_gate"/>。</summary>
    private bool TryUse(string key, bool includeDefinitions, out SqlCatalogSearchIndex? index)
    {
        index = null;

        if (!_entries.TryGetValue(key, out var cached)) return false;

        // 已經被標成過期：這一輪要沿著版本戳重新整理，不是直接用。
        if (cached.IsStale) return false;

        // 手上這一份只有第一段，而這一輪要第二段：這不算命中，呼叫端得去補。
        // 本文改在伺服器端比對的那一份算命中：它的本文本來就不在記憶體裡。
        if (includeDefinitions && cached.Index.Definitions is null && !cached.Index.TextOnServer) return false;

        cached.UsedAt = ++_clock;
        index = cached.Index;
        return true;
    }

    private void Store(string key, SqlCatalogSearchIndex? index)
    {
        if (index is null) return;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var previous)) _bytes -= previous.Index.ApproximateBytes;

            _entries[key] = new Entry(index, ++_clock);
            _bytes += index.ApproximateBytes;
            EvictExcess();
        }
    }

    /// <remarks>
    /// 先讓本文：最久沒用到、而且本文還在記憶體裡的那一份改成伺服器端比對，名稱那一段留著。
    /// 本文佔了索引的絕大部分，讓掉它通常就夠；名稱留著，那個資料庫下一輪不必重掃全表。
    /// 還不夠才整份淘汰，而且留下最後一份：一份比預算還大的索引仍然要能用。
    ///
    /// 掃一遍找最舊的就夠，不必為此維護一條串列——快取裡的份數是使用者這一段工作裡提到的
    /// 資料庫數。
    /// </remarks>
    private void EvictExcess()
    {
        while (_bytes > MaxBytes)
        {
            var oldest = Oldest(withDefinitions: true) ?? (_entries.Count > 1 ? Oldest(withDefinitions: false) : null);
            if (oldest is null) return;

            var entry = _entries[oldest];
            _bytes -= entry.Index.ApproximateBytes;

            if (entry.Index.Definitions is not null)
            {
                entry.Index = entry.Index.WithTextOnServer();
                _bytes += entry.Index.ApproximateBytes;
            }
            else
            {
                _entries.Remove(oldest);
            }
        }
    }

    private string? Oldest(bool withDefinitions)
    {
        string? oldestKey = null;
        var oldestUsedAt = long.MaxValue;

        foreach (var pair in _entries)
        {
            if (withDefinitions && pair.Value.Index.Definitions is null) continue;
            if (pair.Value.UsedAt >= oldestUsedAt) continue;

            oldestUsedAt = pair.Value.UsedAt;
            oldestKey = pair.Key;
        }

        return oldestKey;
    }

    private sealed class Entry
    {
        internal Entry(SqlCatalogSearchIndex index, long usedAt)
        {
            Index = index;
            UsedAt = usedAt;
        }

        internal SqlCatalogSearchIndex Index { get; set; }

        /// <summary>最後一次被讀到是第幾號動作；淘汰時比這個。</summary>
        internal long UsedAt { get; set; }

        /// <summary>使用者說過它舊了；下一次用到時沿著版本戳重新整理。</summary>
        internal bool IsStale { get; set; }
    }
}

/// <summary>一份索引建置的結果：拿到的索引，或讀不到的原因。</summary>
public readonly struct SqlCatalogSearchIndexResult
{
    internal SqlCatalogSearchIndexResult(SqlCatalogSearchIndex? index, SearchUnavailableKind unavailableKind)
    {
        Index = index;
        UnavailableKind = unavailableKind;
    }

    /// <summary>資料庫說不行時為 null。</summary>
    public SqlCatalogSearchIndex? Index { get; }

    /// <summary><see cref="Index"/> 是 null 時是哪一種讀不到。</summary>
    public SearchUnavailableKind UnavailableKind { get; }
}

/// <summary>
/// 一份正在建（或已經建好）的索引；好幾輪搜尋可以同時等同一份。
/// </summary>
/// <remarks>
/// 進度是一個數字而不是事件：建索引的執行緒一秒讀上萬列，每一列發一次事件等於把等它的
/// 每一輪都叫醒；等的人自己隔一小段時間讀一次。
/// </remarks>
public sealed class SqlCatalogSearchIndexBuild
{
    private Task<SqlCatalogSearchIndexResult>? _task;
    private double _progress;

    internal SqlCatalogSearchIndexBuild(bool includesDefinitions)
    {
        IncludesDefinitions = includesDefinitions;
    }

    /// <summary>這一份會帶著定義本文（或把本文改到伺服器端比對）。</summary>
    public bool IncludesDefinitions { get; }

    /// <summary>建完的結果；被 <see cref="SqlCatalogSearchIndexCache.CancelBuilds"/> 停下時是已取消的工作。</summary>
    public Task<SqlCatalogSearchIndexResult> Task => _task ?? throw new InvalidOperationException();

    /// <summary>0 到 1；已經建好的是 1。</summary>
    public double Progress => _task is { IsCompleted: true } ? 1 : Volatile.Read(ref _progress);

    internal static SqlCatalogSearchIndexBuild Completed(SqlCatalogSearchIndex index)
    {
        var build = new SqlCatalogSearchIndexBuild(index.Definitions is not null || index.TextOnServer);
        build.Start(System.Threading.Tasks.Task.FromResult(new SqlCatalogSearchIndexResult(index, default)));
        return build;
    }

    internal void Start(Task<SqlCatalogSearchIndexResult> task) => _task = task;

    internal void Report(double fraction) => Volatile.Write(ref _progress, fraction);

    /// <summary>同步等；取消的是等待，不是建置。</summary>
    internal SqlCatalogSearchIndexResult Wait(CancellationToken cancellationToken)
    {
        try
        {
            Task.Wait(cancellationToken);
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException canceled)
        {
            // 建置被停下：對等的人來說與自己被取消是同一件事，而不是一個包了一層的例外。
            ExceptionDispatchInfo.Capture(canceled).Throw();
        }

        return Task.Result;
    }
}
