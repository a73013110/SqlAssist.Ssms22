using System;
using System.Collections.Generic;
using System.Globalization;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Tabular;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// History 與 Favorites 的批次複製欄位；上限與「全部符合」的逐頁讀取在 <see cref="SqlMemoryBulk"/>。
/// </summary>
/// <remarks>
/// 欄位只讀清單列已經有的資料，不含 SQL 內文：多選複製要的是一張清單，而內文可能一筆就好幾 MB，
/// 讀全文還得逐筆回儲存層。時間一律印絕對時間，相對時間貼到試算表裡隔天就錯了。
/// </remarks>
public static class SqlMemoryCopy
{
    /// <summary>時間欄的格式；試算表認得它是日期時間。</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    // 欄位標題用複製當下的語言，所以每次取值都重建。
    public static IReadOnlyList<SqlTabularColumn<SqlHistoryItem>> HistoryColumns => Array.AsReadOnly(new[]
    {
        new SqlTabularColumn<SqlHistoryItem>(CommonText.Status, HistoryStatus),
        new SqlTabularColumn<SqlHistoryItem>(CommonText.Name, item => item.DisplayName),
        // 沒有連線就留空格，不寫清單上那句「無伺服器」：那是給人看的說明，貼到試算表裡會被當成一台伺服器。
        new SqlTabularColumn<SqlHistoryItem>(CommonText.Server, item => item.Connection?.Server),
        new SqlTabularColumn<SqlHistoryItem>(CommonText.Database, item => item.Connection?.Database),
        new SqlTabularColumn<SqlHistoryItem>(SqlMemoryText.ColumnTime, item => Time(item.CreatedAt)),
        new SqlTabularColumn<SqlHistoryItem>(SqlMemoryText.ColumnCount, item => item.ExecutionCount.ToString(CultureInfo.InvariantCulture)),
    });

    public static IReadOnlyList<SqlTabularColumn<SqlFavoriteItem>> FavoriteColumns => Array.AsReadOnly(new[]
    {
        new SqlTabularColumn<SqlFavoriteItem>(CommonText.Name, item => item.Favorite.Name),
        new SqlTabularColumn<SqlFavoriteItem>(CommonText.Server, item => item.Favorite.Server),
        new SqlTabularColumn<SqlFavoriteItem>(CommonText.Database, item => item.Favorite.Database),
        new SqlTabularColumn<SqlFavoriteItem>(SqlMemoryText.ColumnUpdated, item => Time(item.UpdatedAt)),
    });

    /// <summary>History 列的狀態字；清單的膠囊與複製出去的欄位共用這一份。</summary>
    public static string HistoryStatus(SqlHistoryItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        return item.Kind == SqlHistoryFilter.Executions ? SqlMemoryText.KindExecutions : SqlMemoryText.KindDrafts;
    }

    /// <summary>本機時間的絕對格式。</summary>
    public static string Time(DateTimeOffset time) =>
        time.ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
}
