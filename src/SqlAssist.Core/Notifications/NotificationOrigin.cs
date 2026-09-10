namespace SqlAssist.Core.Notifications;

/// <summary>誰觸發了這件事；決定它能不能被種類開關關掉。</summary>
public enum NotificationOrigin
{
    /// <summary>使用者剛按下去的動作，一律顯示。</summary>
    User,
    /// <summary>打字過程中自動發生的高頻工作。</summary>
    Typing,
    /// <summary>沒有人要求的背景維護：預載、預熱、重新確認。</summary>
    Ambient,
    /// <summary>套件或工作階段啟動。</summary>
    Startup,
}
