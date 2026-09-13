using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

public sealed partial class SqliteQueryMemoryRepository
{
    public Task<QuerySessionState?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: true);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadSession(connection, transaction, sessionId);
    }, cancellationToken);

    private static QuerySessionState? ReadSession(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        QuerySession session;
        long version, sequence;
        Guid? head, execution;
        using (var command = Command(connection, transaction, @"SELECT DocumentId,StartedAt,ClosedAt,Version,LastSequence,
LatestRevisionId,LatestExecutionRevisionId FROM Sessions WHERE SessionId=$id;", ("$id", Id(sessionId))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            session = new QuerySession(sessionId, Guid.ParseExact(reader.GetString(0), "N"), Time(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : Time(reader.GetInt64(2)));
            version = reader.GetInt64(3);
            sequence = reader.GetInt64(4);
            head = GuidOrNull(reader, 5);
            execution = GuidOrNull(reader, 6);
        }
        return new QuerySessionState(session, version, sequence,
            head.HasValue ? ReadRevision(connection, transaction, head.Value) : null,
            execution.HasValue ? ReadRevision(connection, transaction, execution.Value) : null);
    }

    private static QueryRevision? ReadRevision(SqliteConnection connection, SqliteTransaction transaction, Guid revisionId)
    {
        using var command = Command(connection, transaction, @"SELECT r.ParentRevisionId,r.ContentId,r.SessionId,r.CreatedAt,
r.Reason,r.IsExecutionSelection,c.Server,c.DatabaseName,c.IdentityName
FROM Revisions r LEFT JOIN Contexts c ON c.ContextId=r.ContextId WHERE r.RevisionId=$id;", ("$id", Id(revisionId)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new QueryRevision(revisionId, GuidOrNull(reader, 0), reader.GetString(1),
            Guid.ParseExact(reader.GetString(2), "N"), Time(reader.GetInt64(3)), (QueryRevisionReason)reader.GetInt32(4),
            ReadContext(reader, 6), reader.GetBoolean(5)) : null;
    }

    private static QueryConnectionContext? ReadContext(SqliteDataReader reader, int start) => reader.IsDBNull(start) ? null :
        new QueryConnectionContext(reader.GetString(start), reader.GetString(start + 1), StringOrNull(reader, start + 2));

    public Task<QueryContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken)
    {
        if (contentId == null) throw new ArgumentNullException(nameof(contentId));
        return Task.Run<QueryContent?>(() =>
        {
            using var connection = Connect();
            using var command = Command(connection, null, "SELECT SqlBytes,Length,ContentHash FROM Contents WHERE ContentId=$id;", ("$id", contentId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var content = QueryContent.Create(SqliteText.Decode((byte[])reader.GetValue(0)));
            if (content.ContentId != contentId || content.Length != reader.GetInt64(1) || content.ContentHash != reader.GetString(2))
                throw new InvalidDataException("SQL 內容完整性檢查失敗。");
            return content;
        }, cancellationToken);
    }

    public Task<QueryMemoryPage<QueryHistoryItem>> ReadHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return Task.Run(() =>
        {
            try { return ReadHistory(request, cancellationToken); }
            catch (SqliteException) when (cancellationToken.IsCancellationRequested)
            {
                // SQLite 會包裝 scalar function 的取消例外；對呼叫端仍保留取消語意。
                throw new OperationCanceledException(cancellationToken);
            }
        }, cancellationToken);
    }

    private QueryMemoryPage<QueryHistoryItem> ReadHistory(QueryHistoryRequest request, CancellationToken cancellationToken)
    {
        var cursor = SqliteHistoryCursor.Decode(request, _storeId);
        using var connection = Connect();
        var conditions = new List<string>();
        var parameters = new List<(string, object?)> { ("$limit", request.PageSize + 1) };
        if (request.Kind == QueryHistoryKind.Executed || request.Kind == QueryHistoryKind.Drafts)
        { conditions.Add("h.Kind=$kind"); parameters.Add(("$kind", (int)request.Kind)); }
        if (request.Kind == QueryHistoryKind.Pinned) conditions.Add("h.Pinned=1");
        if (request.Server != null) { conditions.Add("h.Server=$server"); parameters.Add(("$server", request.Server)); }
        if (request.Database != null) { conditions.Add("h.DatabaseName=$database"); parameters.Add(("$database", request.Database)); }
        if (request.Since.HasValue) { conditions.Add("h.CreatedAt >= $since"); parameters.Add(("$since", Ticks(request.Since.Value))); }
        if (request.Until.HasValue) { conditions.Add("h.CreatedAt < $until"); parameters.Add(("$until", Ticks(request.Until.Value))); }
        if (cursor != null)
        {
            conditions.Add("(h.CreatedAt, h.EntryKey) < ($time, $key)");
            parameters.Add(("$time", cursor.Ticks)); parameters.Add(("$key", cursor.EntryKey));
        }
        // History 只搜尋 SQL 全文；顯示名稱與連線不是搜尋目標，語意與 Favorite 的欄位清單分開。
        if (SqliteSearchFilter.Create(request.Search) is SqliteSearchFilter search)
            conditions.Add(search.Apply(connection, parameters, "c.SqlBytes", cancellationToken));
        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        // 投影只拿 Preview。全文搜尋雖需掃候選 BLOB，但不將全部 SQL 載入列表或應用程式快取。
        using var command = Command(connection, null, @"SELECT h.EntryKey,h.SessionId,h.RevisionId,h.ContentId,h.CreatedAt,
h.Kind,d.DisplayName,c.Preview,x.Server,x.DatabaseName,x.IdentityName,h.Pinned
FROM History h JOIN Sessions s ON s.SessionId=h.SessionId JOIN Documents d ON d.DocumentId=s.DocumentId
JOIN Contents c ON c.ContentId=h.ContentId LEFT JOIN Contexts x ON x.ContextId=h.ContextId" + where +
            " ORDER BY h.CreatedAt DESC,h.EntryKey DESC LIMIT $limit;", parameters.ToArray());
        using var reader = command.ExecuteReader();
        var items = new List<QueryHistoryItem>();
        string? nextCursor = null;
        string? lastKey = null;
        long lastTicks = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == request.PageSize)
            {
                nextCursor = SqliteHistoryCursor.Encode(request, _storeId, lastTicks, lastKey ?? throw new InvalidDataException("分頁缺少尾端鍵。"));
                break;
            }
            lastKey = reader.GetString(0);
            lastTicks = reader.GetInt64(4);
            items.Add(new QueryHistoryItem(Guid.ParseExact(lastKey.Substring(1), "N"), Guid.ParseExact(reader.GetString(1), "N"),
                GuidOrNull(reader, 2), reader.GetString(3), Time(lastTicks), (QueryHistoryKind)reader.GetInt32(5),
                reader.GetString(6), reader.GetString(7), ReadContext(reader, 8), reader.GetBoolean(11)));
        }
        return new QueryMemoryPage<QueryHistoryItem>(items, nextCursor);
    }
}
