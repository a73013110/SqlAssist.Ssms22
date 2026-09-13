using System;
using System.Globalization;

namespace SqlAssist.Core.QueryMemory;

/// <summary>可重現的清單時間文字；不依賴宿主或 UI 執行緒。</summary>
public static class QueryMemoryPresentation
{
    public static string RelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var elapsed = now - value;
        var local = value.ToLocalTime();
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (elapsed.TotalSeconds < 60) return "剛剛 (" + clock + ")";
        if (elapsed.TotalMinutes < 60) return (int)elapsed.TotalMinutes + " 分鐘前 (" + clock + ")";
        if (local.Date == now.ToLocalTime().Date) return (int)elapsed.TotalHours + " 小時前 (" + clock + ")";
        if (local.Date == now.ToLocalTime().Date.AddDays(-1)) return "昨天 " + clock;
        if (elapsed.TotalDays < 7) return (int)elapsed.TotalDays + " 天前 " + clock;
        return local.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }
}

public enum QueryConnectionSort { Recent, Oldest, Alphabetical, ReverseAlphabetical }

/// <summary>只讀連線名稱，不讀 SQL；每頁最多 100 個，與清單分頁互不影響。</summary>
[Serializable]
public sealed class QueryConnectionFacetRequest
{
    public QueryConnectionFacetRequest(bool saved, SavedQueryScope scope, bool databases,
        string? server = null, QueryConnectionSort sort = QueryConnectionSort.Recent, int offset = 0)
    {
        if (!Enum.IsDefined(typeof(SavedQueryScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (!Enum.IsDefined(typeof(QueryConnectionSort), sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        Saved = saved; Scope = scope; Databases = databases; Server = server; Sort = sort; Offset = offset;
    }
    public bool Saved { get; }
    public SavedQueryScope Scope { get; }
    public bool Databases { get; }
    public string? Server { get; }
    public QueryConnectionSort Sort { get; }
    public int Offset { get; }
}
