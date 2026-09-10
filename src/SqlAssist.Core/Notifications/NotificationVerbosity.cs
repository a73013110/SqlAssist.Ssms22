namespace SqlAssist.Core.Notifications;

/// <summary>使用者選的詳細度；決定 <see cref="NotificationLevel"/> 要到哪一階才上畫面。</summary>
/// <remarks>
/// 四個值對上 <see cref="NotificationLevel"/> 的四階，門檻以下的工作只進診斷統計。
/// 使用者剛觸發的事與失敗、降級不受這個門檻管轄。
/// </remarks>
public enum NotificationVerbosity
{
    /// <summary>只留下值得知道的事；門檻是 <see cref="NotificationLevel.Notice"/>。</summary>
    Quiet,
    /// <summary>各項工作；門檻是 <see cref="NotificationLevel.Info"/>。</summary>
    Normal,
    /// <summary>連逐次的細節工作都顯示；門檻是 <see cref="NotificationLevel.Debug"/>。</summary>
    Verbose,
    /// <summary>連只進診斷統計的 <see cref="NotificationLevel.Trace"/> 也顯示。</summary>
    All,
}
