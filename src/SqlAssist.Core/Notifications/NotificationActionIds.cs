namespace SqlAssist.Core.Notifications;

/// <summary>
/// 提醒按鈕的穩定識別字；呼叫端依它處理並保存決定，詳細診斷也只寫它。
/// </summary>
/// <remarks>
/// 與 <see cref="NotificationCatalog"/> 分開：目錄裡的常數欄位是標題，要守標題的文案契約；
/// 這裡是給程式比對的鍵，改了要連同已經落地的決定一起想，不是改措辭。
/// </remarks>
public static class NotificationActionIds
{
    /// <summary>下載新版；參數是 GitHub 發行頁網址。</summary>
    public const string UpdateDownload = "update.download";

    /// <summary>略過這一版；參數是那個版本號，呼叫端要落地保存。</summary>
    public const string UpdateSkip = "update.skip";

    /// <summary>打開 SQL Memory 的用量與維護分頁。</summary>
    public const string SqlMemoryOpenMaintenance = "sqlmemory.open-maintenance";

    /// <summary>打開 SQL Memory 工具窗。</summary>
    public const string SqlMemoryOpen = "sqlmemory.open";

    /// <summary>收起「關於與診斷」送出的測試提醒；沒有別的作用。</summary>
    public const string RehearsalAcknowledge = "rehearsal.acknowledge";
}
