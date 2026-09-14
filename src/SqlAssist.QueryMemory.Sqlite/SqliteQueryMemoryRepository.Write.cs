using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

internal sealed partial class SqliteQueryMemoryRepository
{
    /// <remarks>提交前最後一次檢查取消；已提交就回傳結果，不因之後的取消改稱失敗。</remarks>
    public QueryMemoryCommitResult Commit(QueryMemoryWrite write, string? leaseId, CancellationToken cancellationToken)
    {
        if (write == null) throw new ArgumentNullException(nameof(write));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Connect();
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
                return QueryMemoryCommitResult.AlreadyCommitted;
            }
        }
        var previous = ReadSession(connection, transaction, write.State.Session.SessionId);
        if (previous?.Version != write.ExpectedVersion) return QueryMemoryCommitResult.Conflict;
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
            if (!revision.IsExecutionSelection && revision.Reason != QueryRevisionReason.BeforeExecute)
                WriteHistory(connection, transaction, "r" + Id(revision.RevisionId), revision.SessionId, revision.RevisionId,
                    revision.ContentId, revision.CreatedAt, QueryHistoryKind.Drafts, revision.Connection, contextId);
        }
        // 明列資料行並標上本程序的租約：有租約就代表還可能在編輯，維護不得回收這個 Session 的 Recovery。
        // 租約在交易內重查：另一個程序可能在心跳之前已回收它，外鍵失敗會讓整個 writer 停擺。
        // 回收後寫成無租約，與 ReleaseLeasesAsync 對既有 Session 的處理一致，下一次心跳重開後再標上。
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
                execution.RevisionId, revision.ContentId, execution.ExecutedAt, QueryHistoryKind.Executed, execution.Connection, contextId);
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
                recovery.CapturedAt, QueryHistoryKind.Drafts, recovery.Connection, contextId);
        }
        Execute(connection, transaction, "INSERT INTO Captures VALUES($id,$session,$sequence);",
            ("$id", Id(write.CaptureId)), ("$session", Id(state.Session.SessionId)), ("$sequence", state.LastSequence));
        if (oldRecoveryContent != null && (write.DeleteRecovery ||
            (write.Recovery != null && write.Recovery.ContentId != oldRecoveryContent)))
        {
            // 只檢查這次被替換的內容，不做全庫 GC；保留任何版本與其他 Session 的引用。
            Execute(connection, transaction, "DELETE FROM Contents WHERE ContentId=$id" + UnreferencedContent + ";", ("$id", oldRecoveryContent));
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return QueryMemoryCommitResult.Committed;
    }

    private static void WriteContent(SqliteConnection connection, SqliteTransaction transaction, QueryContent content)
    {
        var bytes = SqliteText.Encode(content.SqlText);
        using var insert = Command(connection, transaction, @"INSERT INTO Contents VALUES($id,$hash,$sql,$length,$preview)
ON CONFLICT(ContentId) DO NOTHING;", ("$id", content.ContentId), ("$hash", content.ContentHash), ("$sql", bytes),
            ("$length", content.Length), ("$preview", SqliteText.Preview(content.SqlText)));
        if (insert.ExecuteNonQuery() != 0) return;
        using var existing = Command(connection, transaction, "SELECT ContentHash, Length, SqlBytes FROM Contents WHERE ContentId=$id;", ("$id", content.ContentId));
        using var reader = existing.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != content.ContentHash || reader.GetInt64(1) != content.Length ||
            !((byte[])reader.GetValue(2)).SequenceEqual(bytes))
            throw new InvalidDataException("SQL 內容位址碰撞或資料已損壞；不會覆寫舊內容。");
    }

    private static string? WriteContext(SqliteConnection connection, SqliteTransaction transaction, QueryConnectionContext? context)
    {
        if (context == null) return null;
        var key = QueryContent.Create(Field(context.Server) + Field(context.Database)).ContentHash;
        Execute(connection, transaction, "INSERT INTO Contexts VALUES($id,$server,$database) ON CONFLICT(ContextId) DO NOTHING;",
            ("$id", key), ("$server", context.Server), ("$database", context.Database));
        using var command = Command(connection, transaction, "SELECT Server, DatabaseName FROM Contexts WHERE ContextId=$id;", ("$id", key));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || ReadContext(reader, 0) != context) throw new InvalidDataException("連線識別碼碰撞。");
        return key;
    }

    private static string Field(string? value) => SqliteFilterKey.Field(value);

    private static void WriteHistory(SqliteConnection connection, SqliteTransaction transaction, string key, Guid sessionId,
        Guid? revisionId, string contentId, DateTimeOffset time, QueryHistoryKind kind, QueryConnectionContext? context, string? contextId)
    {
        Execute(connection, transaction, @"INSERT INTO History(EntryKey,SessionId,RevisionId,ContentId,CreatedAt,Kind,ContextId,Server,DatabaseName)
VALUES($key,$session,$revision,$content,$time,$kind,$context,$server,$database)
ON CONFLICT(EntryKey) DO UPDATE SET ContentId=excluded.ContentId, CreatedAt=excluded.CreatedAt,
ContextId=excluded.ContextId, Server=excluded.Server, DatabaseName=excluded.DatabaseName;",
            ("$key", key), ("$session", Id(sessionId)), ("$revision", Id(revisionId)), ("$content", contentId),
            ("$time", Ticks(time)), ("$kind", (int)kind), ("$context", contextId), ("$server", context?.Server), ("$database", context?.Database));
    }
}
