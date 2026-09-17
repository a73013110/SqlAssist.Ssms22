using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlAssist.Core.SqlMemory;

/// <summary>選取的版本要和哪一份內容比：<see cref="BaseContentId"/> 是舊的一側。</summary>
public sealed record SqlFavoriteRevisionComparison(string BaseContentId, string TargetContentId, string Description);

/// <summary>
/// 收藏版本時間軸的純邏輯：分頁世代、去重、比較對象、能否回溯，以及誠實的保留說明。
/// </summary>
/// <remarks>
/// 只在 UI 執行緒使用，不做 I/O。取消無法撤回已派送的隔離呼叫，晚到的頁一律以世代擋下。
/// 回溯之後以新世代重讀第一頁，<see cref="Accept"/> 回傳這一輪才出現的版本，介面只替它們播進場。
/// </remarks>
public sealed class SqlFavoriteRevisionTimeline
{
    public const int DefaultPageSize = 50;

    private readonly PagedLoadState _page = new();
    private readonly List<SqlFavoriteRevisionItem> _items = new();
    private readonly int _pageSize;
    private bool _hasPage;
    private long _pending;
    private bool _pendingFirst;

    /// <param name="currentContentId">收藏目前版本的內容；時間軸還沒讀到目前版本也能比較與判斷回溯。</param>
    /// <param name="retainedLimit">每個收藏保留的版本數設定；只用來說明，不裁切清單。</param>
    public SqlFavoriteRevisionTimeline(Guid favoriteId, string currentContentId, int retainedLimit, int pageSize = DefaultPageSize)
    {
        if (favoriteId == Guid.Empty) throw new ArgumentException("SQL Favorite 必須有識別碼。", nameof(favoriteId));
        if (retainedLimit < 1) throw new ArgumentOutOfRangeException(nameof(retainedLimit));
        if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        FavoriteId = favoriteId;
        CurrentContentId = currentContentId ?? throw new ArgumentNullException(nameof(currentContentId));
        RetainedLimit = retainedLimit;
        _pageSize = pageSize;
    }

    public Guid FavoriteId { get; }
    public string CurrentContentId { get; private set; }
    public int RetainedLimit { get; }
    public IReadOnlyList<SqlFavoriteRevisionItem> Items => _items;
    public bool IsLoading => _page.Loading;
    public bool HasMore => _hasPage && _page.Cursor != null;

    /// <summary>重讀第一頁（開窗、重新整理、回溯之後）；先前進行中的讀取一律作廢。</summary>
    public SqlFavoriteRevisionRequest BeginFirstPage(out long generation)
    {
        generation = _page.Reset();
        _page.Begin(generation);
        _pending = generation; _pendingFirst = true;
        return new SqlFavoriteRevisionRequest(FavoriteId, _pageSize);
    }

    /// <returns>null 表示沒有下一頁或已在載入。</returns>
    public SqlFavoriteRevisionRequest? BeginNextPage(out long generation)
    {
        generation = _page.Generation;
        if (!HasMore || !_page.Begin(generation)) return null;
        _pending = generation; _pendingFirst = false;
        return new SqlFavoriteRevisionRequest(FavoriteId, _pageSize, _page.Cursor);
    }

    /// <returns>過期回 null；否則回傳這一頁才新出現的版本（第一次開窗時為空，避免整份清單一起播進場）。</returns>
    public IReadOnlyList<SqlFavoriteRevisionItem>? Accept(long generation, SqlMemoryPage<SqlFavoriteRevisionItem> page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (generation != _pending || !_page.Accept(generation, page.NextCursor)) return null;
        var appeared = new List<SqlFavoriteRevisionItem>();
        if (_pendingFirst)
        {
            var known = new HashSet<Guid>();
            foreach (var item in _items) known.Add(item.RevisionId);
            var reload = _hasPage;
            _items.Clear();
            foreach (var item in page.Items)
            {
                if (Contains(item.RevisionId)) continue;
                _items.Add(item);
                if (reload && !known.Contains(item.RevisionId)) appeared.Add(item);
            }
        }
        else
        {
            // keyset 不會重複，但回溯與續頁交錯時仍以識別碼去重，不讓同一版本出現兩列。
            foreach (var item in page.Items)
            {
                if (Contains(item.RevisionId)) continue;
                _items.Add(item); appeared.Add(item);
            }
        }
        _hasPage = true;
        foreach (var item in _items) if (item.IsCurrent) CurrentContentId = item.ContentId;
        return appeared;
    }

    public void Fail(long generation) => _page.Fail(generation);

    /// <summary>回溯成功後、重讀之前先換上新的目前內容，比較與按鈕狀態不必等清單回來。</summary>
    public void UseCurrentContent(string contentId) => CurrentContentId = contentId ?? throw new ArgumentNullException(nameof(contentId));

    /// <summary>非目前版本與目前版本比；目前版本與較舊的前一版比；沒有前一版回 null。</summary>
    public SqlFavoriteRevisionComparison? ComparisonFor(SqlFavoriteRevisionItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (!item.IsCurrent) return new SqlFavoriteRevisionComparison(item.ContentId, CurrentContentId, "此版本 → 目前版本");
        var index = IndexOf(item.RevisionId);
        return index >= 0 && index + 1 < _items.Count
            ? new SqlFavoriteRevisionComparison(_items[index + 1].ContentId, item.ContentId, "前一版 → 目前版本")
            : null;
    }

    /// <summary>目前版本與內容相同的版本不能回溯：只會多一筆一模一樣的版本。</summary>
    public bool CanRevert(SqlFavoriteRevisionItem item) =>
        item != null && !item.IsCurrent && !string.Equals(item.ContentId, CurrentContentId, StringComparison.Ordinal);

    /// <summary>清單尾端的頁尾；結束時必須說清楚這不是完整編輯史。</summary>
    public SqlMemoryFooter Footer()
    {
        if (!_hasPage) return new SqlMemoryFooter(SqlMemoryFooterKind.Hidden, "");
        var loaded = "已載入 " + _items.Count.ToString(CultureInfo.InvariantCulture) + " 版";
        if (_page.Loading)
            return _pendingFirst ? new SqlMemoryFooter(SqlMemoryFooterKind.Hidden, "")
                : new SqlMemoryFooter(SqlMemoryFooterKind.Loading, loaded, null, "載入中…");
        if (_page.Cursor != null) return new SqlMemoryFooter(SqlMemoryFooterKind.More, loaded, null, "載入更多");
        return _items.Count == 0
            ? new SqlMemoryFooter(SqlMemoryFooterKind.Empty, "沒有保留的版本", "收藏可能已被移除；請關閉後重新整理清單。")
            : new SqlMemoryFooter(SqlMemoryFooterKind.End,
                "共保留 " + _items.Count.ToString(CultureInfo.InvariantCulture) + " 版", RetentionHint);
    }

    /// <summary>保留配額的說明；版本數到達配額時明講更舊的已經回收，不讓人把清單當成完整歷史。</summary>
    public string RetentionHint
    {
        get
        {
            var limit = RetainedLimit.ToString(CultureInfo.InvariantCulture);
            return _items.Count >= RetainedLimit
                ? "只保留最近 " + limit + " 版；更舊的版本已由維護回收（或將在下次維護回收），無法再檢視。"
                : "每個收藏最多保留最近 " + limit + " 版；超過時較舊的版本會被回收。";
        }
    }

    private bool Contains(Guid revisionId) => IndexOf(revisionId) >= 0;

    private int IndexOf(Guid revisionId)
    {
        for (var i = 0; i < _items.Count; i++) if (_items[i].RevisionId == revisionId) return i;
        return -1;
    }
}
