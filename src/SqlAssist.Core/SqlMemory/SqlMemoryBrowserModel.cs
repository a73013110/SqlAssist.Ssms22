using System;
using System.Collections.Generic;
using System.Globalization;

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
    internal SqlMemoryPageLoad(long generation, long hostGeneration, SqlHistoryRequest? history,
        SqlFavoriteRequest? favorites, string? blockedMessage)
    {
        Generation = generation;
        HostGeneration = hostGeneration;
        History = history;
        Favorites = favorites;
        BlockedMessage = blockedMessage;
    }

    public long Generation { get; }
    public long HostGeneration { get; }
    public SqlHistoryRequest? History { get; }
    public SqlFavoriteRequest? Favorites { get; }

    /// <summary>篩選條件還不足以查詢（例如收藏範圍缺伺服器）；這時沒有請求，也不算載入中。</summary>
    public string? BlockedMessage { get; }
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

    public static IReadOnlyList<SqlMemoryOption<SqlFavoriteScope>> ScopeOptions { get; } = Array.AsReadOnly(new[]
    {
        new SqlMemoryOption<SqlFavoriteScope>(SqlFavoriteScope.Global, "全域"),
        new SqlMemoryOption<SqlFavoriteScope>(SqlFavoriteScope.Server, "指定伺服器"),
        new SqlMemoryOption<SqlFavoriteScope>(SqlFavoriteScope.Database, "指定資料庫"),
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
    public string? Server { get; set; }
    public string? Database { get; set; }
    public SqlHistoryFilter Kind { get; set; } = SqlHistoryFilter.All;

    /// <summary>History 預設七天：足夠找回這週的工作，又不讓第一頁掃過整個資料庫。</summary>
    public SqlHistoryPeriod Period { get; set; } = SqlHistoryPeriod.SevenDays;

    public SqlFavoriteScope Scope { get; set; } = SqlFavoriteScope.Global;

    public bool IsFavorites => Tab == SqlMemoryBrowserTab.Favorites;

    /// <summary>無關的伺服器／資料庫區塊直接隱藏，不用停用的灰色控制項佔空間。</summary>
    public bool ShowsServerFilter => !IsFavorites || Scope != SqlFavoriteScope.Global;

    public bool ShowsDatabaseFilter => !IsFavorites || Scope == SqlFavoriteScope.Database;

    /// <summary>收藏的 scope 必須精確指定，沒有「全部」可選。</summary>
    public string EmptyConnectionLabel => IsFavorites ? "請選擇" : "全部";

    /// <summary>最近一次 <see cref="Invalidate"/> 算出的期間起點；同一次分頁的每一頁都用它，游標指紋才對得上。</summary>
    public DateTimeOffset? Since { get; private set; }

    public long Generation => _page.Generation;
    public bool IsLoading => _page.Loading;

    /// <summary>上一頁因搜尋預算提早結束時的進度說明；null 表示游標是一般的「載入更多」。</summary>
    public string? SearchProgress { get; private set; }

    public bool IsAvailable { get; private set; }
    public long HostGeneration { get; private set; }

    public bool CanLoadMore => IsAvailable && !_page.Loading && _page.Cursor != null;

    public string LoadMoreLabel => SearchProgress == null ? "載入更多" : "繼續搜尋";

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

    /// <returns>null 表示不需要載入（不可用或已在載入）；有 <see cref="SqlMemoryPageLoad.BlockedMessage"/> 時只顯示訊息。</returns>
    public SqlMemoryPageLoad? BeginLoad()
    {
        if (!IsAvailable || _page.Loading) return null;
        var generation = _page.Generation;
        if (IsFavorites)
        {
            if (Scope != SqlFavoriteScope.Global && (Server == null || (Scope == SqlFavoriteScope.Database && Database == null)))
            {
                return new SqlMemoryPageLoad(generation, HostGeneration, null, null, Scope == SqlFavoriteScope.Server
                    ? "請選擇收藏的伺服器。"
                    : "請選擇收藏的伺服器與資料庫，或使用目前連線。");
            }

            if (!_page.Begin(generation)) return null;
            return new SqlMemoryPageLoad(generation, HostGeneration, null, new SqlFavoriteRequest(PageSize, Scope,
                Scope == SqlFavoriteScope.Global ? null : Server, Scope == SqlFavoriteScope.Database ? Database : null,
                Search, _page.Cursor), null);
        }

        if (!_page.Begin(generation)) return null;
        return new SqlMemoryPageLoad(generation, HostGeneration,
            new SqlHistoryRequest(PageSize, Kind, Search, Server, Database, Since, cursor: _page.Cursor), null, null);
    }

    /// <summary>回應是否仍屬於目前的篩選與宿主世代；是的話推進游標與搜尋進度。</summary>
    public bool Accept<T>(SqlMemoryPageLoad load, SqlMemoryPage<T> page)
    {
        if (load == null) throw new ArgumentNullException(nameof(load));
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (!IsCurrent(load) || !_page.Accept(load.Generation, page.NextCursor)) return false;
        SearchProgress = !page.IsSearchPartial ? null
            : load.Favorites != null ? "已搜尋部分收藏"
            : page.SearchedThrough is { } through
                ? "已搜尋至 " + through.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
                : "已搜尋部分紀錄";
        return true;
    }

    /// <summary>載入結束（成功、失敗或放棄）；只放開同一世代的載入旗標。</summary>
    public void End(SqlMemoryPageLoad load) => _page.Fail(load.Generation);

    /// <summary>失敗是否屬於目前畫面；舊查詢不得蓋掉新的狀態訊息。</summary>
    public bool IsCurrent(SqlMemoryPageLoad load) =>
        load.Generation == _page.Generation && load.HostGeneration == HostGeneration && IsAvailable;

    /// <summary>採用一頁之後的狀態訊息。搜尋提早結束不是「沒有結果」，交給使用者決定是否繼續往前找。</summary>
    public string PageMessage(int loadedCount) =>
        SearchProgress != null ? SearchProgress + "，繼續搜尋可再往前找。"
        : loadedCount == 0 ? "沒有符合條件的項目。可清除搜尋或放寬期間與範圍。"
        : "";

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

    /// <summary>套用目前查詢視窗的連線：一次更新兩個條件，收藏同時切到資料庫範圍。</summary>
    /// <returns>無法套用時的訊息；原篩選不變。</returns>
    public string? UseConnection(SqlConnectionLabel? connection)
    {
        if (connection is null || string.IsNullOrEmpty(connection.Server) || string.IsNullOrEmpty(connection.Database))
            return "目前沒有已連線的 SQL 查詢視窗；請先選取查詢視窗。原篩選未變更。";
        if (IsFavorites) Scope = SqlFavoriteScope.Database;
        Server = connection.Server;
        Database = connection.Database;
        return null;
    }

    /// <summary>開始一次連線名稱載入；同一種名稱只有最後一次請求的回應會被採用。</summary>
    public long BeginFacet(bool databases) => databases ? ++_databaseFacetRequest : ++_serverFacetRequest;

    public SqlConnectionFacetRequest FacetRequest(bool databases, SqlConnectionFacetSort sort, int offset) =>
        new(IsFavorites, Scope, databases, databases ? Server : null, sort, offset);

    public bool IsCurrentFacet(bool databases, long request, long hostGeneration) =>
        IsAvailable && hostGeneration == HostGeneration &&
        request == (databases ? _databaseFacetRequest : _serverFacetRequest);
}
