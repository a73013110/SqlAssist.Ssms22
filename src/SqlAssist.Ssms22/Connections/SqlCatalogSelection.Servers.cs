using System;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// <see cref="SqlCatalogSelection"/> 的純判斷那一半：兩個伺服器名稱是不是同一台。
/// </summary>
/// <remarks>
/// 與 <c>SqlCatalogSelection.cs</c> 是同一個類別的兩半，切開只有一個理由：這一半零 VS／SSMS
/// 相依，比對規則才寫得進回歸測試。
/// </remarks>
internal sealed partial class SqlCatalogSelection
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
}
