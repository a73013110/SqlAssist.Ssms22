using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

internal sealed partial class SqliteQueryMemoryRepository
{
    public QuerySessionState? ReadSession(Guid sessionId, CancellationToken cancellationToken)
    {
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: true);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadSession(connection, transaction, sessionId);
    }

    private static QuerySessionState? ReadSession(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        QuerySession session;
        long version, sequence;
        Guid? head, execution;
        string? recoveryContentId;
        // LEFT JOIN 到 Recovery：讓引擎不必另外查一次就能判斷目前內容是否已經和 Recovery 相同。
        using (var command = Command(connection, transaction, @"SELECT s.DocumentId,s.StartedAt,s.ClosedAt,s.Version,s.LastSequence,
s.LatestRevisionId,s.LatestExecutionRevisionId,r.ContentId
FROM Sessions s LEFT JOIN Recovery r ON r.SessionId=s.SessionId WHERE s.SessionId=$id;", ("$id", Id(sessionId))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            session = new QuerySession(sessionId, Guid.ParseExact(reader.GetString(0), "N"), Time(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : Time(reader.GetInt64(2)));
            version = reader.GetInt64(3);
            sequence = reader.GetInt64(4);
            head = GuidOrNull(reader, 5);
            execution = GuidOrNull(reader, 6);
            recoveryContentId = StringOrNull(reader, 7);
        }
        return new QuerySessionState(session, version, sequence,
            head.HasValue ? ReadRevision(connection, transaction, head.Value) : null,
            execution.HasValue ? ReadRevision(connection, transaction, execution.Value) : null,
            recoveryContentId);
    }

    private static QueryRevision? ReadRevision(SqliteConnection connection, SqliteTransaction transaction, Guid revisionId)
    {
        using var command = Command(connection, transaction, @"SELECT r.ParentRevisionId,r.ContentId,r.SessionId,r.CreatedAt,
r.Reason,r.IsExecutionSelection,c.Server,c.DatabaseName
FROM Revisions r LEFT JOIN Contexts c ON c.ContextId=r.ContextId WHERE r.RevisionId=$id;", ("$id", Id(revisionId)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new QueryRevision(revisionId, GuidOrNull(reader, 0), reader.GetString(1),
            Guid.ParseExact(reader.GetString(2), "N"), Time(reader.GetInt64(3)), (QueryRevisionReason)reader.GetInt32(4),
            ReadContext(reader, 6), reader.GetBoolean(5)) : null;
    }

    private static QueryConnectionContext? ReadContext(SqliteDataReader reader, int start) => reader.IsDBNull(start) ? null :
        new QueryConnectionContext(reader.GetString(start), reader.GetString(start + 1));

    public QueryContent? ReadContent(string contentId, CancellationToken cancellationToken)
    {
        if (contentId == null) throw new ArgumentNullException(nameof(contentId));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Connect();
        using var command = Command(connection, null, "SELECT SqlBytes,Length,ContentHash FROM Contents WHERE ContentId=$id;", ("$id", contentId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var content = QueryContent.Create(SqliteText.Decode((byte[])reader.GetValue(0)));
        if (content.ContentId != contentId || content.Length != reader.GetInt64(1) || content.ContentHash != reader.GetString(2))
            throw new InvalidDataException("SQL 內容完整性檢查失敗。");
        return content;
    }

    public QueryMemoryPage<QueryHistoryItem> ReadHistory(QueryHistoryRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = SqliteHistoryCursor.Decode(request, _storeId);
        var search = SqliteSearchScan.Create(request.Search, _searchBudget, cancellationToken);
        using var connection = Connect();
        var conditions = new List<string>();
        var parameters = new List<(string, object?)> { ("$limit", search?.CandidateLimit ?? request.PageSize + 1) };
        if (request.Kind == QueryHistoryKind.Executed || request.Kind == QueryHistoryKind.Drafts)
        { conditions.Add("h.Kind=$kind"); parameters.Add(("$kind", (int)request.Kind)); }
        if (request.Server != null) { conditions.Add("h.Server=$server"); parameters.Add(("$server", request.Server)); }
        if (request.Database != null) { conditions.Add("h.DatabaseName=$database"); parameters.Add(("$database", request.Database)); }
        if (request.Since.HasValue) { conditions.Add("h.CreatedAt >= $since"); parameters.Add(("$since", Ticks(request.Since.Value))); }
        if (request.Until.HasValue) { conditions.Add("h.CreatedAt < $until"); parameters.Add(("$until", Ticks(request.Until.Value))); }
        if (cursor != null)
        {
            conditions.Add("(h.CreatedAt, h.EntryKey) < ($time, $key)");
            parameters.Add(("$time", cursor.Ticks)); parameters.Add(("$key", cursor.EntryKey));
        }
        using var command = Command(connection, null, HistoryPageSql(conditions, search != null), parameters.ToArray());
        using var reader = command.ExecuteReader();
        var items = new List<QueryHistoryItem>();
        string? lastKey = null;
        long lastTicks = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (search?.IsExhausted == true)
                return new QueryMemoryPage<QueryHistoryItem>(items,
                    SqliteHistoryCursor.Encode(request, _storeId, lastTicks, lastKey!), Time(lastTicks));
            var key = reader.GetString(0);
            var ticks = reader.GetInt64(4);
            // History 只搜尋 SQL 全文；顯示名稱與連線不是搜尋目標，語意與 Favorite 的欄位清單分開。
            var matched = search == null || search.Matches((byte[])reader.GetValue(10));
            if (matched && items.Count == request.PageSize)
                return new QueryMemoryPage<QueryHistoryItem>(items, SqliteHistoryCursor.Encode(request, _storeId, lastTicks, lastKey!));
            // 未命中的列也推進位置：部分搜尋的游標接在最後檢查過的候選之後，下一頁不重掃。
            lastKey = key;
            lastTicks = ticks;
            if (matched)
                items.Add(new QueryHistoryItem(Guid.ParseExact(key.Substring(1), "N"), Guid.ParseExact(reader.GetString(1), "N"),
                    GuidOrNull(reader, 2), reader.GetString(3), Time(ticks), (QueryHistoryKind)reader.GetInt32(5),
                    reader.GetString(6), reader.GetString(7), ReadContext(reader, 8)));
        }
        return new QueryMemoryPage<QueryHistoryItem>(items, null);
    }

    /// <summary>
    /// 投影只拿 Preview；搜尋時多帶 SqlBytes 給讀取端比對，但不將全部 SQL 載入列表或應用程式快取。
    /// 必須沿時間索引串流而沒有暫存排序，搜尋預算才真的限制讀入的 BLOB；由 EXPLAIN 測試守住。
    /// </summary>
    internal static string HistoryPageSql(IReadOnlyCollection<string> conditions, bool includeSql) =>
        @"SELECT h.EntryKey,h.SessionId,h.RevisionId,h.ContentId,h.CreatedAt,
h.Kind,d.DisplayName,c.Preview,x.Server,x.DatabaseName" + (includeSql ? ",c.SqlBytes" : "") + @"
FROM History h JOIN Sessions s ON s.SessionId=h.SessionId JOIN Documents d ON d.DocumentId=s.DocumentId
JOIN Contents c ON c.ContentId=h.ContentId LEFT JOIN Contexts x ON x.ContextId=h.ContextId" +
        (conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions)) +
        " ORDER BY h.CreatedAt DESC,h.EntryKey DESC LIMIT $limit;";
}
