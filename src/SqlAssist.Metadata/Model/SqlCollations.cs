using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// <c>COLLATE</c> 之後那份清單需要的兩件事：伺服器支援哪些定序，以及目前這個
/// 資料庫用的是哪一個。
/// </summary>
/// <remarks>
/// 兩件事合成一份回傳，是因為呼叫端一定同時要——名單決定清單的內容，
/// 資料庫的那一個決定誰排在最前面。分成兩趟問的話，那一格會先畫出一份
/// 沒有排序意義的五千筆清單，再重畫一次。
///
/// 兩者的快取層級仍然不同（名單屬於伺服器、定序屬於資料庫），
/// 見 <c>SqlMetadataCatalog.GetCollationsAsync</c>。
/// </remarks>
public sealed class SqlCollations
{
    public static readonly SqlCollations Empty = new(Array.Empty<string>(), null);

    public SqlCollations(IReadOnlyList<string> names, string? databaseCollation)
    {
        Names = names ?? throw new ArgumentNullException(nameof(names));
        DatabaseCollation = databaseCollation;
    }

    /// <summary>伺服器支援的定序名稱；查不到時是空的。</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>目前這個資料庫的定序；查不到時是 <c>null</c>。</summary>
    public string? DatabaseCollation { get; }
}
