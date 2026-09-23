using System;
using System.Collections.Generic;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Connections;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// <see cref="SqlSearchCatalogs"/> 的純判斷那一半：兩個伺服器名稱是不是同一台、
/// 一筆結果落在樹上哪一台。
/// </summary>
/// <remarks>
/// 與 <c>SqlSearchCatalogs.cs</c> 是同一個類別的兩半，切開只有一個理由：這一半零 VS／SSMS
/// 相依，比對規則與「不准頂替」這兩條才寫得進回歸測試。規則沒有因此多一份——
/// 伺服器名稱怎麼比仍然只有 <see cref="IsSameServer(string?, string?)"/>。
/// </remarks>
internal sealed partial class SqlSearchCatalogs
{
    /// <summary>
    /// 兩個連線字串裡的伺服器名稱指的是同一台；任何一邊說不出名字就不是。
    /// </summary>
    /// <remarks>
    /// 伺服器名稱的寫法<b>全專案只有這一份</b>：下拉「同一台不列兩次」、導航「樹上是哪一台」、
    /// 移至定義「能不能沿用查詢視窗那條連線」與目錄「範圍還在不在那一台」都走這裡。
    /// 比的是連線字串裡的名稱，不是快取鍵——快取鍵是整串正規化過的連線字串，同一台伺服器的
    /// 兩條連線幾乎不會相等。各寫一次的症狀是下拉少列一台，導航卻說物件總管上沒有它。
    ///
    /// 空名稱一律不相等：說不出是哪一台時，答「同一台」就是替使用者猜。
    /// </remarks>
    public static bool IsSameServer(string? serverName, string? otherServerName) =>
        otherServerName is { Length: > 0 } &&
        string.Equals(serverName, otherServerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>物件總管樹上這一台，就是那個查詢視窗連著的伺服器。</summary>
    public static bool IsSameServer(SsmsObjectExplorerServer server, string? editorServerName) =>
        IsSameServer(server.ServerName, editorServerName);

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

        if (named is not null && IsSameServer(named, origin.ServerName)) return named;

        foreach (var server in servers ?? Array.Empty<SsmsObjectExplorerServer>())
        {
            if (IsSameServer(server, origin.ServerName)) return server;
        }

        return null;
    }

    /// <summary>範圍已經不在這一筆那一台時的那一句；移至定義、物件總管與預覽共用。</summary>
    public static string ElsewhereNotice(SqlSearchOrigin origin) =>
        $"這一筆是在 {origin} 上搜到的，而搜尋範圍已經換到別台；重新搜尋之後再試一次。";
}
