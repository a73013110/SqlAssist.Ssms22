using System;
using System.Data;

namespace SqlAssist.Metadata;

/// <summary>
/// 提供中繼資料查詢專用的連線。
/// </summary>
/// <remarks>
/// 中繼資料查詢絕不能借用編輯器正在使用的那條連線：使用者可能正在執行長時間查詢，
/// 或處於明確交易之中。實作應該另外開一條連線，並在用完後由呼叫端釋放。
/// </remarks>
public interface ISqlConnectionSource
{
    /// <summary>
    /// 識別「同一個伺服器上的同一個資料庫」的穩定字串，用於快取。
    /// 不可包含認證資訊，也不可使用物件識別雜湊之類會隨重連而改變的值。
    /// </summary>
    string CacheKey { get; }

    /// <summary>目前查詢視窗選取的資料庫名稱。</summary>
    string DatabaseName { get; }

    /// <summary>開啟一條已切換到 <see cref="DatabaseName"/> 的連線；呼叫端負責釋放。</summary>
    IDbConnection OpenConnection();
}
