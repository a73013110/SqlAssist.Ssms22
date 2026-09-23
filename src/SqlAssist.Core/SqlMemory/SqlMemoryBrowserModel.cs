using System;
using System.Collections.Generic;
using System.Globalization;
using SqlAssist.Core.Connections;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.SqlMemory;

public enum SqlMemoryBrowserTab { History, Favorites }

public enum SqlHistoryPeriod { Today, SevenDays, ThirtyDays, Any }

/// <summary>篩選選項的顯示文字與值；UI 依清單順序建立控制項，選取位置換回值時查表，不把索引轉型成列舉。</summary>
public sealed class SqlMemoryOption<T>
{
    public SqlMemoryOption(T value, string label, string? shortLabel = null)
    {
        Value = value;
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ShortLabel = shortLabel ?? label;
    }

    public T Value { get; }
    public string Label { get; }
    public string ShortLabel { get; }
}

/// <summary>一次清單載入：屬於哪個篩選世代與宿主世代，以及要送出的請求。</summary>
public sealed class SqlMemoryPageLoad
{
    internal SqlMemoryPageLoad(long generation, long hostGeneration, SqlHistoryRequest? history, SqlFavoriteRequest? favorites)
    {
        Generation = generation;
        HostGeneration = hostGeneration;
        History = history;
        Favorites = favorites;
    }

    public long Generation { get; }
    public long HostGeneration { get; }
    public SqlHistoryRequest? History { get; }
    public SqlFavoriteRequest? Favorites { get; }
}

/// <summary>
/// 某一輪清單的條件快照：可以換頁大小與游標，條件本身固定。
/// </summary>
/// <remarks>
/// 「全部符合」的複製在背景逐頁讀，期間使用者可能已經改了篩選；請求若每頁都從模型現讀，
/// 後面幾頁的條件就與游標的指紋對不上。快照也帶著世代，讀完之後拿它問模型還算不算數。
/// </remarks>
public sealed class SqlMemoryQuery
{
    private readonly SqlHistoryFilter _kind;
    private readonly string _search;
    private readonly TextMatchOptions _matchOptions;
    private readonly string[] _servers;
    private readonly string[] _databases;
    private readonly DateTimeOffset? _since;

    internal SqlMemoryQuery(long generation, long hostGeneration, bool favorites, SqlHistoryFilter kind, string search,
        TextMatchOptions matchOptions, IEnumerable<string> servers, IEnumerable<string> databases, DateTimeOffset? since)
    {
        Generation = generation;
        HostGeneration = hostGeneration;
        IsFavorites = favorites;
        _kind = kind;
        _search = search;
        _matchOptions = matchOptions;
        _servers = new List<string>(servers).ToArray();
        _databases = new List<string>(databases).ToArray();
        _since = since;
        // 與儲存層同一條規則：空字串停用搜尋，空白仍是內容（見 SqliteSearchScan.Create）。
        Matcher = search.Length == 0 ? null : new TextMatcher(search, matchOptions);
    }

    public long Generation { get; }
    public long HostGeneration { get; }
    public bool IsFavorites { get; }

    /// <summary>
    /// 這一輪清單的比對器；沒有搜尋字時 null。
    /// </summary>
    /// <remarks>
    /// 預覽拿它標命中位置：清單與預覽從同一份快照取搜尋字與選項，換了其中一個就是新的一輪，
    /// 不會出現清單照舊條件、預覽照框裡剛打的字標的那種錯位。
    /// </remarks>
    public TextMatcher? Matcher { get; }

    public SqlHistoryRequest HistoryRequest(int pageSize, string? cursor) =>
        new(pageSize, _kind, _search, _servers, _databases, _since, cursor: cursor, matchOptions: _matchOptions);

    public SqlFavoriteRequest FavoriteRequest(int pageSize, string? cursor) =>
        new(pageSize, _servers, _databases, _search, cursor, _matchOptions);
}

public enum SqlMemoryFooterKind
{
    /// <summary>不可用或第一頁載入中；第一頁由表面載入圖示表達，頁尾不佔位置。</summary>
    Hidden,

    /// <summary>已載入完畢且沒有任何項目。</summary>
    Empty,

    /// <summary>還有下一頁；捲到底或按下都會續頁。</summary>
    More,

    /// <summary>搜尋用盡單頁預算；必須由使用者明確續搜。</summary>
    ContinueSearch,

    /// <summary>已有項目、正在載入下一頁；進度留在原地，不遮住已載入的清單。</summary>
    Loading,

    /// <summary>全部載入完畢。</summary>
    End,
}

/// <summary>清單頁尾的呈現：UI 只照著畫，文案與狀態轉換都在這裡決定。</summary>
public sealed class SqlMemoryFooter
{
    internal SqlMemoryFooter(SqlMemoryFooterKind kind, string summary, string? hint = null, string? actionLabel = null)
    {
        Kind = kind;
        Summary = summary;
        Hint = hint;
        ActionLabel = actionLabel;
    }

    /// <summary>不佔位置的那一份；呼叫端把某一種狀態交給別的表面時用它蓋掉頁尾。</summary>
    public static SqlMemoryFooter Hidden { get; } = new(SqlMemoryFooterKind.Hidden, "");

    public SqlMemoryFooterKind Kind { get; }

    /// <summary>頁尾中央的單行摘要，例如筆數。</summary>
    public string Summary { get; }

    /// <summary>摘要下方的淡色說明；沒有時為 null。</summary>
    public string? Hint { get; }

    /// <summary>頁尾按鈕文字；null 表示沒有按鈕。</summary>
    public string? ActionLabel { get; }

    /// <summary>按鈕是否可按；載入中的按鈕保留位置但停用。</summary>
    public bool CanAct => Kind is SqlMemoryFooterKind.More or SqlMemoryFooterKind.ContinueSearch;
}

/// <summary>
/// SQL Memory 瀏覽器的純邏輯：篩選狀態轉請求、分頁與宿主世代、搜尋進度，以及重新整理後的選取還原。
/// </summary>
/// <remarks>
/// 只在 UI 執行緒使用，不做 I/O。UI 把控制項的值寫進來、把回應交回來，由這裡決定要不要採用；
/// 取消無法撤回已派送的隔離呼叫，所以晚到的回應一律以篩選世代與宿主世代過濾。
/// </remarks>
public sealed class SqlMemoryBrowserModel
{
    public const int PageSize = 50;

    private readonly PagedLoadState _page = new();
    private long _serverFacetRequest;
    private long _databaseFacetRequest;
    private Guid? _restoreSelection;
    private bool _hasPage;

    public static IReadOnlyList<SqlMemoryOption<SqlHistoryFilter>> KindOptions { get; } = Array.AsReadOnly(new[]
    {
        new SqlMemoryOption<SqlHistoryFilter>(SqlHistoryFilter.All, "全部"),
        new SqlMemoryOption<SqlHistoryFilter>(SqlHistoryFilter.Executions, "執行"),
        new SqlMemoryOption<SqlHistoryFilter>(SqlHistoryFilter.Drafts, "草稿"),
    });

    public static IReadOnlyList<SqlMemoryOption<SqlHistoryPeriod>> PeriodOptions { get; } = Array.AsReadOnly(new[]
    {
        new SqlMemoryOption<SqlHistoryPeriod>(SqlHistoryPeriod.Today, "今天"),
        new SqlMemoryOption<SqlHistoryPeriod>(SqlHistoryPeriod.SevenDays, "7 天"),
        new SqlMemoryOption<SqlHistoryPeriod>(SqlHistoryPeriod.ThirtyDays, "30 天"),
        new SqlMemoryOption<SqlHistoryPeriod>(SqlHistoryPeriod.Any, "不限"),
    });

    public static IReadOnlyList<SqlMemoryOption<SqlConnectionFacetSort>> SortOptions { get; } = Array.AsReadOnly(new[]
    {
        new SqlMemoryOption<SqlConnectionFacetSort>(SqlConnectionFacetSort.Recent, "最近使用優先", "最近"),
        new SqlMemoryOption<SqlConnectionFacetSort>(SqlConnectionFacetSort.Oldest, "最早使用優先", "最早"),
        new SqlMemoryOption<SqlConnectionFacetSort>(SqlConnectionFacetSort.Alphabetical, "名稱 A–Z", "A–Z"),
        new SqlMemoryOption<SqlConnectionFacetSort>(SqlConnectionFacetSort.ReverseAlphabetical, "名稱 Z–A", "Z–A"),
    });

    public SqlMemoryBrowserTab Tab { get; set; }
    public string Search { get; set; } = "";

    /// <summary>搜尋框裡開著的比對修飾；與 SQL Search 同一份選項與規則。</summary>
    public TextMatchOptions MatchOptions { get; set; }

    public SqlHistoryFilter Kind { get; set; } = SqlHistoryFilter.All;

    /// <summary>
    /// 伺服器與資料庫兩個篩選；空名單表示全部，資料庫不必先指定伺服器。
    /// </summary>
    /// <remarks>
    /// 伺服器多選而不是單選：History 與 Favorites 的列早就存在，篩選只是縮小已存的那一份，
    /// 而使用者要比的往往就是「這兩台上同一段 SQL」。單選的那一版逼他看完一台再換一台，
    /// 而中間那一次換掉會把清單、捲動位置與預覽一起重來。
    ///
    /// 名稱一律 ordinal 比對：名單本來就是儲存層自己回的那幾個字，
    /// 放寬成不分大小寫會讓勾起來的條件配不到任何一列，而畫面上只看得到「沒有符合條件」。
    /// </remarks>
    public SqlConnectionScope Scope { get; } = new(multipleServers: true, StringComparer.Ordinal);

    /// <summary>History 預設七天：足夠找回這週的工作，又不讓第一頁掃過整個資料庫。</summary>
    public SqlHistoryPeriod Period { get; set; } = SqlHistoryPeriod.SevenDays;

    public bool IsFavorites => Tab == SqlMemoryBrowserTab.Favorites;

    /// <summary>最近一次 <see cref="Invalidate"/> 算出的期間起點；同一次分頁的每一頁都用它，游標指紋才對得上。</summary>
    public DateTimeOffset? Since { get; private set; }

    public long Generation => _page.Generation;
    public bool IsLoading => _page.Loading;

    /// <summary>上一頁因搜尋預算提早結束時的進度說明；null 表示游標是一般的「載入更多」。</summary>
    public string? SearchProgress { get; private set; }

    public bool IsAvailable { get; private set; }
    public long HostGeneration { get; private set; }

    public bool CanLoadMore => IsAvailable && !_page.Loading && _page.Cursor != null;

    /// <summary>捲到底可以自動續頁；達到搜尋預算後必須由使用者明確續搜，不能讓捲動耗盡整份儲存。</summary>
    public bool CanAutoLoadMore => CanLoadMore && SearchProgress == null;

    /// <summary>記下宿主狀態。</summary>
    /// <returns>可用性或宿主世代改變：清單、facets 與預覽都屬於舊儲存，呼叫端必須作廢並重新載入。</returns>
    public bool ObserveHost(bool available, long hostGeneration)
    {
        if (IsAvailable == available && HostGeneration == hostGeneration) return false;
        IsAvailable = available;
        HostGeneration = hostGeneration;
        return true;
    }

    /// <summary>篩選或宿主改變：作廢進行中的載入與游標，並固定這一輪分頁的期間起點。</summary>
    public void Invalidate(DateTimeOffset now)
    {
        _page.Reset();
        _hasPage = false;
        SearchProgress = null;
        var local = now.ToLocalTime();
        Since = Period switch
        {
            SqlHistoryPeriod.Today => new DateTimeOffset(local.Date, local.Offset),
            SqlHistoryPeriod.SevenDays => now.AddDays(-7),
            SqlHistoryPeriod.ThirtyDays => now.AddDays(-30),
            _ => null,
        };
    }

    /// <summary>重新整理前記下目前選取；新的第一頁載入後若還在，就選回它。</summary>
    public void RememberSelection(Guid? id) => _restoreSelection = id;

    /// <returns>null 表示不需要載入（不可用或已在載入）。</returns>
    /// <remarks>History 與 Favorites 的伺服器／資料庫篩選同一種語意：未指定表示不限，指定就精確比對。</remarks>
    public SqlMemoryPageLoad? BeginLoad()
    {
        if (!IsAvailable || _page.Loading) return null;
        var generation = _page.Generation;
        if (!_page.Begin(generation)) return null;
        var query = Query();
        return IsFavorites
            ? new SqlMemoryPageLoad(generation, HostGeneration, null, query.FavoriteRequest(PageSize, _page.Cursor))
            : new SqlMemoryPageLoad(generation, HostGeneration, query.HistoryRequest(PageSize, _page.Cursor), null);
    }

    /// <summary>目前這一輪清單的條件；清單的每一頁與「全部符合」的複製都從它組請求，兩邊不會各組一份。</summary>
    public SqlMemoryQuery Query() =>
        new(_page.Generation, HostGeneration, IsFavorites, Kind, Search, MatchOptions, Scope.Servers, Scope.Databases, Since);

    /// <summary>快照是否仍屬於目前的篩選與宿主世代；背景讀完之後才寫剪貼簿，換過條件就不算數。</summary>
    public bool IsCurrent(SqlMemoryQuery query)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));
        return query.Generation == _page.Generation && query.HostGeneration == HostGeneration && IsAvailable;
    }

    /// <summary>還有沒載入的下一頁（含搜尋預算用盡、等使用者續搜的那一種）。</summary>
    public bool HasMore => IsAvailable && _page.Cursor != null;


    /// <summary>回應是否仍屬於目前的篩選與宿主世代；是的話推進游標與搜尋進度。</summary>
    public bool Accept<T>(SqlMemoryPageLoad load, SqlMemoryPage<T> page)
    {
        if (load == null) throw new ArgumentNullException(nameof(load));
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (!IsCurrent(load) || !_page.Accept(load.Generation, page.NextCursor)) return false;
        _hasPage = true;
        // History 以建立時間、Favorites 以最後儲存時間排序；兩者都說得出搜尋到哪一天。
        SearchProgress = page.SearchedThrough is { } through
            ? "已搜尋至 " + through.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            : null;
        return true;
    }

    /// <summary>載入結束（成功、失敗或放棄）；只放開同一世代的載入旗標。</summary>
    public void End(SqlMemoryPageLoad load) => _page.Fail(load.Generation);

    /// <summary>失敗是否屬於目前畫面；舊查詢不得蓋掉新的狀態訊息。</summary>
    public bool IsCurrent(SqlMemoryPageLoad load) =>
        load.Generation == _page.Generation && load.HostGeneration == HostGeneration && IsAvailable;

    /// <summary>
    /// 清單頁尾目前該呈現什麼。搜尋提早結束不是「沒有結果」，交給使用者決定是否繼續往前找；
    /// 續頁中的進度留在頁尾原地，第一頁才交給表面載入圖示。
    /// </summary>
    /// <param name="loadedCount">目前清單的列數；刪除列之後也用它重算。</param>
    public SqlMemoryFooter Footer(int loadedCount)
    {
        if (loadedCount < 0) throw new ArgumentOutOfRangeException(nameof(loadedCount));
        // 這一輪還沒採用過任何一頁（剛換篩選、被擋下或第一頁載入中）：不能先說「沒有符合條件」。
        if (!IsAvailable || !_hasPage) return new SqlMemoryFooter(SqlMemoryFooterKind.Hidden, "");
        var loaded = Count(loadedCount);
        if (_page.Loading)
        {
            return loadedCount == 0
                ? new SqlMemoryFooter(SqlMemoryFooterKind.Hidden, "")
                : new SqlMemoryFooter(SqlMemoryFooterKind.Loading, loaded, SearchProgress,
                    SearchProgress == null ? "載入中…" : "搜尋中…");
        }
        if (_page.Cursor != null)
        {
            return SearchProgress == null
                ? new SqlMemoryFooter(SqlMemoryFooterKind.More, loaded, null, "載入更多")
                : new SqlMemoryFooter(SqlMemoryFooterKind.ContinueSearch, "符合 " + loadedCount.ToString(CultureInfo.InvariantCulture) + " 筆",
                    SearchProgress + "，繼續搜尋可再往前找", "繼續搜尋");
        }
        return loadedCount == 0
            ? new SqlMemoryFooter(SqlMemoryFooterKind.Empty, "沒有符合條件的項目", "可清除搜尋或放寬期間與範圍")
            : new SqlMemoryFooter(SqlMemoryFooterKind.End, "已顯示全部 " + loadedCount.ToString(CultureInfo.InvariantCulture) + " 筆");
    }

    private static string Count(int loadedCount) => "已載入 " + loadedCount.ToString(CultureInfo.InvariantCulture) + " 筆";

    /// <summary>移除一列後要選哪一列：留在原位置（即原本的下一列），刪掉最後一列就退到新的最後一列。</summary>
    /// <returns>null 表示清單已空。</returns>
    public static int? SelectionAfterRemoval(int removedIndex, int remainingCount)
    {
        if (removedIndex < 0) throw new ArgumentOutOfRangeException(nameof(removedIndex));
        if (remainingCount < 0) throw new ArgumentOutOfRangeException(nameof(remainingCount));
        return remainingCount == 0 ? null : Math.Min(removedIndex, remainingCount - 1);
    }

    /// <summary>更新後的收藏是否仍符合目前的標註篩選；改了標註就該離開這份清單，而不是留著過期的列。</summary>
    public bool MatchesFavoriteFilter(SqlFavorite favorite)
    {
        if (favorite == null) throw new ArgumentNullException(nameof(favorite));
        return IsFavorites &&
            (Scope.Servers.Count == 0 || (favorite.Server is { } server && Scope.IsServerSelected(server))) &&
            (Scope.Databases.Count == 0 || (favorite.Database is { } database && Scope.IsDatabaseSelected(database)));
    }

    /// <summary>採用一頁之後要選取哪一列。</summary>
    /// <param name="loadedIds">目前清單全部列的識別碼，依顯示順序。</param>
    /// <param name="hasSelection">清單目前是否已有選取。</param>
    /// <returns>要選取的索引；null 表示維持現狀。沒有要還原的列時預覽第一筆，但不搶已有的選取。</returns>
    public int? ResolveSelection(IReadOnlyList<Guid> loadedIds, bool hasSelection)
    {
        if (loadedIds == null) throw new ArgumentNullException(nameof(loadedIds));
        var restore = _restoreSelection;
        _restoreSelection = null;
        if (restore is { } id)
        {
            for (var i = 0; i < loadedIds.Count; i++)
                if (loadedIds[i] == id) return i;
        }
        return !hasSelection && loadedIds.Count > 0 ? 0 : null;
    }

    /// <summary>開始一次連線名稱載入；同一種名稱只有最後一次請求的回應會被採用。</summary>
    public long BeginFacet(bool databases) => databases ? ++_databaseFacetRequest : ++_serverFacetRequest;

    public SqlConnectionFacetRequest FacetRequest(bool databases, SqlConnectionFacetSort sort, int offset) =>
        new(IsFavorites, databases, databases ? Scope.Servers : null, sort, offset);

    public bool IsCurrentFacet(bool databases, long request, long hostGeneration) =>
        IsAvailable && hostGeneration == HostGeneration &&
        request == (databases ? _databaseFacetRequest : _serverFacetRequest);
}
