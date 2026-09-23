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
    private readonly SqlAgentJobSearchSnapshotCache _jobCache = new();
    private SqlSearchConnection? _connection;

    /// <remarks>
    /// 順序就是分類 pill 的順序（聚合器照 provider 串接）：先資料庫物件，再伺服器層級的
    /// 作業。反過來的話，最常用的資料表與程序會被擠到過濾清單的第二段。
    /// </remarks>
    public SqlSearchProviders()
    {
        Aggregator = new SearchAggregator(
            new ISearchProvider[] { new CatalogSource(this), new AgentJobSource(this) });
    }

    /// <summary>視窗握著的那一個；分類 pill 也由它的 <see cref="SearchAggregator.Categories"/> 產生。</summary>
    public SearchAggregator Aggregator { get; }

    public bool HasConnection => Volatile.Read(ref _connection) is not null;

    /// <summary>
    /// 換上這一輪範圍的目錄，以及它連著哪一台。
    /// </summary>
    /// <remarks>
    /// 由 UI 執行緒在每一輪搜尋之前呼叫；背景的 provider 只讀。目錄本身是註冊表共用的，
    /// 留一份參考沒有所有權問題——不能留的是它底下那個連線來源。
    ///
    /// 兩者裝在<b>同一個</b>欄位裡一次換掉：分兩個欄位的話，背景那一輪可能讀到新的目錄配上
    /// 舊的伺服器，而那一輪每一筆命中都帶著錯的那一台。null 就是沒有連線。
    ///
    /// 「換了沒有」也由這裡回答，因為上一份只有這裡握著：呼叫端自己再記一份的話，
    /// 每輪搜尋前那一次重讀先把新值記下，連線事件那一次就看不出換過，舊結果留著。
    /// </remarks>
    /// <returns>換到另一個目錄或另一台時為 true；同一份重設一次為 false。</returns>
    public bool UseConnection(SqlSearchConnection? connection) =>
        !SqlSearchConnection.SameScope(Interlocked.Exchange(ref _connection, connection), connection);

    /// <summary>
    /// 這一輪的目標資料庫已經有索引了嗎；false 表示可能要先掃一次全表（含定義本文）。
    /// </summary>
    /// <remarks>
    /// 只用來決定載入表面要不要出現，答錯的代價是多轉一圈或少轉一圈的載入圖示，不影響結果。
    /// 指名多個資料庫時只要有一個沒索引就算沒有：使用者要等的是最慢那一個。
    /// 範圍是「全部」時照手上那份資料庫清單算；還不知道有哪幾個就當成沒有，第一輪多半要掃。
    /// </remarks>
    /// <param name="knownDatabases">這台伺服器上已知的資料庫；範圍是「全部」時才用到。</param>
    public bool IsIndexed(SearchScope scope, IReadOnlyList<string> knownDatabases)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        if (knownDatabases is null) throw new ArgumentNullException(nameof(knownDatabases));

        if (Volatile.Read(ref _connection) is not { } connection) return true;

        var source = connection.Catalog.ConnectionSource;
        var databases = scope.Databases.Count != 0 ? scope.Databases : knownDatabases;

        if (databases.Count == 0) return false;

        foreach (var database in databases)
        {
            var key = string.Equals(database, source.DatabaseName, StringComparison.OrdinalIgnoreCase)
                ? source.CacheKey
                : SqlConnectionCacheKey.Compose(source.ServerCacheKey, database);

            if (!_indexCache.IsFresh(key)) return false;
        }

        return true;
    }

    /// <summary>
    /// 標記索引舊了；使用者按重新整理時就是在說這句話。
    /// </summary>
    /// <remarks>
    /// 不整批丟掉：丟掉之後下一輪要重掃整個資料庫的定義本文，而使用者通常只是改了一個
    /// 預存程序。標記過期之後，下一輪沿著 <c>MAX(modify_date)</c> 只重撈變更過的那幾個。
    /// </remarks>
    public void Invalidate()
    {
        _indexCache.Invalidate();

        // 作業那一份整批丟掉而不是標記過期：它本來就整份重撈（兩條查詢），
        // 多一種狀態只是多一個要解釋的東西。理由見 SqlAgentJobSearchSnapshotCache。
        _jobCache.Clear();
    }

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
            // 沒有連線不是失敗：畫面上已經有「尚未連線」那一句，再記一筆例外只會蓋掉真正的錯誤。
            if (Volatile.Read(ref _owner._connection) is not { } connection) return Task.CompletedTask;

            return new SqlCatalogSearchProvider(
                    connection.Catalog.ConnectionSource, connection.Origin, _owner._indexCache)
                .SearchAsync(query, sink, cancellationToken);
        }
    }

    /// <summary>
    /// SQL Agent 作業來源：每一輪現組一個 <see cref="SqlAgentJobSearchProvider"/>，
    /// 共用同一份快照快取。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="CatalogSource"/> 同一個形狀，而那正是重點——加一個來源在這一支是
    /// 多一個十幾行的巢狀類別，<see cref="SearchAggregator"/>、分類 pill、清單樣板與
    /// 預覽一個字都不必改。
    ///
    /// 快取鍵的差別藏在 <see cref="SqlAgentJobSearchSnapshotCache"/> 裡：那一份以
    /// <c>ServerCacheKey</c> 分，不是 <c>CacheKey</c>——作業與目前連在哪一個資料庫無關。
    /// </remarks>
    private sealed class AgentJobSource : ISearchProvider
    {
        private readonly SqlSearchProviders _owner;

        internal AgentJobSource(SqlSearchProviders owner)
        {
            _owner = owner;
            Categories = SqlAgentJobSearchCategories.Create(SqlAgentJobSearchProvider.ProviderId);
        }

        public string Id => SqlAgentJobSearchProvider.ProviderId;

        public string DisplayName => "SQL Agent 作業";

        public IReadOnlyList<SearchCategory> Categories { get; }

        public Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
        {
            // 沒有連線不是失敗：畫面上已經有「尚未連線」那一句。
            if (Volatile.Read(ref _owner._connection) is not { } connection) return Task.CompletedTask;

            return new SqlAgentJobSearchProvider(
                    connection.Catalog.ConnectionSource, connection.Origin, _owner._jobCache)
                .SearchAsync(query, sink, cancellationToken);
        }
    }
}
