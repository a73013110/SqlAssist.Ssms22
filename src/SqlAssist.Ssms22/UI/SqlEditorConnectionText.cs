using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 「查詢視窗的連線」在每一處怎麼說；SQL Search、SQL Memory 與收藏編輯窗共用一份。
/// </summary>
/// <remarks>
/// 一律叫「查詢視窗」，不叫「目前連線」：選了物件總管上一台伺服器之後，「目前連線」讀起來像是
/// 指那一台。範圍列上沒有「查詢視窗」這個選項，查詢視窗只出現在這顆一次性的套用按鈕上。
/// </remarks>
internal static class SqlEditorConnectionText
{
    /// <summary>範圍按鈕左邊那一顆的名稱（無障礙名稱與 Tooltip 的第一句）。</summary>
    public const string ApplyAction = "套用查詢視窗的連線";

    /// <summary>查詢視窗沒有完整連線時，按下那一顆之後回報的那一句；兩邊都保留原本的範圍。</summary>
    public const string NotConnectedReport = "查詢視窗目前沒有連線；範圍保持不變。先在查詢視窗連上資料庫再按一次。";

    /// <summary>按鈕的 Tooltip：按下去範圍會換成哪一條連線。</summary>
    /// <remarks>
    /// 在打開那一刻才問（見 <see cref="SqlAssistChrome.CreateEditorConnectionButton"/>）：
    /// 寫死一句「套用目前連線」的那一版，使用者要按下去才知道會套到哪一台。
    /// </remarks>
    public static string ApplyToolTip(SqlConnectionLabel? connection) =>
        connection is { Server.Length: > 0, Database.Length: > 0 }
            ? ApplyAction + "：" + connection.Server + " · " + connection.Database + "。只改範圍，不切換 SSMS 的連線。"
            : ApplyAction + "：查詢視窗目前沒有連線。";
}
