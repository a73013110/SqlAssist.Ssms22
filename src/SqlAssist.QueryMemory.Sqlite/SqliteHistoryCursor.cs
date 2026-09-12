using System;
using System.Globalization;
using System.Text;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

internal sealed class SqliteHistoryCursor
{
    private SqliteHistoryCursor(long ticks, string entryKey) { Ticks = ticks; EntryKey = entryKey; }
    public long Ticks { get; }
    public string EntryKey { get; }

    public static string Encode(QueryHistoryRequest request, string storeId, long ticks, string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("1|" + storeId + "|" + Fingerprint(request) + "|" +
            ticks.ToString(CultureInfo.InvariantCulture) + "|" + key));

    public static SqliteHistoryCursor? Decode(QueryHistoryRequest request, string storeId)
    {
        if (request.Cursor == null) return null;
        if (request.Cursor.Length > 512) throw new ArgumentException("分頁游標無效。", nameof(request));
        string[] parts;
        try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(request.Cursor)).Split('|'); }
        catch (FormatException error) { throw new ArgumentException("分頁游標無效。", nameof(request), error); }
        if (parts.Length != 5 || parts[0] != "1" || parts[1] != storeId || parts[2] != Fingerprint(request) ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks > DateTime.MaxValue.Ticks || parts[4].Length != 33 || "ers".IndexOf(parts[4][0]) < 0 ||
            !Guid.TryParseExact(parts[4].Substring(1), "N", out _))
            throw new ArgumentException("分頁游標失效或不屬於目前篩選條件。", nameof(request));
        return new SqliteHistoryCursor(ticks, parts[4]);
    }

    private static string Fingerprint(QueryHistoryRequest request)
    {
        string Field(string? value) => value == null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        // 長度前綴避免分隔符出現在使用者文字時，讓不同 filter 得到同一指紋。
        return QueryContent.Create(((int)request.Kind).ToString(CultureInfo.InvariantCulture) + ";" +
            Field(request.Search) + Field(request.Server) + Field(request.Database) +
            Field(request.Since?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)) +
            Field(request.Until?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture))).ContentHash;
    }
}
