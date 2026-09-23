using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 「查詢視窗的連線」在每一處怎麼說；SQL Search、SQL Memory 與收藏編輯窗共用一份。
/// </summary>
/// <remarks>
/// 一律叫「查詢視窗」，不叫「目前連線」：兩個工具窗、兩個下拉與一顆按鈕說的是同一件事，
/// 而「目前連線」在指名了物件總管上一台伺服器之後，讀起來像是指那一台。各處各寫一句的症狀是
/// 使用者在 SQL Memory 看到「目前連線」、在 SQL Search 看到「查詢視窗」，以為是兩個不同的東西。
/// </remarks>
internal static class SqlEditorConnectionText
{
    public const string Name = "查詢視窗";

    /// <summary>查詢視窗沒有連線時括號裡的字。</summary>
    /// <remarks>
    /// 摘要一定說得出跟著的是哪一個，或根本沒連上。只寫「查詢視窗」的症狀是使用者盯著一顆
    /// 看起來正常的按鈕，而下面那一句是「尚未連線」——他分不出是範圍選錯了還是真的沒連。
    /// </remarks>
    public const string NotConnected = "未連線";

    /// <summary>範圍按鈕左邊那一顆的名稱（無障礙名稱與 Tooltip 的第一句）。</summary>
    public const string ApplyAction = "套用查詢視窗的連線";

    /// <summary>查詢視窗沒有完整連線時，按下那一顆之後回報的那一句；兩邊都保留原本的範圍。</summary>
    public const string NotConnectedReport = "查詢視窗目前沒有連線；範圍保持不變。先在查詢視窗連上資料庫再按一次。";

    /// <summary><c>查詢視窗（名稱）</c>；沒有名稱時是 <c>查詢視窗（未連線）</c>。摘要與下拉那一列共用。</summary>
    public static string Label(string? name) =>
        Name + "（" + (name is { Length: > 0 } ? name : NotConnected) + "）";

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
