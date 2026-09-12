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
    private static readonly (string Table, string Key)[] MaintenanceStages =
    {
        ("Executions", "ExecutionId"), ("History", "EntryKey"), ("Revisions", "RevisionId"),
        ("Contents", "ContentId"), ("Contexts", "ContextId"),
    };

    private const string UnprotectedRevision = @"
 AND NOT EXISTS(SELECT 1 FROM SavedQueries WHERE CurrentRevisionId=$revision)
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE RevisionId=$revision AND Reason=$manual)
 AND NOT EXISTS(SELECT 1 FROM History WHERE RevisionId=$revision AND Pinned=1)";

    private const string UnreferencedContent = @"
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ContentId=$id)
 AND NOT EXISTS(SELECT 1 FROM Recovery WHERE ContentId=$id)
 AND NOT EXISTS(SELECT 1 FROM History WHERE ContentId=$id)";

    public Task<QueryMemoryUsage> ReadUsageAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var connection = Connect();
        cancellationToken.ThrowIfCancellationRequested();
        return ReadUsage(connection, null);
    }, cancellationToken);

    private QueryMemoryUsage ReadUsage(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var bytes = ScalarLong(connection, transaction, "SELECT ContentBytes FROM StorageUsage WHERE Id=1;");
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        return new QueryMemoryUsage(bytes, FileLength(path), FileLength(path + "-wal"));
    }

    private static long FileLength(string path)
    {
        // WAL 可在另一個連線關閉時消失；檔案大小僅是觀測，不是 SQLite 交易快照。
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
    }

    public Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return Task.Run(() => Maintain(request, cancellationToken), cancellationToken);
    }

    private QueryMemoryMaintenanceResult Maintain(QueryMemoryMaintenanceRequest request, CancellationToken token)
    {
        var cursor = new SqliteMaintenanceCursor(_storeId, request);
        using var connection = Connect();
        // 候選、保護根重查與刪除共用 IMMEDIATE 交易，Saved 更新不可能插進檢查與刪除之間。
        using var transaction = connection.BeginTransaction(deferred: false);
        token.ThrowIfCancellationRequested();
        var examined = 0;
        var deleted = 0;
        while (cursor.Stage < MaintenanceStages.Length && examined < request.CandidateLimit)
        {
            var stage = MaintenanceStages[cursor.Stage];
            var remaining = request.CandidateLimit - examined;
            var keys = new List<string>();
            // 先限定候選再查引用；不能把 NOT EXISTS 放在 LIMIT 前而掃過全庫受保護列。
            using (var command = Command(connection, transaction, "SELECT " + stage.Key + " FROM " + stage.Table +
                " WHERE " + stage.Key + ">$after ORDER BY " + stage.Key + " LIMIT $limit;",
                ("$after", cursor.After), ("$limit", remaining)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { token.ThrowIfCancellationRequested(); keys.Add(reader.GetString(0)); }
            foreach (var key in keys)
            {
                token.ThrowIfCancellationRequested();
                deleted += DeleteCandidate(connection, transaction, cursor.Stage, key, request.Policy);
                cursor.After = key;
                examined++;
            }
            if (keys.Count < remaining) { cursor.Stage++; cursor.After = ""; }
        }
        cursor.MadeProgress |= deleted > 0;
        var completed = cursor.Stage == MaintenanceStages.Length;
        var usage = ReadUsage(connection, transaction);
        var capacity = !request.Policy.MaxContentBytes.HasValue || usage.ContentBytes <= request.Policy.MaxContentBytes.Value
            ? QueryMemoryCapacityStatus.WithinLimit
            : !completed || cursor.MadeProgress ? QueryMemoryCapacityStatus.MoreWorkRequired
            : QueryMemoryCapacityStatus.CannotReclaimWithinPolicy;
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return new QueryMemoryMaintenanceResult(examined, deleted, completed ? null : cursor.Encode(),
            completed && cursor.MadeProgress, usage, capacity);
    }

    private static int DeleteCandidate(SqliteConnection connection, SqliteTransaction transaction, int stage,
        string key, QueryMemoryMaintenancePolicy policy)
    {
        var source = MaintenanceStages[stage];
        object? revision = null;
        if (stage <= 2)
        {
            using var read = Command(connection, transaction,
                "SELECT RevisionId FROM " + source.Table + " WHERE " + source.Key + "=$id;", ("$id", key));
            revision = read.ExecuteScalar();
        }
        var parameters = new (string Name, object? Value)[]
        {
            ("$id", key), ("$revision", revision), ("$manual", (int)QueryRevisionReason.ManualSnapshot),
            ("$draft", policy.DraftBefore.HasValue ? (object)Ticks(policy.DraftBefore.Value) : null),
            ("$execution", policy.ExecutionBefore.HasValue ? (object)Ticks(policy.ExecutionBefore.Value) : null),
            ("$beforeExecute", (int)QueryRevisionReason.BeforeExecute), ("$history", "e" + key),
        };
        string sql;
        switch (stage)
        {
            case 0:
                // 一個候選最多刪除兩列，沒有 CASCADE 或無界的子列刪除。
                var eligible = "SELECT 1 FROM Executions WHERE ExecutionId=$id AND ExecutedAt<$execution" + UnprotectedRevision;
                Execute(connection, transaction, "DELETE FROM History WHERE EntryKey=$history AND Pinned=0 AND EXISTS(" + eligible + ");", parameters);
                var historyDeleted = (int)ScalarLong(connection, transaction, "SELECT changes();");
                Execute(connection, transaction, "DELETE FROM Executions WHERE ExecutionId=$id AND ExecutedAt<$execution" +
                    UnprotectedRevision + " AND NOT EXISTS(SELECT 1 FROM History WHERE EntryKey=$history);", parameters);
                return historyDeleted + (int)ScalarLong(connection, transaction, "SELECT changes();");
            case 1:
                // Recovery 不憑年齡推定已失效；必須等宿主生命週期有可靠訊號才能解除保護。
                sql = @"DELETE FROM History WHERE EntryKey=$id AND Kind=2 AND RevisionId IS NOT NULL
 AND CreatedAt<$draft AND Pinned=0" + UnprotectedRevision;
                break;
            case 2:
                sql = @"DELETE FROM Revisions WHERE RevisionId=$id
 AND CreatedAt < CASE WHEN IsExecutionSelection=1 OR Reason=$beforeExecute THEN $execution ELSE $draft END" + UnprotectedRevision + @"
 AND NOT EXISTS(SELECT 1 FROM Sessions WHERE LatestRevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM Sessions WHERE LatestExecutionRevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ParentRevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM Executions WHERE RevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM History WHERE RevisionId=$id)";
                break;
            case 3:
                sql = "DELETE FROM Contents WHERE ContentId=$id" + UnreferencedContent;
                break;
            default:
                sql = @"DELETE FROM Contexts WHERE ContextId=$id
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM Executions WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM Recovery WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM History WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM SavedQueries WHERE ContextId=$id)";
                break;
        }
        Execute(connection, transaction, sql + ";", parameters);
        return (int)ScalarLong(connection, transaction, "SELECT changes();");
    }
}
