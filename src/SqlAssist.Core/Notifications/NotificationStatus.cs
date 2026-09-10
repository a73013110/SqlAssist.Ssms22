namespace SqlAssist.Core.Notifications;

/// <summary>通知的生命週期結果。</summary>
/// <remarks>
/// <see cref="Degraded"/> 介於成功與失敗之間：主要結果拿到了，但有一部分資料取不到。
/// 中繼資料查詢本來就會降級，沒有這一階就只能在「整項失敗」與「假裝成功」之間二選一。
/// </remarks>
public enum NotificationStatus
{
    Running,
    Succeeded,
    Degraded,
    Failed,
    Canceled,
}
