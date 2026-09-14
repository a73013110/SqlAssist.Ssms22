using System;
using System.Globalization;

namespace SqlAssist.Core.SqlMemory;

/// <summary>可重現的清單時間文字；不依賴宿主或 UI 執行緒。</summary>
public static class SqlMemoryTimeText
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

    /// <summary>使用者動作失敗時的一行訊息；依分類說明下一步，不從例外字串猜原因。</summary>
    /// <param name="action">動作名稱，例如「載入」「複製」。</param>
    public static string Failure(string action, Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        return error is SqlMemoryStorageException storage ? storage.Kind switch
        {
            SqlMemoryStorageErrorKind.Busy => action + "失敗：資料庫正被其他作業使用；稍後再試。",
            SqlMemoryStorageErrorKind.InvalidCursor => action + "失敗：清單已變更；請重新整理。",
            SqlMemoryStorageErrorKind.Unavailable => action + "未完成：" + storage.Message,
            _ => action + "失敗：" + storage.Message,
        } : action + "失敗：" + error.Message;
    }
}
