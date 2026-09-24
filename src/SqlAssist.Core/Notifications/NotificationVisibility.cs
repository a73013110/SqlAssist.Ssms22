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
        // 提醒是要使用者決定的事，不是降噪的對象：詳細度、來源與失敗／降級通道都不管它，
        // 否則「安靜」詳細度就會安靜地吃掉「有新版」這種一年幾次的決定。種類開關仍然有效。
        if (item.IsPrompt) return KindEnabled(item.Kind, settings);
        // 失敗與降級是獨立通道；即使高頻種類隱藏，它們仍可被看見。
        if (item.Status == NotificationStatus.Failed) return settings.NotificationFailures;
        if (item.Status == NotificationStatus.Degraded) return settings.NotificationDegraded;
        // 種類開關排在來源之前：關掉「程式碼片段」就是不想再看到展開片段的提示，
        // 而那件事永遠由使用者觸發。放在 User 之後的話那幾格永遠按不動。
        if (!KindEnabled(item.Kind, settings)) return false;
        // Trace 是診斷統計用的量，量大到會把畫面洗掉；只有明選「全部」才放行，
        // 連使用者剛觸發的也一樣，否則「全部」與「詳細」就沒有分別。
        if (item.Level == NotificationLevel.Trace && settings.NotificationVerbosity != NotificationVerbosity.All) return false;
        // 使用者剛按下去的事跨得過降噪門檻；種類開關已經在上面問過了。
        if (item.Origin == NotificationOrigin.User) return true;
        // Notice 是「值得知道」，同樣跨過門檻。
        if (item.Level == NotificationLevel.Notice) return true;
        return item.Level >= Threshold(settings.NotificationVerbosity);
    }

    /// <summary>沒有開關的種類（更新檢查、通知測試）不在這一步被擋。</summary>
    private static bool KindEnabled(NotificationKind kind, SqlAssistSettings settings) =>
        !NotificationKindToggle.Governs(kind) || settings.NotificationKinds[kind];
}
