using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.Connections;
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
        string? oldRecoveryContent, previousExecutionEntry;
        using (var old = Command(connection, transaction, "SELECT ContentId FROM Recovery WHERE SessionId=$id;", ("$id", Id(state.Session.SessionId))))
            oldRecoveryContent = old.ExecuteScalar() as string;
        using (var old = Command(connection, transaction, "SELECT LatestExecutionEntryKey FROM Sessions WHERE SessionId=$id;",
            ("$id", Id(state.Session.SessionId))))
            previousExecutionEntry = old.ExecuteScalar() as string;
        cancellationToken.ThrowIfCancellationRequested();
        Execute(connection, transaction, @"INSERT INTO Documents(DocumentId,DisplayName) VALUES($id,$name)
ON CONFLICT(DocumentId) DO UPDATE SET DisplayName=excluded.DisplayName;",
            ("$id", Id(write.Document.DocumentId)), ("$name", write.Document.DisplayName));
        foreach (var content in write.Contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteContent(connection, transaction, content);
        }
        foreach (var revision in write.Revisions)
        {
            // 明列資料行：擷取寫入不帶 Favorite 版本標記，schema 再加欄位也不會錯位。
            Execute(connection, transaction, @"INSERT INTO Revisions
(RevisionId,ParentRevisionId,ContentId,SessionId,CreatedAt,Reason,IsExecutionSelection)
VALUES($id,$parent,$content,$session,$time,$reason,$selection);",
                ("$id", Id(revision.RevisionId)), ("$parent", Id(revision.ParentRevisionId)), ("$content", revision.ContentId),
                ("$session", Id(revision.SessionId)), ("$time", Ticks(revision.CreatedAt)), ("$reason", (int)revision.Reason),
                ("$selection", revision.IsExecutionSelection));
            // 執行版本只由 Execution 顯示，避免每次執行同時冒出一筆假 Draft。
            if (!revision.IsExecutionSelection && revision.Reason != SqlRevisionReason.BeforeExecute)
                WriteHistory(connection, transaction, "r" + Id(revision.RevisionId), state.Session.SessionId,
                    revision.RevisionId, revision.ContentId, revision.CreatedAt, SqlHistoryFilter.Drafts, write.Connection);
        }
        // 明列資料行並標上呼叫端傳入的租約：有租約就代表還可能在編輯，維護不得回收這個 Session 的 Recovery。
        // 租約在交易內重查：另一個程序可能在心跳之前已回收它，外鍵失敗會讓整個 writer 停擺。
        // 回收後寫成無租約，與 ReleaseLeases 對既有 Session 的處理一致，下一次心跳重開後再標上。
        Execute(connection, transaction, @"INSERT INTO Sessions
(SessionId,DocumentId,ClosedAt,Version,LastSequence,LatestRevisionId,LatestExecutionRevisionId,LeaseId)
VALUES($id,$document,$close,$version,$sequence,$head,$execution,(SELECT LeaseId FROM Leases WHERE LeaseId=$lease))
ON CONFLICT(SessionId) DO UPDATE SET ClosedAt=excluded.ClosedAt, Version=excluded.Version,
LastSequence=excluded.LastSequence, LatestRevisionId=excluded.LatestRevisionId,
LatestExecutionRevisionId=excluded.LatestExecutionRevisionId, LeaseId=excluded.LeaseId;",
            ("$lease", leaseId),
            ("$id", Id(state.Session.SessionId)), ("$document", Id(state.Session.DocumentId)),
            ("$close", state.Session.ClosedAt.HasValue ? (object)Ticks(state.Session.ClosedAt.Value) : null),
            ("$version", state.Version), ("$sequence", state.LastSequence), ("$head", Id(state.LatestRevision?.RevisionId)),
            ("$execution", Id(state.LatestExecutionRevision?.RevisionId)));
        if (write.Execution != null)
        {
            var execution = write.Execution;
            var revision = ReadRevision(connection, transaction, execution.RevisionId)
                ?? throw new InvalidDataException("Execution 缺少 Revision。");
            var entry = MergeExecution(connection, transaction, previousExecutionEntry, state.Session.SessionId, execution,
                revision.ContentId, write.Connection);
            if (entry == null)
            {
                entry = "e" + Id(execution.ExecutionId);
                WriteHistory(connection, transaction, entry, state.Session.SessionId,
                    execution.RevisionId, revision.ContentId, execution.ExecutedAt, SqlHistoryFilter.Executions, write.Connection);
            }
            Execute(connection, transaction, @"INSERT INTO Executions(ExecutionId,RevisionId,ExecutedAt,EntryKey) VALUES($id,$revision,$time,$entry);
UPDATE Sessions SET LatestExecutionEntryKey=$entry WHERE SessionId=$session;",
                ("$id", Id(execution.ExecutionId)), ("$revision", Id(execution.RevisionId)), ("$time", Ticks(execution.ExecutedAt)),
                ("$entry", entry), ("$session", Id(state.Session.SessionId)));
        }
        if (write.DeleteRecovery)
        {
            Execute(connection, transaction, "DELETE FROM Recovery WHERE SessionId=$id; DELETE FROM History WHERE EntryKey=$key;",
                ("$id", Id(state.Session.SessionId)), ("$key", "s" + Id(state.Session.SessionId)));
        }
        else if (write.Recovery != null)
        {
            var recovery = write.Recovery;
            Execute(connection, transaction, @"INSERT INTO Recovery(SessionId,ContentId,CapturedAt) VALUES($id,$content,$time)
ON CONFLICT(SessionId) DO UPDATE SET ContentId=excluded.ContentId, CapturedAt=excluded.CapturedAt;",
                ("$id", Id(recovery.SessionId)), ("$content", recovery.ContentId), ("$time", Ticks(recovery.CapturedAt)));
            WriteHistory(connection, transaction, "s" + Id(recovery.SessionId), recovery.SessionId, null, recovery.ContentId,
                recovery.CapturedAt, SqlHistoryFilter.Drafts, write.Connection);
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
        using var command = Command(connection, null, "SELECT SqlBytes,Length FROM Contents WHERE ContentId=$id;", ("$id", contentId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var content = SqlContent.Create(SqliteText.Decode((byte[])reader.GetValue(0)));
        // 內容位址就是雜湊：重算得到同一個 ContentId 才代表 BLOB 沒有損壞。
        if (content.ContentId != contentId || content.Length != reader.GetInt64(1))
            throw new InvalidDataException("SQL 內容完整性檢查失敗。");
        return content;
    }

    /// <remarks>
    /// 投影鍵依種類前綴：執行 e、Recovery s（Session 識別碼）、草稿版本 r。與寫入 History 時的鍵同源，
    /// 所以只憑列表項目就能還原，不必把儲存層的鍵放進 Core 契約。
    /// 本體只刪自己那一份：執行列是併進它的全部 Executions（使用者一次刪的是畫面上那一列，數量隨執行次數而定），
    /// 版本改用與維護相同的引用清單逐一重查，仍被引用就留給維護，不做 CASCADE。
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
        if (SqliteHistoryRows.Delete(connection, transaction, key, Id(item.SessionId), contents, cancellationToken) == 0)
            return SqlHistoryDeleteResult.NotFound;
        SqliteHistoryRows.CollectContents(connection, transaction, contents, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return SqlHistoryDeleteResult.Deleted;
    }

    public SqlMemoryPage<SqlHistoryItem> ReadHistory(SqlHistoryRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var binding = SqliteTimeCursor.Fingerprint(((int)request.Kind).ToString(CultureInfo.InvariantCulture), request.Search,
            ((int)request.MatchOptions).ToString(CultureInfo.InvariantCulture),
            SqlConnectionNames.Fingerprint(request.Servers), SqlConnectionNames.Fingerprint(request.Databases),
            request.Since?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            request.Until?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
        var cursor = SqliteTimeCursor.Decode(request.Cursor, HistoryCursor, _database.StoreId, binding, SqliteHistoryRows.IsKey);
        var search = SqliteSearchScan.Create(request.Search, request.MatchOptions, _searchBudget, cancellationToken);
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)> { ("$limit", search?.CandidateLimit ?? request.PageSize + 1) };
        if (request.Kind == SqlHistoryFilter.Executions || request.Kind == SqlHistoryFilter.Drafts)
        { conditions.Add("h.Kind=$kind"); parameters.Add(("$kind", (int)request.Kind)); }
        SqliteConnectionFilter.Append(conditions, parameters, "h", request.Servers, request.Databases);
        if (request.Since.HasValue) { conditions.Add("h.CreatedAt >= $since"); parameters.Add(("$since", Ticks(request.Since.Value))); }
        if (request.Until.HasValue) { conditions.Add("h.CreatedAt < $until"); parameters.Add(("$until", Ticks(request.Until.Value))); }
        cursor?.AppendCondition(conditions, parameters, "h.CreatedAt", "h.EntryKey");
        using var connection = _database.Connect();
        using var command = Command(connection, null, HistoryPageSql(conditions, search != null), parameters.ToArray());
        using var reader = command.ExecuteReader();
        // History 只搜尋 SQL 全文；顯示名稱與連線不是搜尋目標，語意與 Favorite 的欄位清單分開。
        return SqliteKeysetPage.Read(reader, request.PageSize, search,
            row => (row.GetInt64(4), row.GetString(0)),
            (row, scan) => scan.Matches((byte[])row.GetValue(12)),
            row => new SqlHistoryItem(Guid.ParseExact(row.GetString(0).Substring(1), "N"), Guid.ParseExact(row.GetString(1), "N"),
                GuidOrNull(row, 2), row.GetString(3), Time(row.GetInt64(4)), (SqlHistoryFilter)row.GetInt32(5),
                row.GetString(6), row.GetString(7), ReadConnection(row, 8), row.GetInt32(10),
                row.IsDBNull(11) ? null : Time(row.GetInt64(11))),
            (ticks, key) => SqliteTimeCursor.Encode(HistoryCursor, _database.StoreId, binding, ticks, key), cancellationToken);
    }

    private const string HistoryCursor = "history2";

    /// <summary>
    /// 投影只拿 Preview；搜尋時多帶 SqlBytes 給讀取端比對，但不將全部 SQL 載入列表或應用程式快取。
    /// 必須沿時間索引串流而沒有暫存排序，搜尋預算才真的限制讀入的 BLOB；由 EXPLAIN 測試守住。
    /// </summary>
    internal static string HistoryPageSql(IReadOnlyCollection<string> conditions, bool includeSql) =>
        @"SELECT h.EntryKey,h.SessionId,h.RevisionId,h.ContentId,h.CreatedAt,
h.Kind,d.DisplayName,c.Preview,h.Server,h.DatabaseName,h.ExecutionCount,h.FirstExecutedAt" + (includeSql ? ",c.SqlBytes" : "") + @"
FROM History h JOIN Sessions s ON s.SessionId=h.SessionId JOIN Documents d ON d.DocumentId=s.DocumentId
JOIN Contents c ON c.ContentId=h.ContentId" + SqliteConnectionFilter.Where(conditions) +
        " ORDER BY h.CreatedAt DESC,h.EntryKey DESC LIMIT $limit;";

    public string[] ReadConnectionFacets(SqlConnectionFacetRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Connect();
        var column = request.Databases ? "h.DatabaseName" : "h.Server";
        var source = request.IsFavorites ? "Favorites h" : "History h";
        var time = request.IsFavorites ? "h.UpdatedAt" : "h.CreatedAt";
        var order = request.Sort switch
        {
            SqlConnectionFacetSort.Oldest => "MIN(" + time + ") ASC, Name ASC",
            SqlConnectionFacetSort.Alphabetical => "Name COLLATE NOCASE ASC, Name ASC",
            SqlConnectionFacetSort.ReverseAlphabetical => "Name COLLATE NOCASE DESC, Name DESC",
            _ => "MAX(" + time + ") DESC, Name ASC"
        };
        // 資料庫名單只在指名了伺服器時才縮範圍；讀伺服器名單時這幾個條件一律不加。
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>
        {
            ("$limit", SqlConnectionFacetRequest.PageSize + 1), ("$offset", request.Offset)
        };
        if (request.Databases)
        {
            SqliteConnectionFilter.Append(conditions, parameters, "h", request.Servers, Array.Empty<string>());
        }

        // 識別字與排序僅來自上述封閉集合；所有使用者值仍以參數傳入。
        using var command = Command(connection, null, "SELECT " + column + " AS Name FROM " + source +
            " WHERE " + column + " IS NOT NULL AND " + column + " <> ''" +
            (conditions.Count == 0 ? "" : " AND " + string.Join(" AND ", conditions)) +
            " GROUP BY " + column + " ORDER BY " + order + " LIMIT $limit OFFSET $offset;",
            parameters.ToArray());
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
        using (var command = Command(connection, transaction, @"SELECT s.DocumentId,s.ClosedAt,s.Version,s.LastSequence,
s.LatestRevisionId,s.LatestExecutionRevisionId,r.ContentId
FROM Sessions s LEFT JOIN Recovery r ON r.SessionId=s.SessionId WHERE s.SessionId=$id;", ("$id", Id(sessionId))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            session = new SqlSession(sessionId, Guid.ParseExact(reader.GetString(0), "N"),
                reader.IsDBNull(1) ? null : Time(reader.GetInt64(1)));
            version = reader.GetInt64(2);
            sequence = reader.GetInt64(3);
            head = GuidOrNull(reader, 4);
            execution = GuidOrNull(reader, 5);
            recoveryContentId = StringOrNull(reader, 6);
        }
        return new SqlSessionHead(session, version, sequence,
            head.HasValue ? ReadRevision(connection, transaction, head.Value) : null,
            execution.HasValue ? ReadRevision(connection, transaction, execution.Value) : null,
            recoveryContentId);
    }

    private static SqlRevision? ReadRevision(SqliteConnection connection, SqliteTransaction transaction, Guid revisionId)
    {
        using var command = Command(connection, transaction, @"SELECT ParentRevisionId,ContentId,SessionId,CreatedAt,
Reason,IsExecutionSelection FROM Revisions WHERE RevisionId=$id;", ("$id", Id(revisionId)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new SqlRevision(revisionId, GuidOrNull(reader, 0), reader.GetString(1),
            GuidOrNull(reader, 2), Time(reader.GetInt64(3)), (SqlRevisionReason)reader.GetInt32(4), reader.GetBoolean(5)) : null;
    }

    private static void WriteHistory(SqliteConnection connection, SqliteTransaction transaction, string key, Guid sessionId,
        Guid? revisionId, string contentId, DateTimeOffset time, SqlHistoryFilter kind, SqlConnectionLabel? label)
    {
        Execute(connection, transaction, @"INSERT INTO History
(EntryKey,SessionId,RevisionId,ContentId,CreatedAt,Kind,Server,DatabaseName,ExecutionCount,FirstExecutedAt)
VALUES($key,$session,$revision,$content,$time,$kind,$server,$database,1,$first)
ON CONFLICT(EntryKey) DO UPDATE SET ContentId=excluded.ContentId, CreatedAt=excluded.CreatedAt,
Server=excluded.Server, DatabaseName=excluded.DatabaseName;",
            ("$key", key), ("$session", Id(sessionId)), ("$revision", Id(revisionId)), ("$content", contentId),
            ("$time", Ticks(time)), ("$kind", (int)kind), ("$server", label?.Server), ("$database", label?.Database),
            ("$first", kind == SqlHistoryFilter.Executions ? Ticks(time) : null));
    }

    /// <summary>
    /// 同一 Session 的上一筆執行列內容與連線都相同時併進那一列，回傳它的鍵；否則回傳 null 由呼叫端新增。
    /// </summary>
    /// <remarks>
    /// 只看 Session 記下的上一筆執行列，A→B→A 的第三次比對的是 B，時間軸順序不會被改寫；
    /// 列已被使用者或維護刪掉時條件不成立，下一次執行另起新列。連線以 IS 比對：兩邊都沒有連線也算相同，
    /// 字串走 BINARY 定序而區分大小寫，與連線篩選的語意一致。
    /// </remarks>
    private static string? MergeExecution(SqliteConnection connection, SqliteTransaction transaction, string? previousEntry,
        Guid sessionId, SqlExecution execution, string contentId, SqlConnectionLabel? label)
    {
        if (previousEntry == null) return null;
        Execute(connection, transaction, @"UPDATE History SET RevisionId=$revision, CreatedAt=MAX(CreatedAt,$time),
ExecutionCount=ExecutionCount+1
WHERE EntryKey=$key AND SessionId=$session AND Kind=1 AND ContentId=$content AND Server IS $server AND DatabaseName IS $database;",
            ("$key", previousEntry), ("$session", Id(sessionId)), ("$revision", Id(execution.RevisionId)),
            ("$time", Ticks(execution.ExecutedAt)), ("$content", contentId), ("$server", label?.Server), ("$database", label?.Database));
        return Changes(connection, transaction) > 0 ? previousEntry : null;
    }
}
