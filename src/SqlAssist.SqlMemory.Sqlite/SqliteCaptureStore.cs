using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteContentRows;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>擷取與歷程：Session CAS 提交、History 分頁、全文讀取與連線 facets。</summary>
internal sealed class SqliteCaptureStore
{
    private readonly SqliteDatabase _database;
    private readonly SqliteSearchBudget _searchBudget;

    public SqliteCaptureStore(SqliteDatabase database, SqliteSearchBudget? searchBudget = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _searchBudget = searchBudget ?? SqliteSearchBudget.Default;
    }

    public SqlSessionHead? ReadSession(Guid sessionId, CancellationToken cancellationToken)
    {
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: true);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadSession(connection, transaction, sessionId);
    }

    /// <remarks>提交前最後一次檢查取消；已提交就回傳結果，不因之後的取消改稱失敗。</remarks>
    public SqlHistoryCommitResult Commit(SqlCaptureCommit write, string? leaseId, CancellationToken cancellationToken)
    {
        if (write == null) throw new ArgumentNullException(nameof(write));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Connect();
        // IMMEDIATE 在讀 head 之前取得寫鎖，避免 deferred 交易升級時的 SQLITE_BUSY_SNAPSHOT。
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var duplicate = Command(connection, transaction,
            "SELECT SessionId, Sequence FROM Captures WHERE CaptureId=$id;", ("$id", Id(write.CaptureId))))
        using (var reader = duplicate.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != Id(write.State.Session.SessionId) || reader.GetInt64(1) != write.State.LastSequence)
                    throw new InvalidDataException("CaptureId 已屬於其他擷取。");
                return SqlHistoryCommitResult.AlreadyCommitted;
            }
        }
        var previous = ReadSession(connection, transaction, write.State.Session.SessionId);
        if (previous?.Version != write.ExpectedVersion) return SqlHistoryCommitResult.Conflict;
        var state = write.State;
        if (state.Version != checked((previous?.Version ?? 0) + 1) || state.LastSequence <= (previous?.LastSequence ?? 0) ||
            previous?.Session.ClosedAt != null || (previous != null && previous.Session.DocumentId != write.Document.DocumentId))
            throw new InvalidDataException("寫入計畫的 Session 狀態不一致。");
        string? oldRecoveryContent;
        using (var old = Command(connection, transaction, "SELECT ContentId FROM Recovery WHERE SessionId=$id;", ("$id", Id(state.Session.SessionId))))
            oldRecoveryContent = old.ExecuteScalar() as string;
        cancellationToken.ThrowIfCancellationRequested();
        Execute(connection, transaction, @"INSERT INTO Documents VALUES($id,$name,$path)
ON CONFLICT(DocumentId) DO UPDATE SET DisplayName=excluded.DisplayName, FilePath=excluded.FilePath;",
            ("$id", Id(write.Document.DocumentId)), ("$name", write.Document.DisplayName), ("$path", write.Document.FilePath));
        foreach (var content in write.Contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteContent(connection, transaction, content);
        }
        foreach (var revision in write.Revisions)
        {
            var contextId = WriteContext(connection, transaction, revision.Connection);
            // 明列資料行：擷取寫入不帶 Favorite 版本標記，schema 再加欄位也不會錯位。
            Execute(connection, transaction, @"INSERT INTO Revisions
(RevisionId,ParentRevisionId,ContentId,SessionId,CreatedAt,Reason,ContextId,IsExecutionSelection)
VALUES($id,$parent,$content,$session,$time,$reason,$context,$selection);",
                ("$id", Id(revision.RevisionId)), ("$parent", Id(revision.ParentRevisionId)), ("$content", revision.ContentId),
                ("$session", Id(revision.SessionId)), ("$time", Ticks(revision.CreatedAt)), ("$reason", (int)revision.Reason),
                ("$context", contextId), ("$selection", revision.IsExecutionSelection));
            // 執行版本只由 Execution 顯示，避免每次執行同時冒出一筆假 Draft。
            if (!revision.IsExecutionSelection && revision.Reason != SqlRevisionReason.BeforeExecute)
                WriteHistory(connection, transaction, "r" + Id(revision.RevisionId), state.Session.SessionId,
                    revision.RevisionId, revision.ContentId, revision.CreatedAt, SqlHistoryFilter.Drafts,
                    revision.Connection, contextId);
        }
        // 明列資料行並標上呼叫端傳入的租約：有租約就代表還可能在編輯，維護不得回收這個 Session 的 Recovery。
        // 租約在交易內重查：另一個程序可能在心跳之前已回收它，外鍵失敗會讓整個 writer 停擺。
        // 回收後寫成無租約，與 ReleaseLeases 對既有 Session 的處理一致，下一次心跳重開後再標上。
        Execute(connection, transaction, @"INSERT INTO Sessions
(SessionId,DocumentId,StartedAt,ClosedAt,Version,LastSequence,LatestRevisionId,LatestExecutionRevisionId,LeaseId)
VALUES($id,$document,$start,$close,$version,$sequence,$head,$execution,(SELECT LeaseId FROM Leases WHERE LeaseId=$lease))
ON CONFLICT(SessionId) DO UPDATE SET ClosedAt=excluded.ClosedAt, Version=excluded.Version,
LastSequence=excluded.LastSequence, LatestRevisionId=excluded.LatestRevisionId,
LatestExecutionRevisionId=excluded.LatestExecutionRevisionId, LeaseId=excluded.LeaseId;",
            ("$lease", leaseId),
            ("$id", Id(state.Session.SessionId)), ("$document", Id(state.Session.DocumentId)), ("$start", Ticks(state.Session.StartedAt)),
            ("$close", state.Session.ClosedAt.HasValue ? (object)Ticks(state.Session.ClosedAt.Value) : null),
            ("$version", state.Version), ("$sequence", state.LastSequence), ("$head", Id(state.LatestRevision?.RevisionId)),
            ("$execution", Id(state.LatestExecutionRevision?.RevisionId)));
        if (write.Execution != null)
        {
            var execution = write.Execution;
            var contextId = WriteContext(connection, transaction, execution.Connection);
            Execute(connection, transaction, "INSERT INTO Executions VALUES($id,$revision,$time,$context,$scope);",
                ("$id", Id(execution.ExecutionId)), ("$revision", Id(execution.RevisionId)), ("$time", Ticks(execution.ExecutedAt)),
                ("$context", contextId), ("$scope", (int)execution.Scope));
            var revision = ReadRevision(connection, transaction, execution.RevisionId)
                ?? throw new InvalidDataException("Execution 缺少 Revision。");
            WriteHistory(connection, transaction, "e" + Id(execution.ExecutionId), state.Session.SessionId,
                execution.RevisionId, revision.ContentId, execution.ExecutedAt, SqlHistoryFilter.Executions, execution.Connection, contextId);
        }
        if (write.DeleteRecovery)
        {
            Execute(connection, transaction, "DELETE FROM Recovery WHERE SessionId=$id; DELETE FROM History WHERE EntryKey=$key;",
                ("$id", Id(state.Session.SessionId)), ("$key", "s" + Id(state.Session.SessionId)));
        }
        else if (write.Recovery != null)
        {
            var recovery = write.Recovery;
            var contextId = WriteContext(connection, transaction, recovery.Connection);
            Execute(connection, transaction, @"INSERT INTO Recovery VALUES($id,$content,$sequence,$time,$context)
ON CONFLICT(SessionId) DO UPDATE SET ContentId=excluded.ContentId, Sequence=excluded.Sequence,
CapturedAt=excluded.CapturedAt, ContextId=excluded.ContextId;",
                ("$id", Id(recovery.SessionId)), ("$content", recovery.ContentId), ("$sequence", recovery.Sequence),
                ("$time", Ticks(recovery.CapturedAt)), ("$context", contextId));
            WriteHistory(connection, transaction, "s" + Id(recovery.SessionId), recovery.SessionId, null, recovery.ContentId,
                recovery.CapturedAt, SqlHistoryFilter.Drafts, recovery.Connection, contextId);
        }
        Execute(connection, transaction, "INSERT INTO Captures VALUES($id,$session,$sequence);",
            ("$id", Id(write.CaptureId)), ("$session", Id(state.Session.SessionId)), ("$sequence", state.LastSequence));
        if (oldRecoveryContent != null && (write.DeleteRecovery ||
            (write.Recovery != null && write.Recovery.ContentId != oldRecoveryContent)))
        {
            // 只檢查這次被替換的內容，不做全庫 GC；保留任何版本與其他 Session 的引用。
            Execute(connection, transaction, "DELETE FROM Contents WHERE ContentId=$id" + Unreferenced + ";", ("$id", oldRecoveryContent));
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return SqlHistoryCommitResult.Committed;
    }

    public SqlContent? ReadContent(string contentId, CancellationToken cancellationToken)
    {
        if (contentId == null) throw new ArgumentNullException(nameof(contentId));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Connect();
        using var command = Command(connection, null, "SELECT SqlBytes,Length,ContentHash FROM Contents WHERE ContentId=$id;", ("$id", contentId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var content = SqlContent.Create(SqliteText.Decode((byte[])reader.GetValue(0)));
        if (content.ContentId != contentId || content.Length != reader.GetInt64(1) || content.ContentHash != reader.GetString(2))
            throw new InvalidDataException("SQL 內容完整性檢查失敗。");
        return content;
    }

    /// <remarks>
    /// 投影鍵依種類前綴：執行 e、Recovery s（Session 識別碼）、草稿版本 r。與寫入 History 時的鍵同源，
    /// 所以只憑列表項目就能還原，不必把儲存層的鍵放進 Core 契約。
    /// 本體只刪自己那一列；版本改用與維護相同的引用清單，仍被引用就留給維護，不做 CASCADE。
    /// </remarks>
    public SqlHistoryDeleteResult DeleteHistory(SqlHistoryItem item, CancellationToken cancellationToken)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        cancellationToken.ThrowIfCancellationRequested();
        var key = item.Kind == SqlHistoryFilter.Executions ? "e" + Id(item.ItemId)
            : item.RevisionId == null ? "s" + Id(item.SessionId) : "r" + Id(item.ItemId);
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        var contents = new HashSet<string>(StringComparer.Ordinal);
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        string? revision;
        using (var read = Command(connection, transaction,
            "SELECT RevisionId,ContentId,ContextId FROM History WHERE EntryKey=$key AND SessionId=$session;",
            ("$key", key), ("$session", Id(item.SessionId))))
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) return SqlHistoryDeleteResult.NotFound;
            revision = StringOrNull(reader, 0);
            Release(contents, contexts, reader.GetString(1), StringOrNull(reader, 2));
        }
        Execute(connection, transaction, "DELETE FROM History WHERE EntryKey=$key;", ("$key", key));
        if (key[0] == 'e')
        {
            DeleteReleasing(connection, transaction, contents, contexts, "Executions", "ExecutionId=$id", ("$id", Id(item.ItemId)));
        }
        else if (key[0] == 's')
        {
            // 仍開著的視窗下一次擷取會重寫 Recovery；這裡只移除使用者看到的那一份。
            DeleteReleasing(connection, transaction, contents, contexts, "Recovery", "SessionId=$id", ("$id", Id(item.SessionId)));
        }
        if (revision != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteReleasing(connection, transaction, contents, contexts, "Revisions",
                "RevisionId=$revision" + SqliteContentRows.UnreferencedRevision, ("$revision", revision));
        }
        foreach (var content in contents)
            Execute(connection, transaction, "DELETE FROM Contents WHERE ContentId=$id" + Unreferenced + ";", ("$id", content));
        foreach (var context in contexts)
            Execute(connection, transaction, SqliteContentRows.DeleteUnreferencedContext, ("$id", context));
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return SqlHistoryDeleteResult.Deleted;
    }

    /// <summary>先記下列引用的內容與連線再刪；條件不成立（仍被引用）時什麼都不釋出。</summary>
    private static void DeleteReleasing(SqliteConnection connection, SqliteTransaction transaction, ISet<string> contents,
        ISet<string> contexts, string table, string condition, params (string Name, object? Value)[] parameters)
    {
        string? content = null, context = null;
        var found = false;
        using (var read = Command(connection, transaction, "SELECT * FROM " + table + " WHERE " + condition + ";", parameters))
        using (var reader = read.ExecuteReader())
        {
            if (reader.Read())
            {
                found = true;
                content = Column(reader, "ContentId");
                context = Column(reader, "ContextId");
            }
        }
        if (!found) return;
        Execute(connection, transaction, "DELETE FROM " + table + " WHERE " + condition + ";", parameters);
        Release(contents, contexts, content, context);
    }

    private static string? Column(SqliteDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
            if (reader.GetName(i) == name) return StringOrNull(reader, i);
        return null;
    }

    private static void Release(ISet<string> contents, ISet<string> contexts, string? content, string? context)
    {
        if (content != null) contents.Add(content);
        if (context != null) contexts.Add(context);
    }

    public SqlMemoryPage<SqlHistoryItem> ReadHistory(SqlHistoryRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = SqliteHistoryCursor.Decode(request, _database.StoreId);
        var search = SqliteSearchScan.Create(request.Search, _searchBudget, cancellationToken);
        using var connection = _database.Connect();
        var conditions = new List<string>();
        var parameters = new List<(string, object?)> { ("$limit", search?.CandidateLimit ?? request.PageSize + 1) };
        if (request.Kind == SqlHistoryFilter.Executions || request.Kind == SqlHistoryFilter.Drafts)
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
        var items = new List<SqlHistoryItem>();
        string? lastKey = null;
        long lastTicks = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (search?.IsExhausted == true)
                return new SqlMemoryPage<SqlHistoryItem>(items,
                    SqliteHistoryCursor.Encode(request, _database.StoreId, lastTicks, lastKey!), Time(lastTicks));
            var key = reader.GetString(0);
            var ticks = reader.GetInt64(4);
            // History 只搜尋 SQL 全文；顯示名稱與連線不是搜尋目標，語意與 Favorite 的欄位清單分開。
            var matched = search == null || search.Matches((byte[])reader.GetValue(10));
            if (matched && items.Count == request.PageSize)
                return new SqlMemoryPage<SqlHistoryItem>(items, SqliteHistoryCursor.Encode(request, _database.StoreId, lastTicks, lastKey!));
            // 未命中的列也推進位置：部分搜尋的游標接在最後檢查過的候選之後，下一頁不重掃。
            lastKey = key;
            lastTicks = ticks;
            if (matched)
                items.Add(new SqlHistoryItem(Guid.ParseExact(key.Substring(1), "N"), Guid.ParseExact(reader.GetString(1), "N"),
                    GuidOrNull(reader, 2), reader.GetString(3), Time(ticks), (SqlHistoryFilter)reader.GetInt32(5),
                    reader.GetString(6), reader.GetString(7), ReadContext(reader, 8)));
        }
        return new SqlMemoryPage<SqlHistoryItem>(items, null);
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

    public string[] ReadConnectionFacets(SqlConnectionFacetRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Connect();
        var column = request.Databases ? "h.DatabaseName" : "h.Server";
        var source = request.IsFavorites ? "Favorites h JOIN Revisions r ON r.RevisionId=h.CurrentRevisionId" : "History h";
        var time = request.IsFavorites ? "r.CreatedAt" : "h.CreatedAt";
        var order = request.Sort switch
        {
            SqlConnectionFacetSort.Oldest => "MIN(" + time + ") ASC, Name ASC",
            SqlConnectionFacetSort.Alphabetical => "Name COLLATE NOCASE ASC, Name ASC",
            SqlConnectionFacetSort.ReverseAlphabetical => "Name COLLATE NOCASE DESC, Name DESC",
            _ => "MAX(" + time + ") DESC, Name ASC"
        };
        // 識別字與排序僅來自上述封閉集合；所有使用者值仍以參數傳入。
        using var command = Command(connection, null, "SELECT " + column + " AS Name FROM " + source +
            " WHERE " + column + " IS NOT NULL AND " + column + " <> ''" +
            (request.IsFavorites ? " AND h.Scope=$scope" : "") +
            (request.Databases && request.Server != null ? " AND h.Server=$server" : "") +
            " GROUP BY " + column + " ORDER BY " + order + " LIMIT $limit OFFSET $offset;",
            ("$scope", (int)request.Scope), ("$server", request.Server),
            ("$limit", SqlConnectionFacetRequest.PageSize + 1), ("$offset", request.Offset));
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); names.Add(reader.GetString(0)); }
        return names.ToArray();
    }

    private static SqlSessionHead? ReadSession(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        SqlSession session;
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
            session = new SqlSession(sessionId, Guid.ParseExact(reader.GetString(0), "N"), Time(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : Time(reader.GetInt64(2)));
            version = reader.GetInt64(3);
            sequence = reader.GetInt64(4);
            head = GuidOrNull(reader, 5);
            execution = GuidOrNull(reader, 6);
            recoveryContentId = StringOrNull(reader, 7);
        }
        return new SqlSessionHead(session, version, sequence,
            head.HasValue ? ReadRevision(connection, transaction, head.Value) : null,
            execution.HasValue ? ReadRevision(connection, transaction, execution.Value) : null,
            recoveryContentId);
    }

    private static SqlRevision? ReadRevision(SqliteConnection connection, SqliteTransaction transaction, Guid revisionId)
    {
        using var command = Command(connection, transaction, @"SELECT r.ParentRevisionId,r.ContentId,r.SessionId,r.CreatedAt,
r.Reason,r.IsExecutionSelection,c.Server,c.DatabaseName
FROM Revisions r LEFT JOIN Contexts c ON c.ContextId=r.ContextId WHERE r.RevisionId=$id;", ("$id", Id(revisionId)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new SqlRevision(revisionId, GuidOrNull(reader, 0), reader.GetString(1),
            GuidOrNull(reader, 2), Time(reader.GetInt64(3)), (SqlRevisionReason)reader.GetInt32(4),
            ReadContext(reader, 6), reader.GetBoolean(5)) : null;
    }

    private static void WriteHistory(SqliteConnection connection, SqliteTransaction transaction, string key, Guid sessionId,
        Guid? revisionId, string contentId, DateTimeOffset time, SqlHistoryFilter kind, SqlConnectionLabel? context, string? contextId)
    {
        Execute(connection, transaction, @"INSERT INTO History(EntryKey,SessionId,RevisionId,ContentId,CreatedAt,Kind,ContextId,Server,DatabaseName)
VALUES($key,$session,$revision,$content,$time,$kind,$context,$server,$database)
ON CONFLICT(EntryKey) DO UPDATE SET ContentId=excluded.ContentId, CreatedAt=excluded.CreatedAt,
ContextId=excluded.ContextId, Server=excluded.Server, DatabaseName=excluded.DatabaseName;",
            ("$key", key), ("$session", Id(sessionId)), ("$revision", Id(revisionId)), ("$content", contentId),
            ("$time", Ticks(time)), ("$kind", (int)kind), ("$context", contextId), ("$server", context?.Server), ("$database", context?.Database));
    }
}
