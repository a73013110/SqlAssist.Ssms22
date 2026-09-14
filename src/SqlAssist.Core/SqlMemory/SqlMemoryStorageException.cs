using System;
using System.Runtime.Serialization;

namespace SqlAssist.Core.SqlMemory;

/// <summary>呼叫端依分類決定重試、提示重新整理或停用；不從訊息字串猜原因。</summary>
public enum SqlMemoryStorageErrorKind
{
    /// <summary>無法歸類；呼叫端一律視為致命。</summary>
    Unknown,

    /// <summary>另一個連線或程序持有鎖（SQLITE_BUSY／SQLITE_LOCKED）；唯一可自動重試的分類。</summary>
    Busy,

    /// <summary>磁碟、權限、空間或開檔失敗。</summary>
    Io,

    /// <summary>檔案不是資料庫、頁面損毀或內容完整性檢查失敗。</summary>
    Corrupt,

    /// <summary>身分或 schema 版本不相容、外來資料庫；不自動重建。</summary>
    Incompatible,

    /// <summary>呼叫端參數不合法。</summary>
    InvalidArgument,

    /// <summary>游標格式錯誤、跨庫或跨篩選重用；重新整理即可。</summary>
    InvalidCursor,

    /// <summary>交易違反條件約束。</summary>
    Constraint,

    /// <summary>維護租約已易手，或共用維護狀態已被別的程序推進；整批已回復，重讀狀態即可，不是故障。</summary>
    Conflict,

    /// <summary>儲存尚未開啟、已停用，或在操作途中被關閉／重新開啟；重新整理即可，不是儲存故障。</summary>
    Unavailable,
}

/// <summary>
/// 儲存層對 Core 的唯一失敗型別。只帶可序列化欄位、不帶 inner exception：
/// provider 例外未必能跨 AppDomain，原始錯誤碼與型別名稱另外保留。
/// </summary>
[Serializable]
public sealed class SqlMemoryStorageException : Exception
{
    public SqlMemoryStorageException(SqlMemoryStorageErrorKind kind, string message, int? errorCode = null,
        int? extendedErrorCode = null, string? sourceType = null)
        : base(message)
    {
        Kind = kind;
        ErrorCode = errorCode;
        ExtendedErrorCode = extendedErrorCode;
        SourceType = sourceType;
    }

    private SqlMemoryStorageException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        Kind = (SqlMemoryStorageErrorKind)info.GetInt32(nameof(Kind));
        ErrorCode = (int?)info.GetValue(nameof(ErrorCode), typeof(object));
        ExtendedErrorCode = (int?)info.GetValue(nameof(ExtendedErrorCode), typeof(object));
        SourceType = info.GetString(nameof(SourceType));
    }

    public SqlMemoryStorageErrorKind Kind { get; }

    /// <summary>provider 的主要錯誤碼，例如 SQLite 的 5；非 provider 失敗為 null。</summary>
    public int? ErrorCode { get; }

    public int? ExtendedErrorCode { get; }

    /// <summary>原始例外的型別名稱，只供診斷。</summary>
    public string? SourceType { get; }

    public bool IsTransient => Kind == SqlMemoryStorageErrorKind.Busy;

    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(Kind), (int)Kind);
        info.AddValue(nameof(ErrorCode), ErrorCode, typeof(object));
        info.AddValue(nameof(ExtendedErrorCode), ExtendedErrorCode, typeof(object));
        info.AddValue(nameof(SourceType), SourceType);
    }
}
