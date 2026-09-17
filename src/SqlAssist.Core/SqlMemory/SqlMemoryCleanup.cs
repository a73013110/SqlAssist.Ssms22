using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 使用者主動的清理與立即維護：把有界批次串到巡完，結束後截斷 WAL 並回報釋出容量。
/// </summary>
/// <remarks>
/// 排程維護一次只跑一批、讓出 I/O；這裡是使用者按下按鈕等結果，所以連續跑完，但每批仍是
/// 一個有界交易，擷取與讀取可以插在批次之間。收藏版本與立即維護都走不帶 Claim 的一次性批次，
/// 不讀也不寫共用維護輪次，和背景維護同時進行也只是各自多巡一次。
/// </remarks>
public static class SqlMemoryCleanup
{
    /// <summary>每個交易最多處理的候選；與背景維護相同，單次鎖住資料庫的時間有上界。</summary>
    public const int BatchSize = 200;

    /// <summary>刪除會讓父版本變成孤立而需要再巡；超過這個輪數仍在刪，就留給背景維護，不讓按鈕停不下來。</summary>
    public const int MaximumPasses = 4;

    /// <param name="progress">每批結束後回報累計刪除的資料列數。</param>
    public static async Task<SqlMemoryCleanupResult> RunAsync(ISqlMemoryMaintenanceStore store, SqlMemoryCleanupRequest request,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (request == null) throw new ArgumentNullException(nameof(request));
        var before = await store.ReadUsageAsync(cancellationToken).ConfigureAwait(false);
        long entries = 0, rows = 0;
        if (request.TouchesHistory)
        {
            string? cursor = null;
            do
            {
                var batch = await store.CleanupHistoryAsync(request, cursor, BatchSize, cancellationToken).ConfigureAwait(false);
                entries += batch.DeletedEntries;
                rows += batch.DeletedRows;
                progress?.Report(rows);
                cursor = batch.Cursor;
            }
            while (cursor != null);
        }

        if (request.Includes(SqlMemoryCleanupTargets.FavoriteRevisions))
        {
            // 只給每收藏配額：其餘期限與配額為 null，維護的其他階段一進去就結束，不會順手刪別的。
            var policy = new SqlRetentionPolicy(null, null, null, maxRevisionsPerFavorite: request.KeepFavoriteRevisions);
            rows += await DrainAsync(store, policy, rows, progress, cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(store, before, entries, rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>以日常那一級政策立即巡完一輪；不升級、不寫共用輪次，下一次排程照常。</summary>
    public static async Task<SqlMemoryCleanupResult> MaintainAsync(ISqlMemoryMaintenanceStore store, SqlRetentionPolicy policy,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        var before = await store.ReadUsageAsync(cancellationToken).ConfigureAwait(false);
        var rows = await DrainAsync(store, policy, 0, progress, cancellationToken).ConfigureAwait(false);
        return await FinishAsync(store, before, 0, rows, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> DrainAsync(ISqlMemoryMaintenanceStore store, SqlRetentionPolicy policy, long reported,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        long rows = 0;
        for (var pass = 0; pass < MaximumPasses; pass++)
        {
            string? cursor = null;
            SqlMemoryMaintenanceResult result;
            do
            {
                result = await store.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, BatchSize, cursor), cancellationToken)
                    .ConfigureAwait(false);
                rows += result.DeletedRows;
                progress?.Report(reported + rows);
                cursor = result.Cursor;
            }
            while (cursor != null);
            if (!result.RequiresAnotherPass) break;
        }
        return rows;
    }

    private static async Task<SqlMemoryCleanupResult> FinishAsync(ISqlMemoryMaintenanceStore store, SqlMemoryUsage before,
        long entries, long rows, CancellationToken cancellationToken)
    {
        // 有刪除才截斷 WAL：沒有刪除時截斷只是多一次寫入。截斷被讀取者擋下不是失敗，回報的仍是當下觀測值。
        var after = rows > 0
            ? (await store.CheckpointAsync(cancellationToken).ConfigureAwait(false)).Usage
            : await store.ReadUsageAsync(cancellationToken).ConfigureAwait(false);
        return new SqlMemoryCleanupResult(entries, rows, Math.Max(0, before.ContentBytes - after.ContentBytes), after);
    }
}
