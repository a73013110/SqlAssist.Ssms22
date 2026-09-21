using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.SqlMemory;

/// <summary>游標由儲存層產生，必須包含穩定排序的時間與唯一鍵，並綁定原篩選條件。</summary>
[Serializable]
public sealed class SqlMemoryPage<T>
{
    public SqlMemoryPage(IEnumerable<T> items, string? nextCursor)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        Items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
    }

    /// <summary>搜尋用盡單頁掃描預算而提早結束的頁；游標接在最後檢查過的候選之後，不是最後一筆結果之後。</summary>
    public SqlMemoryPage(IEnumerable<T> items, string nextCursor, DateTimeOffset searchedThrough)
        : this(items, nextCursor ?? throw new ArgumentNullException(nameof(nextCursor)))
    {
        IsSearchPartial = true;
        SearchedThrough = searchedThrough;
    }

    public IReadOnlyList<T> Items { get; }
    public string? NextCursor { get; }

    /// <summary>
    /// true 表示還有候選沒檢查，Items 可能少於頁大小甚至為空；以 NextCursor 繼續搜尋，不代表沒有更多結果。
    /// </summary>
    public bool IsSearchPartial { get; }

    /// <summary>部分搜尋時最後檢查過的候選時間（含）；不是部分搜尋時為 null。</summary>
    public DateTimeOffset? SearchedThrough { get; }
}

[Serializable]
public sealed class SqlHistoryRequest
{
    /// <param name="servers">要列的伺服器；空名單表示不限。</param>
    /// <param name="databases">要列的資料庫；不需要先指定伺服器。</param>
    public SqlHistoryRequest(int pageSize, SqlHistoryFilter kind = SqlHistoryFilter.All,
        string? search = null, IEnumerable<string>? servers = null, IEnumerable<string>? databases = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null, string? cursor = null)
    {
        if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (!Enum.IsDefined(typeof(SqlHistoryFilter), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (since > until) throw new ArgumentException("起始時間不可晚於結束時間。", nameof(since));
        PageSize = pageSize;
        Kind = kind;
        Search = search;
        Servers = SqlConnectionNames.Normalize(servers);
        Databases = SqlConnectionNames.Normalize(databases);
        Since = since?.ToUniversalTime();
        Until = until?.ToUniversalTime();
        Cursor = cursor;
    }

    public int PageSize { get; }
    public SqlHistoryFilter Kind { get; }
    public string? Search { get; }

    /// <summary>要列的伺服器；空名單表示不限，不隱含「未標註」。</summary>
    public IReadOnlyList<string> Servers { get; }

    /// <summary>要列的資料庫；空名單表示不限。</summary>
    public IReadOnlyList<string> Databases { get; }

    public DateTimeOffset? Since { get; }
    public DateTimeOffset? Until { get; }
    public string? Cursor { get; }
}

// 列表只帶有界預覽；SQL 全文另以 ContentId 按需讀取。
/// <param name="CreatedAt">執行列是最後一次執行的時間；清單依它排序。</param>
/// <param name="ExecutionCount">
/// 同一 Session 連續以相同內容與連線執行時併成一列的次數；草稿固定為 1。
/// 維護回收較舊的執行後會跟著減少，只代表仍保存的執行。
/// </param>
/// <param name="FirstExecutedAt">仍保存的最早一次執行；草稿為 null。</param>
[Serializable]
public sealed record SqlHistoryItem(Guid ItemId, Guid SessionId, Guid? RevisionId,
    string ContentId, DateTimeOffset CreatedAt, SqlHistoryFilter Kind, string DisplayName,
    string Preview, SqlConnectionLabel? Connection, int ExecutionCount = 1, DateTimeOffset? FirstExecutedAt = null);
