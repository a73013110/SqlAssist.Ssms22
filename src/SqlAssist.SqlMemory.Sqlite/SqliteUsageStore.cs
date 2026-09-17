using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>用量頁的讀取與使用者主動的清理、備份；排程維護在 <see cref="SqliteMaintenanceStore"/>。</summary>
/// <remarks>
/// 計數會掃描索引，成本隨資料量線性成長；只在使用者開啟用量頁時讀，每一句都挑能以索引涵蓋的形狀，
/// 不讀 SQL BLOB。清理沿用逐筆刪除 History 的交易步驟，保護根與引用清單不另寫一份。
/// </remarks>
internal sealed class SqliteUsageStore
{
    /// <summary>伺服器分布只列前幾名；其餘由總數推得，畫面不需要長尾。</summary>
    public const int ServerShareLimit = 5;

    private const string CleanupCursor = "cleanup1";

    /// <summary>沿 IX_History_ServerTime 的前綴分組不需要暫存 B-tree；排序只作用在分組後的少數列。</summary>
    internal const string ServerSharesSql = @"SELECT Server,count(*) n FROM History INDEXED BY IX_History_ServerTime
 WHERE Server IS NOT NULL GROUP BY Server ORDER BY n DESC,Server LIMIT $limit;";

    private readonly SqliteDatabase _database;

    public SqliteUsageStore(SqliteDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public SqlMemoryUsageReport ReadReport(CancellationToken cancellationToken)
    {
        using var connection = _database.Connect();
        // 同一個讀取快照內計數，畫面上的各項數字彼此一致；WAL 下不擋寫入。
        using var transaction = connection.BeginTransaction(deferred: true);
        cancellationToken.ThrowIfCancellationRequested();
        long Count(string sql)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ScalarLong(connection, transaction, sql);
        }

        var usage = SqliteMaintenanceStore.ReadUsage(_database, connection, transaction);
        var free = Count("PRAGMA freelist_count;") * Count("PRAGMA page_size;");
        var counts = new SqlMemoryUsageCounts(
            Count("SELECT count(*) FROM History INDEXED BY IX_History_KindTime WHERE Kind=1;"),
            Count("SELECT count(*) FROM Executions;"),
            Count("SELECT count(*) FROM History INDEXED BY IX_History_KindTime WHERE Kind=2;"),
            Count("SELECT count(*) FROM Recovery;"),
            Count("SELECT count(*) FROM Sessions s INDEXED BY IX_Sessions_Lease WHERE s.LeaseId IS NOT NULL" +
                " AND EXISTS(SELECT 1 FROM Recovery r WHERE r.SessionId=s.SessionId);"),
            Count("SELECT count(*) FROM Sessions;"),
            Count("SELECT count(*) FROM Favorites;"),
            Count("SELECT count(*) FROM Revisions INDEXED BY IX_Revisions_Favorite WHERE FavoriteId IS NOT NULL;"),
            Count(@"SELECT coalesce(max(n),0) FROM (SELECT count(*) n FROM Revisions INDEXED BY IX_Revisions_Favorite
 WHERE FavoriteId IS NOT NULL AND FavoriteId IN (SELECT FavoriteId FROM Favorites) GROUP BY FavoriteId);"),
            Count("SELECT count(*) FROM Contents;"));

        var servers = new List<SqlMemoryUsageShare>();
        using (var command = Command(connection, transaction, ServerSharesSql, ("$limit", ServerShareLimit)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) servers.Add(new SqlMemoryUsageShare(reader.GetString(0), reader.GetInt64(1)));

        // min 與 max 分開查才各自走索引一端；合在同一句會退化成全索引掃描。
        var oldest = TimeOrNull(connection, transaction, "SELECT min(CreatedAt) FROM History;");
        var newest = TimeOrNull(connection, transaction, "SELECT max(CreatedAt) FROM History;");
        transaction.Commit();
        return new SqlMemoryUsageReport(usage, free, counts, servers.ToArray(), oldest, newest);
    }

    public SqlMemoryCleanupEstimate Estimate(SqlMemoryCleanupRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: true);
        cancellationToken.ThrowIfCancellationRequested();
        long executions = 0, drafts = 0, recovery = 0, favorites = 0;
        if (request.TouchesHistory)
        {
            var (conditions, parameters) = HistoryConditions(request);
            using var command = Command(connection, transaction, @"SELECT coalesce(sum(h.Kind=1),0),
 coalesce(sum(h.Kind=2 AND h.RevisionId IS NOT NULL),0), coalesce(sum(h.Kind=2 AND h.RevisionId IS NULL),0)
 FROM History h" + SqliteConnectionFilter.Where(conditions) + ";", parameters.ToArray());
            using var reader = command.ExecuteReader();
            reader.Read();
            executions = reader.GetInt64(0);
            drafts = reader.GetInt64(1);
            recovery = reader.GetInt64(2);
        }
        if (request.Includes(SqlMemoryCleanupTargets.FavoriteRevisions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // rank() 等於「比它新的版本數 + 1」；維護界線是第 N 新的時間且只刪嚴格更舊的列，
            // 兩者在同時間並列時一致，試算不會比實際刪除多算。目前版本是保護根，另外排除。
            favorites = ScalarLong(connection, transaction, @"SELECT count(*) FROM (
 SELECT r.RevisionId, rank() OVER (PARTITION BY r.FavoriteId ORDER BY r.CreatedAt DESC) n
 FROM Revisions r INDEXED BY IX_Revisions_Favorite
 WHERE r.FavoriteId IS NOT NULL AND r.FavoriteId IN (SELECT FavoriteId FROM Favorites)) x
 WHERE x.n>$keep AND NOT EXISTS(SELECT 1 FROM Favorites f WHERE f.CurrentRevisionId=x.RevisionId);",
                ("$keep", request.KeepFavoriteRevisions));
        }
        transaction.Commit();
        return new SqlMemoryCleanupEstimate(executions, drafts, recovery, favorites);
    }

    /// <remarks>
    /// 由新到舊沿時間索引續讀，形狀與 History 清單相同；先讀出候選再逐筆刪，不在讀取游標開著時改表。
    /// 仍有心跳租約的回復內容由候選條件排除，跳過的列靠游標前進，不會每批重讀。
    /// </remarks>
    public SqlMemoryCleanupBatch CleanupHistory(SqlMemoryCleanupRequest request, string? cursor, int limit,
        CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!request.TouchesHistory) return new SqlMemoryCleanupBatch(0, 0, 0, null);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = SqliteTimeCursor.Fingerprint(((int)request.Targets).ToString(CultureInfo.InvariantCulture),
            request.Before?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture), request.Server, request.Database);
        var position = SqliteTimeCursor.Decode(cursor, CleanupCursor, _database.StoreId, binding, SqliteHistoryRows.IsKey);
        var (conditions, parameters) = HistoryConditions(request);
        position?.AppendCondition(conditions, parameters, "h.CreatedAt", "h.EntryKey");
        parameters.Add(("$limit", limit));

        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        var candidates = new List<(string Key, string Session, long Ticks)>();
        using (var command = Command(connection, transaction, "SELECT h.EntryKey,h.SessionId,h.CreatedAt FROM History h" +
            SqliteConnectionFilter.Where(conditions) + " ORDER BY h.CreatedAt DESC,h.EntryKey DESC LIMIT $limit;", parameters.ToArray()))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));

        var contents = new HashSet<string>(StringComparer.Ordinal);
        int entries = 0, rows = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = SqliteHistoryRows.Delete(connection, transaction, candidate.Key, candidate.Session, contents, cancellationToken);
            if (deleted == 0) continue;
            entries++;
            rows += deleted;
        }
        rows += SqliteHistoryRows.CollectContents(connection, transaction, contents, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        // 讀滿一批才可能還有下一批；不足一批表示已經巡到最舊的一列。
        string? next = null;
        if (candidates.Count == limit)
        {
            var last = candidates[candidates.Count - 1];
            next = SqliteTimeCursor.Encode(CleanupCursor, _database.StoreId, binding, last.Ticks, last.Key);
        }
        return new SqlMemoryCleanupBatch(candidates.Count, entries, rows, next);
    }

    /// <remarks>
    /// <c>VACUUM INTO</c> 在一個讀取交易內寫出完整且壓縮過的副本；WAL 讓擷取照常提交，不必先排空 writer。
    /// 目的檔已存在時 SQLite 會拒絕，覆寫與否由呼叫端先決定，這裡不刪使用者的檔案。
    /// </remarks>
    public long Backup(string destinationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathRooted(destinationPath))
            throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidArgument, "備份必須指定絕對路徑。");
        var path = Path.GetFullPath(destinationPath);
        if (string.Equals(path, _database.FilePath, StringComparison.OrdinalIgnoreCase))
            throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidArgument, "備份不能覆寫目前的資料庫。");
        if (File.Exists(path))
            throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidArgument, "備份檔案已存在；請選擇新的檔名。");
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        Execute(connection, null, "VACUUM INTO $path;", ("$path", path));
        return new FileInfo(path).Length;
    }

    private static (List<string> Conditions, List<(string Name, object? Value)> Parameters) HistoryConditions(
        SqlMemoryCleanupRequest request)
    {
        var kinds = new List<string>();
        if (request.Includes(SqlMemoryCleanupTargets.Executions)) kinds.Add("h.Kind=1");
        if (request.Includes(SqlMemoryCleanupTargets.Drafts)) kinds.Add("(h.Kind=2 AND h.RevisionId IS NOT NULL)");
        // 租約還在代表那個程序可能還開著這份未存檔草稿；與維護回收 Recovery 的條件相同。
        if (request.Includes(SqlMemoryCleanupTargets.ClosedRecovery))
            kinds.Add("(h.Kind=2 AND h.RevisionId IS NULL AND NOT EXISTS(SELECT 1 FROM Sessions s" +
                " WHERE s.SessionId=h.SessionId AND s.LeaseId IS NOT NULL))");
        var conditions = new List<string> { "(" + string.Join(" OR ", kinds) + ")" };
        var parameters = new List<(string Name, object? Value)>();
        if (request.Before.HasValue)
        {
            conditions.Add("h.CreatedAt<$before");
            parameters.Add(("$before", Ticks(request.Before.Value)));
        }
        SqliteConnectionFilter.Append(conditions, parameters, "h", request.Server, request.Database);
        return (conditions, parameters);
    }

    private static DateTimeOffset? TimeOrNull(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar() is long ticks ? Time(ticks) : null;
    }
}
