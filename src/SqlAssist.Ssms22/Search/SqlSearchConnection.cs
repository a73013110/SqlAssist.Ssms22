using System;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 這一輪範圍的目錄，以及它連著的那一台；一起問、一起換、一起讀。
/// </summary>
/// <remarks>
/// 兩者<b>只能</b>來自同一次解析。分開問的症狀出現在換連線之後、背景確認之前那一段：目錄還是
/// 上一台的，伺服器已經是新的那一台，搜出來的每一筆都標錯台——在物件總管上找不到，
/// 移至定義還會沿用新那一台的連線開出上一台的定義。所以這個型別只由
/// <c>SqlSearchCatalogs.ResolveConnection</c> 建立，下游不再各自組一份。
/// </remarks>
internal sealed class SqlSearchConnection
{
    internal SqlSearchConnection(SqlMetadataCatalog catalog, SqlSearchOrigin origin)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
    }

    internal SqlMetadataCatalog Catalog { get; }

    internal SqlSearchOrigin Origin { get; }

    /// <summary>兩份指的是同一個範圍；都沒有連線也算。</summary>
    /// <remarks>
    /// 比快取鍵而不比參考：註冊表可能為同一個鍵換過一份目錄，而那不是換範圍。
    /// </remarks>
    internal static bool SameScope(SqlSearchConnection? left, SqlSearchConnection? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.Catalog.CacheKey, right.Catalog.CacheKey, StringComparison.Ordinal)
              && string.Equals(left.Origin.ServerName, right.Origin.ServerName, StringComparison.Ordinal);
}
