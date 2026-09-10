using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Notifications;

/// <summary>只決定通知可見度，不停用工作或刪除診斷紀錄。</summary>
public static class NotificationVisibility
{
    /// <summary>詳細度要到哪一階才上畫面。</summary>
    public static NotificationLevel Threshold(NotificationVerbosity verbosity) => verbosity switch
    {
        NotificationVerbosity.Quiet => NotificationLevel.Notice,
        NotificationVerbosity.Verbose => NotificationLevel.Debug,
        NotificationVerbosity.All => NotificationLevel.Trace,
        _ => NotificationLevel.Info,
    };

    public static bool Includes(NotificationItem item, SqlAssistSettings settings)
    {
        if (!settings.Enabled || !settings.NotificationEnabled) return false;
        // 失敗與降級是獨立通道；即使高頻種類隱藏，它們仍可被看見。
        if (item.Status == NotificationStatus.Failed) return settings.NotificationFailures;
        if (item.Status == NotificationStatus.Degraded) return settings.NotificationDegraded;
        // 種類開關排在來源之前：關掉「程式碼片段」就是不想再看到展開片段的提示，
        // 而那件事永遠由使用者觸發。放在 User 之後的話那幾格永遠按不動。
        if (!settings.NotificationKinds[item.Kind]) return false;
        // Trace 是診斷統計用的量，量大到會把畫面洗掉；只有明選「全部」才放行，
        // 連使用者剛觸發的也一樣，否則「全部」與「詳細」就沒有分別。
        if (item.Level == NotificationLevel.Trace && settings.NotificationVerbosity != NotificationVerbosity.All) return false;
        // 使用者剛按下去的事跨得過降噪門檻；種類開關已經在上面問過了。
        if (item.Origin == NotificationOrigin.User) return true;
        // Notice 是「值得知道」，同樣跨過門檻。
        if (item.Level == NotificationLevel.Notice) return true;
        return item.Level >= Threshold(settings.NotificationVerbosity);
    }
}
