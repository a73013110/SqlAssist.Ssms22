namespace SqlAssist.Core.Notifications;

/// <summary>通知屬於哪個子系統；只回答「在做什麼」，觸發來源見 <see cref="NotificationOrigin"/>。</summary>
/// <remarks>
/// 兩軸分開之前，同一個列舉同時放子系統與觸發來源，結果是使用者操作觸發的中繼資料查詢
/// 被歸成「使用者操作」，「中繼資料載入」開關永遠看不到它。
/// </remarks>
public enum NotificationKind
{
    Metadata,
    Completion,
    Analysis,
    Preview,
    Editing,
    Navigation,
    Results,
    Snippets,
    Settings,
    Package,
    /// <summary>還沒分類的工作。預設顯示，否則新工作會靜默漏掉。</summary>
    Unclassified,
}
