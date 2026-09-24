using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 一次刪除的對象：一組 History 或一組收藏，不混。單筆刪除是只有一筆的一次，與多選刪除走同一條路。
/// </summary>
/// <remarks>
/// History 分批交給儲存層，每批一個交易：一次交易握著寫入鎖，一萬筆一起刪會讓同時間的擷取卡到逾時；
/// 逐筆各開一個交易則是一萬次落盤。收藏逐筆送：每一筆都要比對自己的版本，衝突的那幾筆要留在清單上，
/// 而收藏的數量級是幾十筆，不值得為它多一份批次契約。
/// </remarks>
public sealed class SqlMemoryDeletion
{
    /// <summary>History 一個交易刪幾筆；與逐頁讀取同一個數字，讀一頁刪一頁。</summary>
    public const int ChunkSize = SqlMemoryBulk.PageSize;

    private readonly IReadOnlyList<SqlHistoryItem> _history;
    private readonly IReadOnlyList<SqlFavoriteItem> _favorites;

    private SqlMemoryDeletion(IReadOnlyList<SqlHistoryItem> history, IReadOnlyList<SqlFavoriteItem> favorites)
    {
        _history = history;
        _favorites = favorites;
    }

    public static SqlMemoryDeletion Of(IReadOnlyList<SqlHistoryItem> items) =>
        new(items ?? throw new ArgumentNullException(nameof(items)), Array.Empty<SqlFavoriteItem>());

    public static SqlMemoryDeletion Of(IReadOnlyList<SqlFavoriteItem> items) =>
        new(Array.Empty<SqlHistoryItem>(), items ?? throw new ArgumentNullException(nameof(items)));

    public bool IsFavorites => _favorites.Count > 0;

    public int Count => _history.Count + _favorites.Count;

    /// <summary>
    /// 依序刪除；取消只在兩批之間生效，已提交的那幾批照實回報。
    /// </summary>
    /// <remarks>
    /// 取消不擲出而是回傳：使用者按了取消時，前面幾批已經刪掉了，畫面要知道哪幾筆不在了。
    /// 儲存層在交易中途看到取消會整批回滾，所以那一批不算。其他例外照樣往上丟，
    /// 呼叫端重新讀清單，而不是相信一份只做了一半的回報。
    /// </remarks>
    /// <param name="deleteHistory">一個交易刪一批，回傳實際刪掉幾筆。</param>
    /// <param name="deleteFavorite">比對版本後移除一筆收藏。</param>
    /// <param name="progress">每處理完一批回報累計的筆數。</param>
    public async Task<SqlMemoryDeleteReport> RunAsync(
        Func<IReadOnlyList<SqlHistoryItem>, CancellationToken, Task<int>> deleteHistory,
        Func<SqlFavoriteItem, CancellationToken, Task<SqlFavoriteWriteResult>> deleteFavorite,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (deleteHistory == null) throw new ArgumentNullException(nameof(deleteHistory));
        if (deleteFavorite == null) throw new ArgumentNullException(nameof(deleteFavorite));
        var removed = new List<Guid>();
        var deleted = 0;
        var conflicts = 0;
        var processed = 0;
        try
        {
            for (var start = 0; start < _history.Count; start += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = _history.Skip(start).Take(ChunkSize).ToArray();
                deleted += await deleteHistory(chunk, cancellationToken).ConfigureAwait(false);
                // 沒刪到的那幾筆是已經不在了（被維護回收或別的 SSMS 刪掉）：目標狀態一樣，照樣移出清單。
                removed.AddRange(chunk.Select(item => item.ItemId));
                processed += chunk.Length;
                progress?.Report(processed);
            }

            foreach (var item in _favorites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await deleteFavorite(item, cancellationToken).ConfigureAwait(false);
                if (result == SqlFavoriteWriteResult.Committed)
                {
                    deleted++;
                    removed.Add(item.Favorite.FavoriteId);
                }
                else conflicts++;
                progress?.Report(++processed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SqlMemoryDeleteReport(IsFavorites, Count, removed, deleted, conflicts, canceled: true);
        }

        return new SqlMemoryDeleteReport(IsFavorites, Count, removed, deleted, conflicts, canceled: false);
    }
}

/// <summary>一次刪除的結果：哪幾筆已經不在儲存裡、實際刪了幾筆、幾筆因衝突留下。</summary>
public sealed class SqlMemoryDeleteReport
{
    internal SqlMemoryDeleteReport(bool favorites, int requested, IReadOnlyList<Guid> removed, int deleted, int conflicts,
        bool canceled)
    {
        IsFavorites = favorites;
        Requested = requested;
        Removed = removed;
        Deleted = deleted;
        Conflicts = conflicts;
        IsCanceled = canceled;
    }

    public bool IsFavorites { get; }

    public int Requested { get; }

    /// <summary>應該移出清單的列識別：History 是 <see cref="SqlHistoryItem.ItemId"/>，收藏是 <c>FavoriteId</c>。</summary>
    public IReadOnlyList<Guid> Removed { get; }

    public int Deleted { get; }

    /// <summary>本來就不在的那幾筆；只有 History 會有。</summary>
    public int Missing => Removed.Count - Deleted;

    /// <summary>收藏的版本已經換過（被修改或刪除），沒有移除。</summary>
    public int Conflicts { get; }

    public bool IsCanceled { get; }

    /// <summary>
    /// 通知上的那一句：數量、已不存在與衝突，收藏另外說明 History 不受影響。
    /// </summary>
    /// <remarks>
    /// 單筆成功時是空字串：標題與主體已經說完「刪了哪一筆」。多筆一律寫數字，
    /// 使用者要對得上確認框裡的筆數。
    /// </remarks>
    public string Summary
    {
        get
        {
            var verb = IsFavorites ? "移除" : "刪除";
            var parts = new List<string>();
            if (IsCanceled) parts.Add($"取消前已{verb} {Count(Deleted)} 筆，其餘保留。");
            else if (Requested > 1) parts.Add($"共{verb} {Count(Deleted)} 筆。");
            if (Missing > 0) parts.Add(Requested == 1 ? "這一筆已經不存在；已從清單移除。" : $"{Count(Missing)} 筆已經不存在。");
            if (Conflicts > 0)
                parts.Add((Requested == 1 ? "收藏" : Count(Conflicts) + " 筆收藏") + "已被修改或移除，未移除；請重新整理後再操作。");
            if (IsFavorites && Deleted > 0) parts.Add("History 不受影響。");
            return string.Concat(parts);
        }
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
