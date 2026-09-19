using System;
using System.Collections.Generic;
using System.Threading;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 以連線快取鍵為鍵的搜尋索引快取，有位元組預算。
/// </summary>
/// <remarks>
/// <b>只索引呼叫端明確指名的資料庫。</b>把每一個 <c>HAS_DBACCESS</c> 進得去的資料庫都
/// 先建一份索引的話，共用主機上等於幾十輪全表掃描與幾十份常駐索引，而其中九成九
/// 不會有人搜；這一條與第一層快照的「不預先載入」是同一條規則，只是這裡一份索引
/// 比一份快照貴得多——它含定義本文。
///
/// 上限是<b>位元組</b>而不是份數。照份數算（「同時留四份」）的症狀是四個大庫就把 SSMS 的
/// 行程撐爆，而四個小庫又白白丟掉明明留得住的東西——一份索引的大小差到三個數量級，
/// 份數與記憶體之間沒有關係。滿了就淘汰最久沒用到的那一份，但<b>永遠留下至少一份</b>：
/// 一份比預算還大的索引仍然要能用，否則使用者對那個資料庫的每一輪搜尋都在重建。
///
/// 鍵一律問 <see cref="ISqlConnectionSource.CacheKey"/>，不自己拼字串：拼法一旦與
/// <see cref="SqlConnectionCacheKey"/> 分岔，同一個資料庫就會拿到兩份索引——
/// 掃描次數加倍，而兩份的新舊各走各的。
/// </remarks>
public sealed class SqlCatalogSearchIndexCache
{
    /// <summary>所有索引加起來最多佔這麼多位元組。</summary>
    /// <remarks>
    /// 一份含定義本文的索引上限是
    /// <see cref="SqlCatalogSearchIndex.DefaultMaxDefinitionBytes"/>（64 MiB）加上名稱那一段，
    /// 所以這個數字大約放得下三個「本文撈到滿」的大資料庫，或十幾個一般大小的。
    /// </remarks>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>每一個快取鍵自己的建置鎖；理由見 <see cref="GetOrBuild"/>。</summary>
    private readonly Dictionary<string, object> _buildGates = new(StringComparer.Ordinal);

    private readonly long _maxDefinitionBytes;
    private long _clock;
    private long _bytes;
    private int _builds;

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
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
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
            lock (_gate)
            {
                return _builds;
            }
        }
    }

    /// <summary>
    /// 拿這個來源的索引，沒有就建一份；資料庫說不行時回傳 null。
    /// </summary>
    /// <param name="includeDefinitions">
    /// 這一輪要不要定義本文。手上那一份只有第一段而這裡要第二段時，只補第二段，
    /// 不重掃第一段。
    /// </param>
    /// <remarks>
    /// <b>鎖只鎖同一個快取鍵。</b>整份快取一把鎖的話，使用者勾了五個資料庫的那一輪會排成
    /// 一列——第二個資料庫要等第一個掃完全表才開始，而它們用的是不同的連線、不同的伺服器
    /// 工作負載，本來可以一起跑。改成每個鍵一把之後，同一個資料庫仍然只會被建一次
    /// （兩條同時進來的執行緒之中，後到的那一條在鎖後面等，醒來就拿到快取）。
    ///
    /// 鎖的順序永遠是「先拿鍵鎖、再拿 <see cref="_gate"/>」，不會反過來；反過來的一條路徑
    /// 就是一個死結，而死結在搜尋上的症狀是工具窗整個不動，沒有任何訊息。
    ///
    /// <b>失敗不進快取。</b>否則連線恢復之後仍然拿到空的，而空的索引與
    /// 「這個資料庫真的沒有東西」在畫面上長得一模一樣。
    /// </remarks>
    public SqlCatalogSearchIndex? GetOrBuild(
        ISqlConnectionSource connectionSource, bool includeDefinitions, CancellationToken cancellationToken)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        var key = connectionSource.CacheKey;
        object gate;

        lock (_gate)
        {
            if (TryUse(key, includeDefinitions, out var ready)) return ready;
            gate = BuildGate(key);
        }

        lock (gate)
        {
            SqlCatalogSearchIndex? existing;
            bool stale;

            lock (_gate)
            {
                // 在鎖後面等的那段時間，先到的那一條可能已經建好了。
                if (TryUse(key, includeDefinitions, out var ready)) return ready;

                var cached = _entries.TryGetValue(key, out var entry) ? entry : null;
                existing = cached?.Index;
                stale = cached?.IsStale ?? false;
                _builds++;
            }

            var index = Build(connectionSource, existing, stale, includeDefinitions, cancellationToken);

            return Store(key, index);
        }
    }

    /// <summary>
    /// 手上這一份要走哪一條：從頭建、只補第二段，還是沿著版本戳重新整理。
    /// </summary>
    /// <remarks>
    /// 已經標記過期的那一份不從頭建：整份重建會把上一輪已經撈回來、而且一個字都沒變的
    /// 定義本文再撈一次，而那是整條路徑上最貴的一段。
    /// </remarks>
    private SqlCatalogSearchIndex? Build(
        ISqlConnectionSource connectionSource,
        SqlCatalogSearchIndex? existing,
        bool stale,
        bool includeDefinitions,
        CancellationToken cancellationToken)
    {
        if (existing is null)
        {
            return SqlCatalogSearchIndex.TryBuild(
                connectionSource, includeDefinitions, cancellationToken, _maxDefinitionBytes);
        }

        if (!stale)
        {
            return existing.TryAddDefinitions(connectionSource, cancellationToken, _maxDefinitionBytes);
        }

        // 手上已經有第二段時，重新整理照樣把它帶著走：這一輪雖然只問名稱，下一輪要本文時
        // 才不會因為剛剛丟掉而重撈一次。
        return SqlCatalogSearchIndex.TryRefresh(
            existing, connectionSource, includeDefinitions || existing.Definitions is not null,
            cancellationToken, _maxDefinitionBytes);
    }

    /// <summary>
    /// 把手上每一份都標成過期；下一輪沿著版本戳只重撈變更過的東西。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Clear"/> 是兩件事。使用者按「重新整理」說的是「我知道它舊了」，不是
    /// 「這些東西全部不能用了」——保住沒有變更過的資料行與定義本文之後，改了一個預存程序
    /// 再按一次重新整理，付的是一條物件查詢加那一個程序的本文，而不是整個資料庫。
    /// <see cref="Clear"/> 留給「換了一條連線」那種手上這一份根本不屬於這台伺服器的情形。
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
    /// 給呼叫端決定「這一輪要不要顯示載入表面」用的。答錯的代價只是多轉一圈或少轉一圈的
    /// 載入圖示，不影響結果——但把「標成過期、下一輪要重新整理」的那一份算成可以直接用，
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
    /// 整批丟掉。
    /// </summary>
    /// <remarks>
    /// 留給「換了一條連線」那種手上這一份根本不屬於這台伺服器的情形。使用者按「重新整理」
    /// 走的是 <see cref="Invalidate"/>——那一條保得住沒有變更過的東西。
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _bytes = 0;
        }

        // 建置鎖<b>不</b>跟著清掉：正在建的那一條執行緒手上還握著它，換一顆新的等於同一個
        // 資料庫可以被兩條執行緒同時掃全表。留著的代價是每一個碰過的資料庫多一個空物件，
        // 而那個數量是使用者這一段工作裡提到的資料庫數。
    }

    /// <summary>呼叫端必須持有 <see cref="_gate"/>。</summary>
    private bool TryUse(string key, bool includeDefinitions, out SqlCatalogSearchIndex? index)
    {
        index = null;

        if (!_entries.TryGetValue(key, out var cached)) return false;

        // 已經被標成過期：這一輪要沿著版本戳重新整理，不是直接用。
        if (cached.IsStale) return false;

        // 手上這一份只有第一段，而這一輪要第二段：這不算命中，呼叫端得去補。
        if (includeDefinitions && cached.Index.Definitions is null) return false;

        cached.UsedAt = ++_clock;
        index = cached.Index;
        return true;
    }

    /// <summary>呼叫端必須持有 <see cref="_gate"/>。</summary>
    private object BuildGate(string key)
    {
        if (_buildGates.TryGetValue(key, out var gate)) return gate;

        gate = new object();
        _buildGates[key] = gate;
        return gate;
    }

    private SqlCatalogSearchIndex? Store(string key, SqlCatalogSearchIndex? index)
    {
        if (index is null) return null;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var previous)) _bytes -= previous.Index.ApproximateBytes;

            _entries[key] = new Entry(index, ++_clock);
            _bytes += index.ApproximateBytes;
            EvictExcess();
        }

        return index;
    }

    /// <remarks>
    /// 掃一遍找最舊的就夠，不必為此維護一條串列——快取裡的份數是使用者這一段工作裡提到的
    /// 資料庫數，而這一段一輪搜尋最多跑一次。
    ///
    /// 留下最後一份：一份比預算還大的索引仍然要能用。丟掉它的話，使用者對那個資料庫的
    /// 每一輪搜尋都在重掃全表，而畫面上只看得出「搜尋很慢」。
    /// </remarks>
    private void EvictExcess()
    {
        while (_bytes > MaxBytes && _entries.Count > 1)
        {
            string? oldestKey = null;
            var oldestUsedAt = long.MaxValue;

            foreach (var pair in _entries)
            {
                if (pair.Value.UsedAt < oldestUsedAt)
                {
                    oldestUsedAt = pair.Value.UsedAt;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey is null) return;

            _bytes -= _entries[oldestKey].Index.ApproximateBytes;
            _entries.Remove(oldestKey);
        }
    }

    private sealed class Entry
    {
        internal Entry(SqlCatalogSearchIndex index, long usedAt)
        {
            Index = index;
            UsedAt = usedAt;
        }

        internal SqlCatalogSearchIndex Index { get; }

        /// <summary>最後一次被讀到是第幾號動作；淘汰時比這個。</summary>
        internal long UsedAt { get; set; }

        /// <summary>使用者說過它舊了；下一次用到時沿著版本戳重新整理。</summary>
        internal bool IsStale { get; set; }
    }
}
