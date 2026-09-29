using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;

namespace SqlAssist.Metadata.Caching;

/// <summary>
/// 執行個體名單（定序、語言、時區）的快取；鍵是<b>伺服器</b>加名單，不是伺服器加資料庫。
/// </summary>
/// <remarks>
/// 這是唯一一份跨目錄共用的資料。<c>sys.fn_helpcollations()</c>、<c>sys.syslanguages</c>、
/// <c>sys.time_zone_info</c> 回答的是「這個執行個體支援什麼」，與連到哪一個資料庫無關，而
/// <see cref="SqlMetadataCatalog"/> 是以「伺服器＋資料庫」為單位建立的——跟著目錄各存一份的話，
/// 使用者每打出一個跨資料庫的限定字就多五千多個定序字串，而且對同一台伺服器多送一輪查詢。
///
/// 刻意不設有效期，與系統物件同一條理由：名單跟著 SQL Server 的版本走，
/// 不會在一次工作階段中途變動。也刻意<b>不</b>掛在目錄的
/// <see cref="SqlMetadataCatalog.Invalidate"/> 上——那一次重新整理清的是某一個
/// 資料庫，而這一份是別的目錄也在用的。
///
/// 查詢失敗不會走到這裡：失敗的結果一律不進快取，否則連線恢復之後仍然拿到空的。
/// </remarks>
internal static class SqlServerInstanceListCache
{
    private static readonly object Gate = new();

    private static readonly Dictionary<(string Server, CompletionTarget List), IReadOnlyList<SqlInstanceListEntry>> Entries =
        new(new KeyComparer());

    public static bool TryGet(string serverCacheKey, SqlInstanceList list, out IReadOnlyList<SqlInstanceListEntry> entries)
    {
        lock (Gate)
        {
            return Entries.TryGetValue((serverCacheKey, list.Target), out entries!);
        }
    }

    public static void Set(string serverCacheKey, SqlInstanceList list, IReadOnlyList<SqlInstanceListEntry> entries)
    {
        lock (Gate)
        {
            Entries[(serverCacheKey, list.Target)] = entries;
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string Server, CompletionTarget List)>
    {
        public bool Equals((string Server, CompletionTarget List) x, (string Server, CompletionTarget List) y) =>
            x.List == y.List && string.Equals(x.Server, y.Server, StringComparison.Ordinal);

        public int GetHashCode((string Server, CompletionTarget List) key) =>
            unchecked((StringComparer.Ordinal.GetHashCode(key.Server) * 397) ^ (int)key.List);
    }
}
