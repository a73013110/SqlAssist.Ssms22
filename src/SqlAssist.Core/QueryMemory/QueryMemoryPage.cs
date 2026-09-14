using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.QueryMemory;

/// <summary>游標由儲存層產生，必須包含穩定排序的時間與唯一鍵，並綁定原篩選條件。</summary>
[Serializable]
public sealed class QueryMemoryPage<T>
{
    public QueryMemoryPage(IEnumerable<T> items, string? nextCursor)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        Items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
    }

    public IReadOnlyList<T> Items { get; }
    public string? NextCursor { get; }
}

[Serializable]
public sealed class QueryHistoryRequest
{
    public QueryHistoryRequest(int pageSize, QueryHistoryKind kind = QueryHistoryKind.All,
        string? search = null, string? server = null, string? database = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null, string? cursor = null)
    {
        if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (!Enum.IsDefined(typeof(QueryHistoryKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (since > until) throw new ArgumentException("起始時間不可晚於結束時間。", nameof(since));
        PageSize = pageSize;
        Kind = kind;
        Search = search;
        Server = server;
        Database = database;
        Since = since?.ToUniversalTime();
        Until = until?.ToUniversalTime();
        Cursor = cursor;
    }

    public int PageSize { get; }
    public QueryHistoryKind Kind { get; }
    public string? Search { get; }
    public string? Server { get; }
    public string? Database { get; }
    public DateTimeOffset? Since { get; }
    public DateTimeOffset? Until { get; }
    public string? Cursor { get; }
}

// 列表只帶有界預覽；SQL 全文另以 ContentId 按需讀取。
[Serializable]
public sealed record QueryHistoryItem(Guid ItemId, Guid SessionId, Guid? RevisionId,
    string ContentId, DateTimeOffset CreatedAt, QueryHistoryKind Kind, string DisplayName,
    string Preview, QueryConnectionContext? Connection);
