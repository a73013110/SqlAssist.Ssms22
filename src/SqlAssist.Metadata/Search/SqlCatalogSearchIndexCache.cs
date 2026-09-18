using System;
using System.Collections.Generic;
using System.Threading;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 以連線快取鍵為鍵的搜尋索引快取，有數量上限。
/// </summary>
/// <remarks>
/// <b>只索引呼叫端明確指名的資料庫。</b>把每一個 <c>HAS_DBACCESS</c> 進得去的資料庫都
/// 先建一份索引的話，共用主機上等於幾十輪全表掃描與幾十份常駐索引，而其中九成九
/// 不會有人搜；這一條與第一層快照的「不預先載入」是同一條規則，只是這裡一份索引
/// 比一份快照貴得多——它含定義本文。
///
/// 上限要涵蓋的是「這一段工作裡手邊會提到的資料庫」，不是「這台伺服器上有幾個」；
/// 滿了就淘汰最久沒用到的那一份。
///
/// 鍵一律問 <see cref="ISqlConnectionSource.CacheKey"/>，不自己拼字串：拼法一旦與
/// <see cref="SqlConnectionCacheKey"/> 分岔，同一個資料庫就會拿到兩份索引——
/// 掃描次數加倍，而兩份的新舊各走各的。
/// </remarks>
public sealed class SqlCatalogSearchIndexCache
{
    /// <summary>同時留幾份索引。</summary>
    public const int DefaultCapacity = 4;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly long _maxDefinitionBytes;
    private long _clock;
    private int _builds;

    public SqlCatalogSearchIndexCache(
        int capacity = DefaultCapacity,
        long maxDefinitionBytes = SqlCatalogSearchIndex.DefaultMaxDefinitionBytes)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maxDefinitionBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDefinitionBytes));
        }

        Capacity = capacity;
        _maxDefinitionBytes = maxDefinitionBytes;
    }

    public int Capacity { get; }

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

    /// <summary>建過幾次索引；重建與否測得出來，靠的是這個數字而不是結果筆數。</summary>
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
    /// <remarks>
    /// 整段在同一把鎖裡，包括建索引那一次資料庫往返。放掉鎖再建的話，兩條同時搜尋的
    /// 執行緒會對同一個資料庫各掃一次全表——那是這一層最貴的一件事，而兩份結果一模一樣。
    /// 代價是不同資料庫的索引不會並行建立；一輪搜尋通常只碰一兩個資料庫，
    /// 用這個代價換掉「同一份被建兩次」是划算的。
    ///
    /// <b>失敗不進快取。</b>否則連線恢復之後仍然拿到空的，而空的索引與
    /// 「這個資料庫真的沒有東西」在畫面上長得一模一樣。
    /// </remarks>
    public SqlCatalogSearchIndex? GetOrBuild(ISqlConnectionSource connectionSource, CancellationToken cancellationToken)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        var key = connectionSource.CacheKey;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                cached.UsedAt = ++_clock;
                return cached.Index;
            }

            _builds++;
            var index = SqlCatalogSearchIndex.TryBuild(
                connectionSource, cancellationToken, _maxDefinitionBytes);

            if (index is null)
            {
                return null;
            }

            _entries[key] = new Entry(index, ++_clock);
            EvictExcess();
            return index;
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

    /// <summary>整批丟掉；使用者按重新整理時就是在說「我知道它舊了」。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <remarks>
    /// 一次只會多出一份，所以掃一遍找最舊的就夠，不必為此維護一條串列——
    /// 上限是個位數，而這段一輪搜尋最多跑一次。
    /// </remarks>
    private void EvictExcess()
    {
        while (_entries.Count > Capacity)
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

            if (oldestKey is null)
            {
                return;
            }

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
    }
}
