namespace SqlAssist.Core.Notifications;

/// <summary>工作階段統計的排序依據。</summary>
/// <remarks>
/// 兩個問題不同：次數回答「哪些動作在重複」，總耗時回答「時間花在哪」。
/// 一次只按其中一個排，兩者都排不出來的組合不存在。
/// </remarks>
public enum NotificationDigestOrder
{
    /// <summary>呼叫次數多的排前面。</summary>
    Count,
    /// <summary>總耗時長的排前面。</summary>
    TotalElapsed,
}
