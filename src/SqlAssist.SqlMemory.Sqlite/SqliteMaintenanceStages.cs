using System;
using System.Collections.Generic;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Sqlite;

internal enum SqliteMaintenanceStage
{
    Executions, DraftHistory, SelectionRevisions, FavoriteRevisions, Recovery, Revisions, Contents,
}

/// <summary>
/// 各階段只挑可能過期或超額的列：候選查詢先 LIMIT，引用保護在刪除時於同一交易重查。
/// </summary>
/// <remarks>
/// 排序固定：Execution 先釋出執行專用版本，版本階段才回收得到；Recovery 排在最後，
/// 釋出的內容由本批的引用回收接手。擷取產生的文件版本不是候選——引擎一律把新版本接在
/// 前一個 head 之後，舊文件版本不是 head 就有子版本，時間經過不會讓它變得可刪；
/// 只有完整掃描逐鍵重查這類版本與所有內容，承接任何索引路徑沒預料到的孤立資料。
/// </remarks>
internal static class SqliteMaintenanceStages
{
    public static readonly SqliteMaintenanceStage[] Indexed =
    {
        SqliteMaintenanceStage.Executions, SqliteMaintenanceStage.DraftHistory, SqliteMaintenanceStage.SelectionRevisions,
        SqliteMaintenanceStage.FavoriteRevisions, SqliteMaintenanceStage.Recovery,
    };

    public static readonly SqliteMaintenanceStage[] Full =
    {
        SqliteMaintenanceStage.Executions, SqliteMaintenanceStage.DraftHistory, SqliteMaintenanceStage.SelectionRevisions,
        SqliteMaintenanceStage.FavoriteRevisions, SqliteMaintenanceStage.Recovery, SqliteMaintenanceStage.Revisions,
        SqliteMaintenanceStage.Contents,
    };

    public static SqliteMaintenanceStage[] For(SqlMemoryMaintenanceScan scan) =>
        scan == SqlMemoryMaintenanceScan.Full ? Full : Indexed;

    // 以 (時間, 鍵) 列值比較續讀；$time 為 long.MinValue 時等於從頭開始，不必另備一份無下界的 SQL。
    // 一律 INDEXED BY：沒有 ANALYZE 時規劃器偏好等值條件，會改走 Kind 之類的索引再暫存排序，
    // 候選就變成隨表大小成長；索引不可用時寧可直接失敗。
    public const string Executions = @"SELECT ExecutionId,ExecutedAt FROM Executions INDEXED BY IX_Executions_Time
 WHERE ExecutedAt<$cutoff AND (ExecutedAt,ExecutionId)>($time,$key) ORDER BY ExecutedAt,ExecutionId LIMIT $limit;";

    public const string SelectionRevisions = @"SELECT RevisionId,CreatedAt FROM Revisions INDEXED BY IX_Revisions_SelectionTime
 WHERE IsExecutionSelection=1 AND CreatedAt<$cutoff AND (CreatedAt,RevisionId)>($time,$key)
 ORDER BY CreatedAt,RevisionId LIMIT $limit;";

    public const string Recovery = @"SELECT SessionId,CapturedAt FROM Recovery INDEXED BY IX_Recovery_Time
 WHERE CapturedAt<$cutoff AND (CapturedAt,SessionId)>($time,$key) ORDER BY CapturedAt,SessionId LIMIT $limit;";

    // 分組階段的配額界線每組不同，沒有全域時間上界；逐組跳到下一個鍵，組內再依時間範圍讀。
    public const string DraftHistoryGroup = @"SELECT SessionId FROM History INDEXED BY IX_History_SessionDrafts
 WHERE Kind=2 AND RevisionId IS NOT NULL AND SessionId>$group ORDER BY SessionId LIMIT 1;";

    public const string DraftHistory = @"SELECT EntryKey,CreatedAt FROM History INDEXED BY IX_History_SessionDrafts
 WHERE Kind=2 AND RevisionId IS NOT NULL AND SessionId=$group AND CreatedAt<$cutoff AND (CreatedAt,EntryKey)>($time,$key)
 ORDER BY CreatedAt,EntryKey LIMIT $limit;";

    public const string FavoriteRevisionGroup = @"SELECT FavoriteId FROM Revisions INDEXED BY IX_Revisions_Favorite
 WHERE FavoriteId IS NOT NULL AND FavoriteId>$group ORDER BY FavoriteId LIMIT 1;";

    public const string FavoriteRevisions = @"SELECT RevisionId,CreatedAt FROM Revisions INDEXED BY IX_Revisions_Favorite
 WHERE FavoriteId IS NOT NULL AND FavoriteId=$group AND CreatedAt<$cutoff AND (CreatedAt,RevisionId)>($time,$key)
 ORDER BY CreatedAt,RevisionId LIMIT $limit;";

    // 合併的 History 列回收部分執行後，從剩下的執行各取最新與最早一端；只讀一個索引項，不隨合併次數成長。
    public const string LatestEntryExecution = @"SELECT RevisionId,ExecutedAt FROM Executions INDEXED BY IX_Executions_Entry
 WHERE EntryKey=$entry ORDER BY EntryKey DESC,ExecutedAt DESC,ExecutionId DESC LIMIT 1;";

    public const string FirstEntryExecution = @"SELECT ExecutedAt FROM Executions INDEXED BY IX_Executions_Entry
 WHERE EntryKey=$entry ORDER BY EntryKey,ExecutedAt,ExecutionId LIMIT 1";

    public static string ByKey(SqliteMaintenanceStage stage)
    {
        var (table, key) = stage switch
        {
            SqliteMaintenanceStage.Revisions => ("Revisions", "RevisionId"),
            SqliteMaintenanceStage.Contents => ("Contents", "ContentId"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        return "SELECT " + key + " FROM " + table + " WHERE " + key + ">$key ORDER BY " + key + " LIMIT $limit;";
    }

    /// <summary>候選、分組與合併列重算查詢及預期命中的索引；供 EXPLAIN QUERY PLAN 回歸測試逐一確認。</summary>
    public static IEnumerable<(string Sql, string Index)> CandidateQueries() => new[]
    {
        (Executions, "IX_Executions_Time"),
        (DraftHistoryGroup, "IX_History_SessionDrafts"),
        (DraftHistory, "IX_History_SessionDrafts"),
        (SelectionRevisions, "IX_Revisions_SelectionTime"),
        (FavoriteRevisionGroup, "IX_Revisions_Favorite"),
        (FavoriteRevisions, "IX_Revisions_Favorite"),
        (Recovery, "IX_Recovery_Time"),
        (LatestEntryExecution, "IX_Executions_Entry"),
        (FirstEntryExecution, "IX_Executions_Entry"),
    };
}
