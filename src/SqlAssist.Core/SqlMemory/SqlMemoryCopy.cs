using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Tabular;

namespace SqlAssist.Core.SqlMemory;

/// <summary>「全部符合」逐頁讀完的結果。</summary>
public sealed class SqlMemoryCopyBatch<T>
{
    internal SqlMemoryCopyBatch(IReadOnlyList<T> items, bool truncated)
    {
        Items = items;
        IsTruncated = truncated;
    }

    /// <summary>依清單順序；最多 <see cref="SqlMemoryCopy.Limit"/> 筆。</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>還有符合的項目，但已經到上限而沒有讀進來。</summary>
    public bool IsTruncated { get; }
}

/// <summary>
/// History 與 Favorites 的批次複製：欄位定義與「全部符合」的逐頁讀取。
/// </summary>
/// <remarks>
/// 欄位只讀清單列已經有的資料，不含 SQL 內文：多選複製要的是一張清單，而內文可能一筆就好幾 MB，
/// 讀全文還得逐筆回儲存層。時間一律印絕對時間，相對時間貼到試算表裡隔天就錯了。
/// </remarks>
public static class SqlMemoryCopy
{
    /// <summary>
    /// 一次最多複製幾筆。
    /// </summary>
    /// <remarks>
    /// 與預設的執行保留筆數同一級：預設設定下整份 History 都複製得到，而一萬列、六欄的 TSV 與
    /// HTML 合起來仍只有幾 MB，剪貼簿與 Excel 都吃得下。調高保留筆數的人仍可以縮小篩選分批複製；
    /// 沒有上限的那一版，一次「全部符合」就是把整個資料庫讀進記憶體。
    /// </remarks>
    public const int Limit = 10000;

    /// <summary>逐頁讀取時的頁大小；儲存層允許的上限，來回次數最少。</summary>
    public const int PageSize = 200;

    /// <summary>時間欄的格式；試算表認得它是日期時間。</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static IReadOnlyList<SqlTabularColumn<SqlHistoryItem>> HistoryColumns { get; } = Array.AsReadOnly(new[]
    {
        new SqlTabularColumn<SqlHistoryItem>("狀態", HistoryStatus),
        new SqlTabularColumn<SqlHistoryItem>("名稱", item => item.DisplayName),
        // 沒有連線就留空格，不寫清單上那句「無伺服器」：那是給人看的說明，貼到試算表裡會被當成一台伺服器。
        new SqlTabularColumn<SqlHistoryItem>("伺服器", item => item.Connection?.Server),
        new SqlTabularColumn<SqlHistoryItem>("資料庫", item => item.Connection?.Database),
        new SqlTabularColumn<SqlHistoryItem>("時間", item => Time(item.CreatedAt)),
        new SqlTabularColumn<SqlHistoryItem>("次數", item => item.ExecutionCount.ToString(CultureInfo.InvariantCulture)),
    });

    public static IReadOnlyList<SqlTabularColumn<SqlFavoriteItem>> FavoriteColumns { get; } = Array.AsReadOnly(new[]
    {
        new SqlTabularColumn<SqlFavoriteItem>("名稱", item => item.Favorite.Name),
        new SqlTabularColumn<SqlFavoriteItem>("伺服器", item => item.Favorite.Server),
        new SqlTabularColumn<SqlFavoriteItem>("資料庫", item => item.Favorite.Database),
        new SqlTabularColumn<SqlFavoriteItem>("更新時間", item => Time(item.UpdatedAt)),
    });

    /// <summary>History 列的狀態字；清單的膠囊與複製出去的欄位共用這一份。</summary>
    public static string HistoryStatus(SqlHistoryItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        return item.Kind == SqlHistoryFilter.Executions ? "執行" : "草稿";
    }

    /// <summary>本機時間的絕對格式。</summary>
    public static string Time(DateTimeOffset time) =>
        time.ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 以同一組條件從第一頁逐頁讀到底，或讀到超過 <paramref name="limit"/> 為止。
    /// </summary>
    /// <remarks>
    /// 游標由儲存層產生並綁定條件，所以 <paramref name="readPage"/> 每一頁都要用同一份條件組請求。
    /// 搜尋用盡單頁掃描預算的那一頁可能一筆都沒有，但仍帶游標：這裡照游標繼續讀，
    /// 不把它當成讀完——使用者要的是「全部符合」，不是清單上那種交給人決定要不要續搜的一頁。
    /// 多讀一筆才知道是不是真的還有，否則剛好一萬筆也會說成「還有更多」。
    /// </remarks>
    /// <param name="readPage">給游標（第一頁是 null）讀一頁。</param>
    /// <param name="progress">每讀完一頁回報目前累計的筆數。</param>
    public static async Task<SqlMemoryCopyBatch<T>> ReadAllAsync<T>(
        Func<string?, CancellationToken, Task<SqlMemoryPage<T>>> readPage, int limit, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (readPage == null) throw new ArgumentNullException(nameof(readPage));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var items = new List<T>();
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await readPage(cursor, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                items.Add(item);
                if (items.Count > limit) break;
            }

            progress?.Report(Math.Min(items.Count, limit));
            cursor = page.NextCursor;
        }
        while (cursor != null && items.Count <= limit);

        var truncated = items.Count > limit;
        if (truncated) items.RemoveRange(limit, items.Count - limit);
        return new SqlMemoryCopyBatch<T>(items.AsReadOnly(), truncated);
    }
}
