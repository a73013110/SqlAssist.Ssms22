using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>
/// 一個 IMMEDIATE 交易內的工作量與引用回收。界線只在批次內解析一次並快取；
/// 批次只刪比界線更舊的列，最新 N 筆不會在批次內移動。
/// </summary>
internal sealed class SqliteMaintenanceBatch
{
    private const string UnprotectedRevision = SqliteContentRows.UnprotectedRevision;

    private const int ExecutionDelete = 0, HistoryDelete = 1, RevisionDelete = 2, RecoveryDelete = 3, ContentDelete = 4;

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqlRetentionPolicy _policy;
    private readonly CancellationToken _token;
    private readonly long? _draft;
    private readonly Dictionary<string, long?> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> _favorites = new(StringComparer.Ordinal);

    // 本批刪除列所引用的內容；批次結束前逐一重查引用，不為了孤立資料掃全表。
    private readonly HashSet<string> _releasedContents = new(StringComparer.Ordinal);

    public SqliteMaintenanceBatch(SqliteConnection connection, SqliteTransaction transaction,
        SqlRetentionPolicy policy, CancellationToken token)
    {
        _connection = connection;
        _transaction = transaction;
        _policy = policy;
        _token = token;
        _draft = policy.DraftBefore.HasValue ? Ticks(policy.DraftBefore.Value) : null;
        ExecutionCutoff = Later(policy.ExecutionBefore.HasValue ? Ticks(policy.ExecutionBefore.Value) : null,
            Boundary(policy.MaxExecutionEvents, "SELECT ExecutedAt FROM Executions ORDER BY ExecutedAt DESC"));
    }

    /// <summary>分組探測也算一個候選單位，批次工作量才不會隨 Session 或收藏數量無界成長。</summary>
    public int Examined { get; private set; }

    public int Deleted { get; private set; }

    /// <summary>執行專用版本沿用同一界線，否則配額只會留下永遠無法回收的孤立版本。</summary>
    private long? ExecutionCutoff { get; }

    /// <returns>true 表示工作量用完而這個階段可能還有候選；false 表示階段已巡完。</returns>
    public bool Advance(SqliteMaintenanceStage stage, SqliteMaintenanceCursor cursor, int remaining) => stage switch
    {
        SqliteMaintenanceStage.Executions =>
            ByTime(SqliteMaintenanceStages.Executions, ExecutionCutoff, cursor, remaining, ExecutionDelete),
        SqliteMaintenanceStage.DraftHistory => _draft.HasValue || _policy.MaxAutoRevisionsPerSession.HasValue
            ? ByGroup(SqliteMaintenanceStages.DraftHistoryGroup, SqliteMaintenanceStages.DraftHistory,
                session => Later(_draft, AutoRevisionCutoff(session)), cursor, remaining, HistoryDelete)
            : false,
        SqliteMaintenanceStage.SelectionRevisions =>
            ByTime(SqliteMaintenanceStages.SelectionRevisions, ExecutionCutoff, cursor, remaining, RevisionDelete),
        // 收藏還在時只受每 Favorite 配額處理；移除收藏後才改依草稿期限，與刪除條件的 CASE 一致。
        SqliteMaintenanceStage.FavoriteRevisions => _draft.HasValue || _policy.MaxRevisionsPerFavorite.HasValue
            ? ByGroup(SqliteMaintenanceStages.FavoriteRevisionGroup, SqliteMaintenanceStages.FavoriteRevisions,
                favorite => FavoriteExists(favorite) ? FavoriteRevisionCutoff(favorite) : _draft, cursor, remaining,
                RevisionDelete)
            : false,
        SqliteMaintenanceStage.Recovery => ByTime(SqliteMaintenanceStages.Recovery,
            _policy.RecoveryBefore.HasValue ? Ticks(_policy.RecoveryBefore.Value) : null, cursor, remaining, RecoveryDelete),
        SqliteMaintenanceStage.Revisions => ByKey(stage, cursor, remaining, RevisionDelete),
        SqliteMaintenanceStage.Contents => ByKey(stage, cursor, remaining, ContentDelete),
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    /// <summary>本批結束前重查被釋出的內容；每個被刪的列最多帶出一個。</summary>
    public void CollectReleasedReferences()
    {
        foreach (var content in _releasedContents)
        {
            _token.ThrowIfCancellationRequested();
            Deleted += Delete("DELETE FROM Contents WHERE ContentId=$id" + SqliteContentRows.Unreferenced + ";", ("$id", content));
        }
    }

    private bool ByTime(string sql, long? cutoff, SqliteMaintenanceCursor cursor, int remaining, int kind)
    {
        if (!cutoff.HasValue) return false;
        // 先限定候選再查引用；不能把 NOT EXISTS 放在 LIMIT 前而掃過全庫受保護列。
        var candidates = Candidates(sql, remaining, ("$cutoff", cutoff), ("$time", cursor.Time ?? long.MinValue),
            ("$key", cursor.Key));
        foreach (var (key, time) in candidates)
        {
            Visit(kind, key);
            cursor.Time = time;
            cursor.Key = key;
        }
        return candidates.Count == remaining;
    }

    private bool ByGroup(string groupSql, string sql, Func<string, long?> groupCutoff, SqliteMaintenanceCursor cursor,
        int remaining, int kind)
    {
        while (remaining > 0)
        {
            _token.ThrowIfCancellationRequested();
            if (cursor.Time == null)
            {
                string? group;
                using (var probe = Command(_connection, _transaction, groupSql, ("$group", cursor.Group)))
                    group = probe.ExecuteScalar() as string;
                if (group == null) return false;
                cursor.Group = group;
                cursor.Time = long.MinValue;
                cursor.Key = "";
                Examined++;
                remaining--;
                continue;
            }
            var cutoff = groupCutoff(cursor.Group);
            var candidates = cutoff.HasValue
                ? Candidates(sql, remaining, ("$group", cursor.Group), ("$cutoff", cutoff), ("$time", cursor.Time),
                    ("$key", cursor.Key))
                : new List<(string Key, long Time)>();
            foreach (var (key, time) in candidates)
            {
                Visit(kind, key);
                cursor.Time = time;
                cursor.Key = key;
            }
            if (candidates.Count == remaining) return true;
            remaining -= candidates.Count;
            cursor.Time = null;
            cursor.Key = "";
        }
        return true;
    }

    private bool ByKey(SqliteMaintenanceStage stage, SqliteMaintenanceCursor cursor, int remaining, int kind)
    {
        var keys = new List<string>();
        using (var command = Command(_connection, _transaction, SqliteMaintenanceStages.ByKey(stage),
            ("$key", cursor.Key), ("$limit", remaining)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) { _token.ThrowIfCancellationRequested(); keys.Add(reader.GetString(0)); }
        foreach (var key in keys)
        {
            Visit(kind, key);
            cursor.Key = key;
        }
        return keys.Count == remaining;
    }

    private List<(string Key, long Time)> Candidates(string sql, int limit, params (string Name, object? Value)[] parameters)
    {
        var candidates = new List<(string Key, long Time)>();
        using var command = Command(_connection, _transaction, sql, parameters);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _token.ThrowIfCancellationRequested();
            candidates.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        return candidates;
    }

    private void Visit(int kind, string key)
    {
        _token.ThrowIfCancellationRequested();
        Deleted += kind switch
        {
            ExecutionDelete => DeleteExecution(key),
            HistoryDelete => DeleteDraftHistory(key),
            RevisionDelete => DeleteRevision(key),
            RecoveryDelete => DeleteRecovery(key),
            ContentDelete => Delete("DELETE FROM Contents WHERE ContentId=$id" + SqliteContentRows.Unreferenced + ";", ("$id", key)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Examined++;
    }

    /// <remarks>
    /// 配額與期限以執行事件計，合併的 History 列跟著它剩下的執行走：刪到最後一筆才刪投影，
    /// 否則依剩餘執行重算次數、首次與最後時間及引用版本。剩餘執行沿 <c>IX_Executions_Entry</c> 各取一端，
    /// 不隨合併次數增加讀取量；一個候選仍最多刪除本體與投影兩列。
    /// </remarks>
    private int DeleteExecution(string key)
    {
        string revision, entry;
        using (var read = Command(_connection, _transaction, "SELECT RevisionId,EntryKey FROM Executions WHERE ExecutionId=$id;",
            ("$id", key)))
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) return 0;
            revision = reader.GetString(0);
            entry = reader.GetString(1);
        }
        var parameters = new (string Name, object? Value)[]
        {
            ("$id", key), ("$revision", revision), ("$execution", ExecutionCutoff), ("$entry", entry),
        };
        var executions = Delete("DELETE FROM Executions WHERE ExecutionId=$id AND ExecutedAt<$execution" + UnprotectedRevision + ";",
            parameters);
        if (executions == 0) return 0;
        string? latestRevision = null;
        long latest = 0;
        using (var read = Command(_connection, _transaction, SqliteMaintenanceStages.LatestEntryExecution, parameters))
        using (var reader = read.ExecuteReader())
        {
            if (reader.Read())
            {
                latestRevision = reader.GetString(0);
                latest = reader.GetInt64(1);
            }
        }
        if (latestRevision == null)
            return executions + DeleteReleasing("History", "EntryKey=$entry", "DELETE FROM History WHERE EntryKey=$entry;", parameters);
        Execute(_connection, _transaction, @"UPDATE History SET ExecutionCount=ExecutionCount-1, CreatedAt=$latest,
 RevisionId=$latestRevision, FirstExecutedAt=(" + SqliteMaintenanceStages.FirstEntryExecution + @")
 WHERE EntryKey=$entry;", ("$entry", entry), ("$latest", latest), ("$latestRevision", latestRevision));
        return executions;
    }

    private int DeleteDraftHistory(string key)
    {
        object? revision = null;
        long? autoQuota = null;
        // History 自己沒有 Reason；Draft 的配額分類要看它引用的版本。
        using (var read = Command(_connection, _transaction, @"SELECT h.RevisionId,r.SessionId,r.Reason,r.IsExecutionSelection FROM History h
 LEFT JOIN Revisions r ON r.RevisionId=h.RevisionId WHERE h.EntryKey=$id;", ("$id", key)))
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) return 0;
            revision = StringOrNull(reader, 0);
            if (!reader.IsDBNull(1) && reader.GetInt64(2) == (long)SqlRevisionReason.AutoCheckpoint && reader.GetInt64(3) == 0)
                autoQuota = AutoRevisionCutoff(reader.GetString(1));
        }
        // Recovery 的投影沒有 RevisionId，另由 Recovery 階段連同 Recovery 一起處理。
        return DeleteReleasing("History", "EntryKey=$id", @"DELETE FROM History WHERE EntryKey=$id AND Kind=2 AND RevisionId IS NOT NULL
 AND (CreatedAt<$draft OR CreatedAt<$autoQuota)" + UnprotectedRevision + ";",
            ("$id", key), ("$revision", revision), ("$draft", _draft), ("$autoQuota", autoQuota));
    }

    private int DeleteRevision(string key)
    {
        long? autoQuota = null;
        long? favoriteQuota = null;
        using (var read = Command(_connection, _transaction,
            "SELECT SessionId,Reason,IsExecutionSelection,FavoriteId FROM Revisions WHERE RevisionId=$id;", ("$id", key)))
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) return 0;
            if (reader.GetInt64(1) == (long)SqlRevisionReason.AutoCheckpoint && reader.GetInt64(2) == 0)
                autoQuota = AutoRevisionCutoff(reader.GetString(0));
            else if (!reader.IsDBNull(3) && reader.GetInt64(1) == (long)SqlRevisionReason.Favorite)
                favoriteQuota = FavoriteRevisionCutoff(reader.GetString(3));
        }
        // 收藏還在時，它自己產生的版本不受草稿期限影響；只有每 Favorite 版本配額能回收它。
        return DeleteReleasing("Revisions", "RevisionId=$id", @"DELETE FROM Revisions WHERE RevisionId=$id
 AND (CreatedAt < CASE
   WHEN IsExecutionSelection=1 OR Reason=$beforeExecute THEN $execution
   WHEN Reason=$favoriteReason AND EXISTS(SELECT 1 FROM Favorites WHERE FavoriteId=Revisions.FavoriteId) THEN $favoriteQuota
   ELSE $draft END
  OR CreatedAt<$autoQuota)" + SqliteContentRows.UnreferencedRevision + ";",
            ("$id", key), ("$revision", key), ("$draft", _draft), ("$execution", ExecutionCutoff), ("$autoQuota", autoQuota),
            ("$beforeExecute", (int)SqlRevisionReason.BeforeExecute),
            ("$favoriteReason", (int)SqlRevisionReason.Favorite), ("$favoriteQuota", favoriteQuota));
    }

    private int DeleteRecovery(string key)
    {
        var parameters = new (string Name, object? Value)[]
        {
            ("$id", key), ("$recoveryHistory", "s" + key),
            ("$recovery", _policy.RecoveryBefore.HasValue ? (object)Ticks(_policy.RecoveryBefore.Value) : null),
        };
        // 租約還在就代表那個程序可能還開著這份未存檔草稿；過期只是宿主可以去確認，不是可以刪。
        var unowned = "SELECT 1 FROM Recovery WHERE SessionId=$id AND CapturedAt<$recovery" +
            " AND NOT EXISTS(SELECT 1 FROM Sessions WHERE SessionId=$id AND LeaseId IS NOT NULL)";
        var projection = DeleteReleasing("History", "EntryKey=$recoveryHistory",
            "DELETE FROM History WHERE EntryKey=$recoveryHistory AND EXISTS(" + unowned + ");", parameters);
        return projection + DeleteReleasing("Recovery", "SessionId=$id",
            "DELETE FROM Recovery WHERE SessionId=$id AND CapturedAt<$recovery" +
            " AND NOT EXISTS(SELECT 1 FROM Sessions WHERE SessionId=$id AND LeaseId IS NOT NULL)" +
            " AND NOT EXISTS(SELECT 1 FROM History WHERE EntryKey=$recoveryHistory);", parameters);
    }

    /// <summary>先記下這一列引用的內容再刪；真的刪掉才交給本批的引用回收。</summary>
    private int DeleteReleasing(string table, string row, string sql, params (string Name, object? Value)[] parameters)
    {
        string? content;
        using (var read = Command(_connection, _transaction, "SELECT ContentId FROM " + table + " WHERE " + row + ";", parameters))
            content = read.ExecuteScalar() as string;
        if (content == null) return 0;
        var deleted = Delete(sql, parameters);
        if (deleted > 0) _releasedContents.Add(content);
        return deleted;
    }

    private int Delete(string sql, params (string Name, object? Value)[] parameters)
    {
        Execute(_connection, _transaction, sql, parameters);
        return Changes(_connection, _transaction);
    }

    private long? AutoRevisionCutoff(string sessionId)
    {
        if (!_policy.MaxAutoRevisionsPerSession.HasValue) return null;
        if (_sessions.TryGetValue(sessionId, out var cached)) return cached;
        // 常數條件對應 IX_Revisions_SessionAuto，只掃描該 Session 的前 N 筆索引項。
        var cutoff = Boundary(_policy.MaxAutoRevisionsPerSession, "SELECT CreatedAt FROM Revisions WHERE SessionId=$session" +
            " AND Reason=" + (int)SqlRevisionReason.AutoCheckpoint + " AND IsExecutionSelection=0 ORDER BY CreatedAt DESC",
            ("$session", sessionId));
        _sessions.Add(sessionId, cutoff);
        return cutoff;
    }

    /// <summary>界線含目前版本；它本身另受 Favorite 引用保護，配額不會把收藏清成沒有 SQL。</summary>
    private long? FavoriteRevisionCutoff(string favoriteId)
    {
        if (!_policy.MaxRevisionsPerFavorite.HasValue) return null;
        if (_favorites.TryGetValue(favoriteId, out var cached)) return cached;
        // 明寫 IS NOT NULL，部分索引才會命中，界線只掃描該收藏的前 N 筆索引項。
        var cutoff = Boundary(_policy.MaxRevisionsPerFavorite,
            "SELECT CreatedAt FROM Revisions WHERE FavoriteId=$favorite AND FavoriteId IS NOT NULL ORDER BY CreatedAt DESC",
            ("$favorite", favoriteId));
        _favorites.Add(favoriteId, cutoff);
        return cutoff;
    }

    private bool FavoriteExists(string favoriteId)
    {
        using var command = Command(_connection, _transaction, "SELECT 1 FROM Favorites WHERE FavoriteId=$id;",
            ("$id", favoriteId));
        return command.ExecuteScalar() != null;
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
