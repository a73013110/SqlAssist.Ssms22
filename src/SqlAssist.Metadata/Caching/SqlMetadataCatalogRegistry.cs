using System;
using System.Collections.Generic;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Caching;

/// <summary>
/// 依快取鍵共用 <see cref="SqlMetadataCatalog"/>。
/// </summary>
/// <remarks>
/// 先前每個查詢視窗各自建立一份中繼資料提供者，同一個資料庫開了五個分頁
/// 就會查五次。改為以「伺服器＋資料庫」為單位共用，開新分頁時可以直接命中快取。
/// </remarks>
public sealed class SqlMetadataCatalogRegistry
{
    /// <summary>
    /// 跨資料庫目錄的數量上限。
    /// </summary>
    /// <remarks>
    /// 編輯器連線的目錄數量由使用者開了幾條連線決定，本來就不會多；跨資料庫的
    /// 目錄卻是<b>使用者打字打出來的</b>，一路打下去可以把這台伺服器上每一個
    /// 進得去的資料庫都建一份。而第一層快照是常駐的、一個資料庫動輒幾千列，
    /// 沒有上限就是一條隨著輸入成長的記憶體。
    ///
    /// 挑八個是因為它要涵蓋的是「這一段工作裡手邊會提到的資料庫」，
    /// 不是「這台伺服器上有幾個資料庫」。超過就淘汰最久沒用到的那一個，
    /// 代價是下次再打到它要重查一輪——那是背景查詢，不擋按鍵。
    /// </remarks>
    private const int MaximumScopedCatalogs = 8;

    public static readonly SqlMetadataCatalogRegistry Default = new();

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Entry> _catalogs = new(StringComparer.Ordinal);
    private long _clock;

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 取得查詢視窗自己那條連線的目錄。
    /// </summary>
    /// <remarks>
    /// 這樣拿到的目錄<b>不會</b>被淘汰：它是使用者正在看的那個資料庫，
    /// 淘汰它等於讓下一次按鍵重查一輪，而那正是這個註冊表要避免的事。
    /// </remarks>
    public SqlMetadataCatalog GetOrCreate(ISqlConnectionSource connectionSource)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        lock (_syncRoot)
        {
            if (_catalogs.TryGetValue(connectionSource.CacheKey, out var existing))
            {
                // 已經有目錄在用同一個連線，這一份不會有人用到。
                // 由這裡負責釋放，呼叫端就不必判斷自己交出去的那份還在不在被共用——
                // 先前呼叫端會自行釋放，結果關掉一個查詢視窗就讓其他視窗的目錄失效。
                (connectionSource as IDisposable)?.Dispose();

                // 跨資料庫先建過同一個資料庫的目錄時就在這裡改掛成編輯器的，
                // 之後不再被淘汰；重建一份的話同一個資料庫會被查兩輪。
                existing.IsPinned = true;
                existing.LastUsed = ++_clock;
                return existing.Catalog;
            }

            return Add(connectionSource, isPinned: true).Catalog;
        }
    }

    /// <summary>
    /// 取得同一台伺服器上<b>另一個資料庫</b>的目錄。
    /// </summary>
    /// <remarks>
    /// 查詢一律寫成不加限定的 <c>sys.objects</c>，所以換資料庫換的是連線而不是 SQL
    /// ——整套查詢、分層與失敗降級都照舊，這裡只多一層指向別的資料庫的連線來源。
    /// </remarks>
    public SqlMetadataCatalog GetOrCreateFor(ISqlConnectionSource connectionSource, string databaseName)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("資料庫名稱不可為空。", nameof(databaseName));
        }

        var cacheKey = SqlConnectionCacheKey.Compose(connectionSource.ServerCacheKey, databaseName);

        lock (_syncRoot)
        {
            if (_catalogs.TryGetValue(cacheKey, out var existing))
            {
                existing.LastUsed = ++_clock;
                return existing.Catalog;
            }

            var added = Add(new SqlDatabaseScopedConnectionSource(connectionSource, databaseName), isPinned: false);
            EvictScopedOverflow();
            return added.Catalog;
        }
    }

    /// <summary>清空所有目錄的快取，但保留實例，避免正在使用的呼叫端拿到孤兒物件。</summary>
    public void InvalidateAll()
    {
        lock (_syncRoot)
        {
            foreach (var entry in _catalogs.Values)
            {
                entry.Catalog.Invalidate();
            }
        }
    }

    private Entry Add(ISqlConnectionSource connectionSource, bool isPinned)
    {
        var entry = new Entry(
            new SqlMetadataCatalog(connectionSource, Lifetime, CommandTimeoutSeconds),
            connectionSource.CacheKey)
        {
            IsPinned = isPinned,
            LastUsed = ++_clock
        };

        _catalogs[entry.CacheKey] = entry;
        return entry;
    }

    /// <summary>淘汰最久沒用到的跨資料庫目錄；編輯器連線的目錄不在淘汰範圍內。</summary>
    private void EvictScopedOverflow()
    {
        var scoped = 0;

        foreach (var entry in _catalogs.Values)
        {
            if (!entry.IsPinned)
            {
                scoped++;
            }
        }

        while (scoped > MaximumScopedCatalogs)
        {
            Entry? oldest = null;

            foreach (var entry in _catalogs.Values)
            {
                if (!entry.IsPinned && (oldest is null || entry.LastUsed < oldest.LastUsed))
                {
                    oldest = entry;
                }
            }

            if (oldest is null)
            {
                return;
            }

            _catalogs.Remove(oldest.CacheKey);
            scoped--;
        }
    }

    private sealed class Entry
    {
        public Entry(SqlMetadataCatalog catalog, string cacheKey)
        {
            Catalog = catalog;
            CacheKey = cacheKey;
        }

        public SqlMetadataCatalog Catalog { get; }

        public string CacheKey { get; }

        /// <summary>是查詢視窗自己那條連線的目錄，不參與淘汰。</summary>
        public bool IsPinned { get; set; }

        public long LastUsed { get; set; }
    }
}
