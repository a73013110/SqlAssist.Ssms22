namespace SqlAssist.Core.Notifications;

/// <summary>「關於與診斷」的通知測試有哪幾種。</summary>
public enum NotificationRehearsalScenario
{
    Success,
    Failure,
    Repeats,
    Prompt,
    PromptStack,
    PromptWithActivity,
}
