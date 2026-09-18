using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;
using SqlAssist.Metadata.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 工具窗要問哪幾個來源；之後加 SQL Memory、開啟中的分頁或片段 provider 只改這一支。
/// </summary>
/// <remarks>
/// 視窗持有的是 <see cref="SearchAggregator"/> 而不是某一個 provider：清單的排序、去重、
/// 預算與失敗隔離都在聚合器上，繞過它直接問 provider 的症狀是第二個來源加進來的那一天，
/// 順序開始取決於誰先回來。
///
/// 連線的處理是這一支最容易寫錯的地方。<b>禁止</b>持有 <see cref="ISqlConnectionSource"/>：
/// 所有權在 <see cref="SqlMetadataCatalogRegistry"/>，同一個快取鍵重複建立時多出來的那一份
/// 會當場釋放，而呼叫端分不出留下的是不是自己那份。留一份的症狀是換過資料庫之後每一輪搜尋
/// 都以 <see cref="ObjectDisposedException"/> 收場，而那不是 <see cref="System.Data.Common.DbException"/>，
/// 索引那一層的降級接不住。這裡只留<b>目錄</b>，每一輪重新向它要 <c>ConnectionSource</c>。
/// </remarks>
internal sealed class SqlSearchProviders
{
    private readonly SqlCatalogSearchIndexCache _indexCache = new();
    private SqlMetadataCatalog? _catalog;

    public SqlSearchProviders()
    {
        Aggregator = new SearchAggregator(new ISearchProvider[] { new CatalogSource(this) });
    }

    /// <summary>視窗握著的那一個；分類 pill 也由它的 <see cref="SearchAggregator.Categories"/> 產生。</summary>
    public SearchAggregator Aggregator { get; }

    public bool HasConnection => Volatile.Read(ref _catalog) is not null;

    /// <summary>
    /// 換上目前查詢視窗那條連線的目錄。
    /// </summary>
    /// <remarks>
    /// 由 UI 執行緒在每一輪搜尋之前呼叫；背景的 provider 只讀。目錄本身是註冊表共用的，
    /// 留一份參考沒有所有權問題——不能留的是它底下那個連線來源。
    /// </remarks>
    public void UseCatalog(SqlMetadataCatalog? catalog) => Volatile.Write(ref _catalog, catalog);

    /// <summary>
    /// 這一輪的目標資料庫已經有索引了嗎；false 表示可能要先掃一次全表（含定義本文）。
    /// </summary>
    /// <remarks>
    /// 只用來決定載入表面要不要出現，答錯的代價是多轉一圈或少轉一圈的載入圖示，不影響結果。
    /// 指名多個資料庫時只要有一個沒索引就算沒有：使用者要等的是最慢那一個。
    /// </remarks>
    public bool IsIndexed(SearchScope scope)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));

        var catalog = Volatile.Read(ref _catalog);
        if (catalog is null) return true;

        var source = catalog.ConnectionSource;

        if (scope.Databases.Count == 0) return _indexCache.TryGet(source.CacheKey, out _);

        foreach (var database in scope.Databases)
        {
            var key = string.Equals(database, source.DatabaseName, StringComparison.OrdinalIgnoreCase)
                ? source.CacheKey
                : SqlConnectionCacheKey.Compose(source.ServerCacheKey, database);

            if (!_indexCache.TryGet(key, out _)) return false;
        }

        return true;
    }

    /// <summary>整批丟掉索引；使用者按重新整理時就是在說「我知道它舊了」。</summary>
    public void Invalidate() => _indexCache.Clear();

    /// <summary>
    /// 目錄物件來源：每一輪現組一個 <see cref="SqlCatalogSearchProvider"/>，共用同一份索引快取。
    /// </summary>
    /// <remarks>
    /// 現組是為了不保存連線來源（見型別註解）。代價只有一次建構——它做的事是接兩個參考
    /// 與建一份分類清單，而索引留在共用的快取裡，不會因此重掃。
    /// </remarks>
    private sealed class CatalogSource : ISearchProvider
    {
        private readonly SqlSearchProviders _owner;

        internal CatalogSource(SqlSearchProviders owner)
        {
            _owner = owner;
            Categories = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId);
        }

        public string Id => SqlCatalogSearchProvider.ProviderId;

        public string DisplayName => "資料庫物件";

        public IReadOnlyList<SearchCategory> Categories { get; }

        public Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
        {
            var catalog = Volatile.Read(ref _owner._catalog);

            // 沒有連線不是失敗：畫面上已經有「尚未連線」那一句，再記一筆例外只會蓋掉真正的錯誤。
            if (catalog is null) return Task.CompletedTask;

            return new SqlCatalogSearchProvider(catalog.ConnectionSource, _owner._indexCache)
                .SearchAsync(query, sink, cancellationToken);
        }
    }
}
