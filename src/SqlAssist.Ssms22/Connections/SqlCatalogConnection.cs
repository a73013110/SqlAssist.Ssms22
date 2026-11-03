using System;
using System.ComponentModel;
using SqlAssist.Metadata.Caching;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 範圍列選定的那一份目錄，以及它連著的那一台；一起問、一起換、一起讀。
/// </summary>
/// <remarks>
/// 兩者<b>只能</b>來自同一次解析。分開問的症狀出現在換連線之後、背景確認之前那一段：目錄還是
/// 上一台的，伺服器已經是新的那一台，SQL Search 的每一筆命中都標錯台，資料字典則把描述寫到
/// 另一台上。所以這個型別只由 <see cref="SqlCatalogSelection.ResolveConnection"/> 與
/// <see cref="SqlCatalogSelection.ReadActiveEditorAsync"/> 建立，下游不再各自組一份。
///
/// <see cref="ServerName"/> 是連線字串裡的伺服器名稱，比對規則只有
/// <see cref="SqlCatalogSelection.IsSameServer(string?, string?)"/> 一份。
/// </remarks>
internal sealed class SqlCatalogConnection
{
    [Localizable(false)]
    internal SqlCatalogConnection(SqlMetadataCatalog catalog, string serverName)
    {
        if (string.IsNullOrEmpty(serverName)) throw new ArgumentException("伺服器名稱不可為空。", nameof(serverName));

        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ServerName = serverName;
    }

    internal SqlMetadataCatalog Catalog { get; }

    internal string ServerName { get; }

    /// <summary>兩份指的是同一個範圍；都沒有連線也算。</summary>
    /// <remarks>
    /// 比快取鍵而不比參考：註冊表可能為同一個鍵換過一份目錄，而那不是換範圍。
    /// </remarks>
    internal static bool SameScope(SqlCatalogConnection? left, SqlCatalogConnection? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.Catalog.CacheKey, right.Catalog.CacheKey, StringComparison.Ordinal)
              && string.Equals(left.ServerName, right.ServerName, StringComparison.Ordinal);
}
