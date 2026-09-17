using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>(時間, 鍵) 新到舊游標；History、Favorites 與收藏版本時間軸共用同一份編碼與拒絕規則。</summary>
internal sealed class SqliteTimeCursor
{
    private SqliteTimeCursor(long ticks, string key) { Ticks = ticks; Key = key; }

    public long Ticks { get; }
    public string Key { get; }

    /// <param name="list">清單種類；不同清單的游標互不相容。</param>
    /// <param name="binding">篩選指紋或所屬收藏；換條件沿用舊游標會被拒絕。</param>
    public static string Encode(string list, string storeId, string binding, long ticks, string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(list + "|" + storeId + "|" + binding + "|" +
            ticks.ToString(CultureInfo.InvariantCulture) + "|" + key));

    /// <param name="isKey">鍵的形狀檢查；鍵會直接成為 SQL 參數，外來字串不得混入。</param>
    public static SqliteTimeCursor? Decode(string? cursor, string list, string storeId, string binding, Func<string, bool> isKey)
    {
        if (cursor == null) return null;
        if (cursor.Length > 512) throw Invalid();
        string[] parts;
        try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|'); }
        catch (FormatException) { throw Invalid(); }
        if (parts.Length != 5 || parts[0] != list || parts[1] != storeId || parts[2] != binding ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks > DateTime.MaxValue.Ticks || !isKey(parts[4]))
            throw Invalid();
        return new SqliteTimeCursor(ticks, parts[4]);
    }

    public static bool IsId(string key) => Guid.TryParseExact(key, "N", out _);

    /// <summary>篩選條件的指紋；長度前綴避免分隔符出現在使用者文字時，讓不同條件得到同一指紋。</summary>
    public static string Fingerprint(params string?[] fields)
    {
        var text = new StringBuilder();
        foreach (var field in fields)
            text.Append(field == null ? "-1:" : field.Length.ToString(CultureInfo.InvariantCulture) + ":" + field);
        return SqlContent.Create(text.ToString()).ContentHash;
    }

    /// <summary>接續條件；欄位名稱只來自呼叫端的常數。</summary>
    public void AppendCondition(ICollection<string> conditions, ICollection<(string Name, object? Value)> parameters,
        string timeColumn, string keyColumn)
    {
        conditions.Add("(" + timeColumn + "," + keyColumn + ") < ($time,$key)");
        parameters.Add(("$time", Ticks));
        parameters.Add(("$key", Key));
    }

    private static SqlMemoryStorageException Invalid() =>
        new(SqlMemoryStorageErrorKind.InvalidCursor, "分頁游標失效或不屬於目前篩選條件。");
}

/// <summary>
/// 新到舊 keyset 的一頁：多讀一列判斷下一頁、搜尋預算用盡提早結束、未命中列也推進位置，只寫這一次。
/// </summary>
/// <remarks>
/// 部分搜尋的游標接在最後檢查過的候選之後，不是最後一筆結果之後，下一頁不重掃；
/// 候選查詢必須沿索引串流、沒有暫存排序，否則預算限制不了讀入的 BLOB。
/// </remarks>
internal static class SqliteKeysetPage
{
    /// <param name="position">目前列的 (時間, 鍵)。</param>
    /// <param name="matches">搜尋時的比對；只在有搜尋時呼叫。</param>
    /// <param name="cursor">以最後檢查過的 (時間, 鍵) 產生游標。</param>
    public static SqlMemoryPage<T> Read<T>(SqliteDataReader reader, int pageSize, SqliteSearchScan? search,
        Func<SqliteDataReader, (long Ticks, string Key)> position, Func<SqliteDataReader, SqliteSearchScan, bool> matches,
        Func<SqliteDataReader, T> item, Func<long, string, string> cursor, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var last = (Ticks: 0L, Key: "");
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (search?.IsExhausted == true)
                return new SqlMemoryPage<T>(items, cursor(last.Ticks, last.Key), SqliteDatabase.Time(last.Ticks));
            var matched = search == null || matches(reader, search);
            if (matched && items.Count == pageSize) return new SqlMemoryPage<T>(items, cursor(last.Ticks, last.Key));
            last = position(reader);
            if (matched) items.Add(item(reader));
        }
        return new SqlMemoryPage<T>(items, null);
    }
}
