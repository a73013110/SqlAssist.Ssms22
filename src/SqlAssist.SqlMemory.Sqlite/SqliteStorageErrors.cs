using System;
using System.IO;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>
/// 把 provider 與 store 的例外轉成可跨 AppDomain 的分類例外。
/// 只有忙碌可重試；分類不出來一律 Unknown，讓呼叫端當成致命，不擅自重試。
/// </summary>
public static class SqliteStorageErrors
{
    public static SqlMemoryStorageException Translate(Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        if (error is AggregateException { InnerExceptions.Count: 1 } aggregate) error = aggregate.InnerExceptions[0];
        var source = error.GetType().Name;
        return error switch
        {
            // 重新建立而不是原樣回傳：原例外可能帶著無法序列化的 inner exception 或堆疊資料。
            SqlMemoryStorageException storage => new(storage.Kind, storage.Message, storage.ErrorCode,
                storage.ExtendedErrorCode, storage.SourceType ?? source),
            SqliteException sqlite => new(Classify(sqlite.SqliteErrorCode), "SQL Memory 儲存失敗（SQLite " +
                sqlite.SqliteErrorCode + "/" + sqlite.SqliteExtendedErrorCode + "）：" + sqlite.Message,
                sqlite.SqliteErrorCode, sqlite.SqliteExtendedErrorCode, source),
            InvalidDataException => new(SqlMemoryStorageErrorKind.Corrupt, error.Message, sourceType: source),
            ArgumentException => new(SqlMemoryStorageErrorKind.InvalidArgument, error.Message, sourceType: source),
            IOException or UnauthorizedAccessException => new(SqlMemoryStorageErrorKind.Io, error.Message, sourceType: source),
            _ => new(SqlMemoryStorageErrorKind.Unknown,
                "SQL Memory 儲存失敗（" + source + "）：" + error.GetBaseException().Message, sourceType: source),
        };
    }

    /// <summary>依 SQLite 主要結果碼分類；延伸碼（例如 BUSY_SNAPSHOT）的低 8 位元就是主要碼。</summary>
    public static SqlMemoryStorageErrorKind Classify(int sqliteErrorCode) => (sqliteErrorCode & 0xFF) switch
    {
        5 or 6 => SqlMemoryStorageErrorKind.Busy, // SQLITE_BUSY、SQLITE_LOCKED
        11 or 26 => SqlMemoryStorageErrorKind.Corrupt, // SQLITE_CORRUPT、SQLITE_NOTADB
        7 or 8 or 10 or 13 or 14 or 15 or 23 => SqlMemoryStorageErrorKind.Io, // NOMEM、READONLY、IOERR、FULL、CANTOPEN、PROTOCOL、AUTH
        19 => SqlMemoryStorageErrorKind.Constraint,
        _ => SqlMemoryStorageErrorKind.Unknown,
    };
}
