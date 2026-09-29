using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 一份執行個體名單向伺服器問到的兩件事：名單本身，以及目前在用的那一個。
/// </summary>
/// <remarks>
/// 兩件事合成一份回傳，是因為呼叫端一定同時要——名單決定清單的內容，
/// 在用的那一個決定誰排在最前面。分成兩趟問的話，那一格會先畫出一份
/// 沒有排序意義的清單，再重畫一次。兩者的快取層級不同（名單屬於伺服器），
/// 見 <c>SqlMetadataCatalog.GetInstanceListAsync</c>。
/// </remarks>
public sealed class SqlInstanceListData
{
    public static readonly SqlInstanceListData Empty = new(Array.Empty<SqlInstanceListEntry>(), null);

    public SqlInstanceListData(IReadOnlyList<SqlInstanceListEntry> entries, string? inUse)
    {
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        InUse = string.IsNullOrWhiteSpace(inUse) ? null : inUse;
    }

    /// <summary>伺服器上的名單；查不到時是空的。</summary>
    public IReadOnlyList<SqlInstanceListEntry> Entries { get; }

    /// <summary>目前在用的那一個；查不到或這一版問不到時是 <c>null</c>。</summary>
    public string? InUse { get; }
}
