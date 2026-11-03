using System;
using System.Collections.Generic;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Connections;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// <see cref="SqlSearchCatalogs"/> 的純判斷那一半：一筆結果落在樹上哪一台。
/// </summary>
/// <remarks>
/// 與 <c>SqlSearchCatalogs.cs</c> 是同一個類別的兩半，切開只有一個理由：這一半零 VS／SSMS
/// 相依，「不准頂替」才寫得進回歸測試。伺服器名稱怎麼比仍然只有
/// <see cref="SqlCatalogSelection.IsSameServer(string?, string?)"/> 一份。
/// </remarks>
internal sealed partial class SqlSearchCatalogs
{
    /// <summary>
    /// 樹上哪一台是 <paramref name="origin"/> 那一台；沒有就回傳 null。
    /// </summary>
    /// <remarks>
    /// 指名的那一台（<paramref name="named"/>）是同一台時優先用它：同一台伺服器在樹上可能有
    /// 兩條連線（不同登入），使用者挑的是那一條。不是同一台時它<b>不</b>算數——
    /// 指名的伺服器是現在的範圍，而這一筆可能是換範圍之前搜到的。
    /// </remarks>
    public static SsmsObjectExplorerServer? FindOnTree(
        SsmsObjectExplorerServer? named,
        IReadOnlyList<SsmsObjectExplorerServer>? servers,
        SqlSearchOrigin origin)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        if (named is not null && SqlCatalogSelection.IsSameServer(named, origin.ServerName)) return named;

        foreach (var server in servers ?? Array.Empty<SsmsObjectExplorerServer>())
        {
            if (SqlCatalogSelection.IsSameServer(server, origin.ServerName)) return server;
        }

        return null;
    }

    /// <summary>範圍已經不在這一筆那一台時的那一句；移至定義、物件總管與預覽共用。</summary>
    public static string ElsewhereNotice(SqlSearchOrigin origin) =>
        SqlSearchText.ElsewhereNotice(origin);
}
