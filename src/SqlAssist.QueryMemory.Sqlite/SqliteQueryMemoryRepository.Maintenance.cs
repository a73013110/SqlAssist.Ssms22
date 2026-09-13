using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

public sealed partial class SqliteQueryMemoryRepository
{
    private const int ExecutionStage = 0, HistoryStage = 1, RevisionStage = 2, RecoveryStage = 3, ContentStage = 4;

    // Recovery 排在內容之前，同一輪釋出的未存檔草稿內容才回收得到；順序改動要一併改上面的常數。
    private static readonly (string Table, string Key)[] MaintenanceStages =
    {
        ("Executions", "ExecutionId"), ("History", "EntryKey"), ("Revisions", "RevisionId"),
        ("Recovery", "SessionId"), ("Contents", "ContentId"), ("Contexts", "ContextId"),
    };

    private const string UnprotectedRevision = @"
 AND NOT EXISTS(SELECT 1 FROM FavoriteQueries WHERE CurrentRevisionId=$revision)
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

    public Task<QueryMemoryCheckpointResult> CheckpointAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var connection = Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // TRUNCATE 要求所有讀取者都在最新快照；還有人讀舊快照就回報 busy，不中斷他們也不改資料。
        bool truncated;
        using (var command = Command(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);"))
        using (var reader = command.ExecuteReader())
            truncated = reader.Read() && reader.GetInt64(0) == 0;
        return new QueryMemoryCheckpointResult(truncated, ReadUsage(connection, null));
    }, cancellationToken);

    public Task<QueryMemoryUsage> CompactAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var connection = Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // VACUUM 不能在交易內；重建期間需要與資料庫等量的暫存空間，因此不排進背景維護。
        Execute(connection, null, "VACUUM;");
        // 重建結果先進 WAL，不接著 checkpoint 主檔案就不會縮小，使用者會看到「整理完卻沒變小」。
        Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
        return ReadUsage(connection, null);
    }, cancellationToken);

    public Task<QueryMemoryMaintenanceResult> MaintainAsync(QueryMemoryMaintenanceRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return Task.Run(() => Maintain(request, cancellationToken), cancellationToken);
    }

    private QueryMemoryMaintenanceResult Maintain(QueryMemoryMaintenanceRequest request, CancellationToken token)
    {
        var cursor = new SqliteMaintenanceCursor(_storeId, request);
        using var connection = Connect();
        // 候選、保護根重查與刪除共用 IMMEDIATE 交易，Favorite 更新不可能插進檢查與刪除之間。
        using var transaction = connection.BeginTransaction(deferred: false);
        token.ThrowIfCancellationRequested();
        var quotas = new MaintenanceQuotas(connection, transaction, request.Policy);
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
                deleted += DeleteCandidate(connection, transaction, cursor.Stage, key, request.Policy, quotas);
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
        string key, QueryMemoryMaintenancePolicy policy, MaintenanceQuotas quotas)
    {
        object? revision = null;
        long? autoQuota = null;
        long? favoriteQuota = null;
        if (stage == ExecutionStage)
        {
            using var read = Command(connection, transaction,
                "SELECT RevisionId FROM Executions WHERE ExecutionId=$id;", ("$id", key));
            revision = read.ExecuteScalar();
        }
        else if (stage == HistoryStage || stage == RevisionStage)
        {
            // History 自己沒有 Reason；Draft 的配額分類要看它引用的版本。
            using var read = Command(connection, transaction, stage == HistoryStage
                ? @"SELECT h.RevisionId,r.SessionId,r.Reason,r.IsExecutionSelection,r.FavoriteQueryId FROM History h
 LEFT JOIN Revisions r ON r.RevisionId=h.RevisionId WHERE h.EntryKey=$id;"
                : "SELECT RevisionId,SessionId,Reason,IsExecutionSelection,FavoriteQueryId FROM Revisions WHERE RevisionId=$id;", ("$id", key));
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                revision = StringOrNull(reader, 0);
                if (!reader.IsDBNull(1) && reader.GetInt64(2) == (long)QueryRevisionReason.AutoCheckpoint && reader.GetInt64(3) == 0)
                    autoQuota = quotas.AutoRevisionCutoff(reader.GetString(1));
                else if (!reader.IsDBNull(4) && reader.GetInt64(2) == (long)QueryRevisionReason.FavoriteQueryEdit)
                    favoriteQuota = quotas.FavoriteRevisionCutoff(reader.GetString(4));
            }
        }
        var parameters = new (string Name, object? Value)[]
        {
            ("$id", key), ("$revision", revision), ("$manual", (int)QueryRevisionReason.ManualSnapshot),
            ("$draft", policy.DraftBefore.HasValue ? (object)Ticks(policy.DraftBefore.Value) : null),
            ("$execution", quotas.ExecutionCutoff), ("$autoQuota", autoQuota),
            ("$beforeExecute", (int)QueryRevisionReason.BeforeExecute), ("$history", "e" + key),
            ("$favoriteEdit", (int)QueryRevisionReason.FavoriteQueryEdit), ("$favoriteQuota", favoriteQuota),
            ("$recoveryHistory", "s" + key),
            ("$recovery", policy.RecoveryBefore.HasValue ? (object)Ticks(policy.RecoveryBefore.Value) : null),
        };
        string sql;
        switch (stage)
        {
            case ExecutionStage:
                // 一個候選最多刪除兩列，沒有 CASCADE 或無界的子列刪除。
                var eligible = "SELECT 1 FROM Executions WHERE ExecutionId=$id AND ExecutedAt<$execution" + UnprotectedRevision;
                Execute(connection, transaction, "DELETE FROM History WHERE EntryKey=$history AND Pinned=0 AND EXISTS(" + eligible + ");", parameters);
                var historyDeleted = (int)ScalarLong(connection, transaction, "SELECT changes();");
                Execute(connection, transaction, "DELETE FROM Executions WHERE ExecutionId=$id AND ExecutedAt<$execution" +
                    UnprotectedRevision + " AND NOT EXISTS(SELECT 1 FROM History WHERE EntryKey=$history);", parameters);
                return historyDeleted + (int)ScalarLong(connection, transaction, "SELECT changes();");
            case HistoryStage:
                // Recovery 的投影沒有 RevisionId，另由 Recovery 階段連同 Recovery 一起處理。
                sql = @"DELETE FROM History WHERE EntryKey=$id AND Kind=2 AND RevisionId IS NOT NULL
 AND (CreatedAt<$draft OR CreatedAt<$autoQuota) AND Pinned=0" + UnprotectedRevision;
                break;
            case RevisionStage:
                // 收藏還在時，改 SQL 產生的版本不受草稿期限影響；只有每 Favorite 版本配額能回收它。
                sql = @"DELETE FROM Revisions WHERE RevisionId=$id
 AND (CreatedAt < CASE
   WHEN IsExecutionSelection=1 OR Reason=$beforeExecute THEN $execution
   WHEN Reason=$favoriteEdit AND EXISTS(SELECT 1 FROM FavoriteQueries WHERE FavoriteQueryId=Revisions.FavoriteQueryId) THEN $favoriteQuota
   ELSE $draft END
  OR CreatedAt<$autoQuota)" + UnprotectedRevision + @"
 AND NOT EXISTS(SELECT 1 FROM Sessions WHERE LatestRevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM Sessions WHERE LatestExecutionRevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ParentRevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM Executions WHERE RevisionId=$id)
 AND NOT EXISTS(SELECT 1 FROM History WHERE RevisionId=$id)";
                break;
            case RecoveryStage:
                // 租約還在就代表那個程序可能還開著這份未存檔草稿；過期只是宿主可以去確認，不是可以刪。
                var unowned = "SELECT 1 FROM Recovery WHERE SessionId=$id AND CapturedAt<$recovery" +
                    " AND NOT EXISTS(SELECT 1 FROM Sessions WHERE SessionId=$id AND LeaseId IS NOT NULL)";
                Execute(connection, transaction, "DELETE FROM History WHERE EntryKey=$recoveryHistory AND Pinned=0" +
                    " AND EXISTS(" + unowned + ");", parameters);
                var projectionDeleted = (int)ScalarLong(connection, transaction, "SELECT changes();");
                // Pinned 的投影留著就擋下 Recovery 本身，使用者釘住的草稿不會只剩一半。
                Execute(connection, transaction, "DELETE FROM Recovery WHERE SessionId=$id AND CapturedAt<$recovery" +
                    " AND NOT EXISTS(SELECT 1 FROM Sessions WHERE SessionId=$id AND LeaseId IS NOT NULL)" +
                    " AND NOT EXISTS(SELECT 1 FROM History WHERE EntryKey=$recoveryHistory);", parameters);
                return projectionDeleted + (int)ScalarLong(connection, transaction, "SELECT changes();");
            case ContentStage:
                sql = "DELETE FROM Contents WHERE ContentId=$id" + UnreferencedContent;
                break;
            default:
                sql = @"DELETE FROM Contexts WHERE ContextId=$id
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM Executions WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM Recovery WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM History WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM FavoriteQueries WHERE ContextId=$id)";
                break;
        }
        Execute(connection, transaction, sql + ";", parameters);
        return (int)ScalarLong(connection, transaction, "SELECT changes();");
    }

    /// <summary>界線只在批次開始解析一次；批次只刪除比界線更舊的列，最新 N 筆不會在批次內移動。</summary>
    private sealed class MaintenanceQuotas
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly QueryMemoryMaintenancePolicy _policy;
        private readonly Dictionary<string, long?> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long?> _favorite = new(StringComparer.Ordinal);

        public MaintenanceQuotas(SqliteConnection connection, SqliteTransaction transaction, QueryMemoryMaintenancePolicy policy)
        {
            _connection = connection;
            _transaction = transaction;
            _policy = policy;
            ExecutionCutoff = Later(policy.ExecutionBefore.HasValue ? Ticks(policy.ExecutionBefore.Value) : null,
                Boundary(policy.MaxExecutionEvents, "SELECT ExecutedAt FROM Executions ORDER BY ExecutedAt DESC"));
        }

        /// <summary>執行專用版本沿用同一界線，否則配額只會留下永遠無法回收的孤立版本。</summary>
        public long? ExecutionCutoff { get; }

        public long? AutoRevisionCutoff(string sessionId)
        {
            if (!_policy.MaxAutoRevisionsPerSession.HasValue) return null;
            if (_sessions.TryGetValue(sessionId, out var cached)) return cached;
            // 常數條件對應 IX_Revisions_SessionAuto，只掃描該 Session 的前 N 筆索引項。
            var cutoff = Boundary(_policy.MaxAutoRevisionsPerSession, "SELECT CreatedAt FROM Revisions WHERE SessionId=$session" +
                " AND Reason=" + (int)QueryRevisionReason.AutoCheckpoint + " AND IsExecutionSelection=0 ORDER BY CreatedAt DESC",
                ("$session", sessionId));
            _sessions.Add(sessionId, cutoff);
            return cutoff;
        }

        /// <summary>界線含目前版本；它本身另受 Favorite 引用保護，配額不會把收藏清成沒有 SQL。</summary>
        public long? FavoriteRevisionCutoff(string favoriteQueryId)
        {
            if (!_policy.MaxRevisionsPerFavoriteQuery.HasValue) return null;
            if (_favorite.TryGetValue(favoriteQueryId, out var cached)) return cached;
            // 明寫 IS NOT NULL，部分索引才會命中，界線只掃描該收藏的前 N 筆索引項。
            var cutoff = Boundary(_policy.MaxRevisionsPerFavoriteQuery,
                "SELECT CreatedAt FROM Revisions WHERE FavoriteQueryId=$favorite AND FavoriteQueryId IS NOT NULL ORDER BY CreatedAt DESC",
                ("$favorite", favoriteQueryId));
            _favorite.Add(favoriteQueryId, cutoff);
            return cutoff;
        }

        private long? Boundary(int? quota, string sql, params (string Name, object? Value)[] parameters)
        {
            if (!quota.HasValue) return null;
            // 配額 0 沒有第 0 新的列可當界線；全部候選都超額，保護根仍由刪除條件擋下。
            if (quota.Value == 0) return long.MaxValue;
            using var command = Command(_connection, _transaction, sql + " LIMIT 1 OFFSET " +
                (quota.Value - 1).ToString(CultureInfo.InvariantCulture) + ";", parameters);
            // 界線取第 N 新的時間且只刪嚴格更舊的列，同時間的列一併保留，實際筆數可能略多於配額。
            var value = command.ExecuteScalar();
            return value == null || value is DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static long? Later(long? left, long? right) =>
            left.HasValue && right.HasValue ? Math.Max(left.Value, right.Value) : left ?? right;
    }
}
