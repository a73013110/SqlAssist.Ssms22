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
        if (elapsed.TotalSeconds < 60) return SqlMemoryText.JustNow(clock);
        if (elapsed.TotalMinutes < 60) return SqlMemoryText.MinutesAgo(Whole(elapsed.TotalMinutes), clock);
        if (local.Date == now.ToLocalTime().Date) return SqlMemoryText.HoursAgo(Whole(elapsed.TotalHours), clock);
        if (local.Date == now.ToLocalTime().Date.AddDays(-1)) return SqlMemoryText.Yesterday(clock);
        if (elapsed.TotalDays < 7) return SqlMemoryText.DaysAgo(Whole(elapsed.TotalDays), clock);
        return local.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>使用者動作失敗時的一行訊息；依分類說明下一步，不從例外字串猜原因。</summary>
    /// <param name="action">動作名稱，例如「載入」「複製」。</param>
    public static string Failure(string action, Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        return error is SqlMemoryStorageException { Kind: SqlMemoryStorageErrorKind.Unavailable }
            ? SqlMemoryText.ActionIncomplete(action, error.Message)
            : SqlMemoryText.ActionFailed(action, Describe(error));
    }

    /// <summary>失敗原因的一句話，用目前的語言。</summary>
    /// <remarks>
    /// 儲存層例外的 <see cref="Exception.Message"/> 是隔離 AppDomain 寫的診斷繁中，
    /// 這裡只看分類、原因與錯誤碼；原文留在診斷紀錄。宿主自己擲出的 Unavailable 與
    /// 非儲存層例外照原訊息。
    /// </remarks>
    public static string Describe(Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        if (error is not SqlMemoryStorageException storage || storage.Kind == SqlMemoryStorageErrorKind.Unavailable)
            return error.Message;
        var reason = storage.Reason switch
        {
            SqlMemoryStorageReason.BackupPathNotAbsolute => SqlMemoryText.ErrorBackupPathNotAbsolute,
            SqlMemoryStorageReason.BackupOverwritesDatabase => SqlMemoryText.ErrorBackupOverwritesDatabase,
            SqlMemoryStorageReason.BackupFileExists => SqlMemoryText.ErrorBackupFileExists,
            _ => storage.Kind switch
            {
                SqlMemoryStorageErrorKind.Busy => SqlMemoryText.ErrorBusy,
                SqlMemoryStorageErrorKind.InvalidCursor => SqlMemoryText.ErrorInvalidCursor,
                SqlMemoryStorageErrorKind.Io => SqlMemoryText.ErrorIo,
                SqlMemoryStorageErrorKind.Corrupt => SqlMemoryText.ErrorCorrupt,
                SqlMemoryStorageErrorKind.Incompatible => SqlMemoryText.ErrorIncompatible,
                SqlMemoryStorageErrorKind.InvalidArgument => SqlMemoryText.ErrorInvalidArgument,
                SqlMemoryStorageErrorKind.Constraint => SqlMemoryText.ErrorConstraint,
                SqlMemoryStorageErrorKind.Conflict => SqlMemoryText.ErrorConflict,
                _ => SqlMemoryText.ErrorUnknown,
            },
        };
        // 忙碌與游標失效自己就說清楚了；其餘附上 SQLite 錯誤碼，回報時對得上診斷紀錄。
        return storage.ErrorCode is { } code && storage.Kind is not (SqlMemoryStorageErrorKind.Busy or SqlMemoryStorageErrorKind.InvalidCursor)
            ? SqlMemoryText.ErrorSentenceWithCode(reason, Number(code), Number(storage.ExtendedErrorCode ?? code))
            : SqlMemoryText.ErrorSentence(reason);
    }

    private static string Whole(double value) => Number((int)value);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
