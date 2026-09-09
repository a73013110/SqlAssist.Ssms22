using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Caching;

/// <summary>
/// 定序名單的快取；鍵是<b>伺服器</b>，不是伺服器加資料庫。
/// </summary>
/// <remarks>
/// 這是唯一一份跨目錄共用的資料。<c>sys.fn_helpcollations()</c> 回答的是
/// 「這個執行個體支援哪些定序」，與連到哪一個資料庫無關，而
/// <see cref="SqlMetadataCatalog"/> 是以「伺服器＋資料庫」為單位建立的——
/// 跟著目錄各存一份的話，使用者每打出一個跨資料庫的限定字就多五千多個字串，
/// 而且對同一台伺服器多送一輪查詢。
///
/// 刻意不設有效期，與系統物件同一條理由：名單跟著 SQL Server 的版本走，
/// 不會在一次工作階段中途變動。也刻意<b>不</b>掛在目錄的
/// <see cref="SqlMetadataCatalog.Invalidate"/> 上——那一次重新整理清的是某一個
/// 資料庫，而這一份是別的目錄也在用的。
///
/// 查詢失敗不會走到這裡：失敗的結果一律不進快取，否則連線恢復之後仍然拿到空的。
/// </remarks>
internal static class SqlServerCollationCache
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, IReadOnlyList<string>> Names =
        new(StringComparer.Ordinal);

    public static bool TryGet(string serverCacheKey, out IReadOnlyList<string> names)
    {
        lock (Gate)
        {
            return Names.TryGetValue(serverCacheKey, out names!);
        }
    }

    public static void Set(string serverCacheKey, IReadOnlyList<string> names)
    {
        lock (Gate)
        {
            Names[serverCacheKey] = names;
        }
    }
}
