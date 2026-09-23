namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 物件總管目前連著的一台 SQL Server。
/// </summary>
/// <remarks>
/// 只留識別用的字串，<b>不留連線也不留任何祕密</b>：要開連線時再回頭問物件總管。
/// 這個值會進 UI 的下拉選項與按鈕摘要，留著連線等於把密碼綁在一個畫面物件上。
/// </remarks>
internal sealed class SsmsObjectExplorerServer
{
    internal SsmsObjectExplorerServer(string displayName, string serverName, string rootUrn)
    {
        DisplayName = displayName;
        ServerName = serverName;
        RootUrn = rootUrn;
    }

    /// <summary>物件總管樹上顯示的那一個名稱；使用者是照這個名字認伺服器的。</summary>
    public string DisplayName { get; }

    /// <summary>連線字串裡那個伺服器名稱；用來與查詢視窗那一條連線比對。</summary>
    public string ServerName { get; }

    /// <summary>根節點的 URN；回頭向物件總管要連線時就是靠它。</summary>
    public string RootUrn { get; }
}
