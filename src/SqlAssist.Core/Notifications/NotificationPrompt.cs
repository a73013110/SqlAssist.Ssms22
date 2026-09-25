using System;
using System.ComponentModel;
using System.Collections.Generic;

namespace SqlAssist.Core.Notifications;

/// <summary>一則提醒的內容：鍵、措辭、嚴重度與按鈕。</summary>
/// <remarks>
/// 只由 <see cref="NotificationCatalog"/> 建立，呼叫端不自己組標題、訊息或按鈕標籤；
/// 三軸與出處仍由呼叫端交給 <see cref="NotificationCenter.Prompt"/> 明寫。
/// </remarks>
public sealed class NotificationPrompt
{
    internal NotificationPrompt(string key, string title, string message, NotificationSeverity severity,
        params NotificationAction[] actions)
        : this(key, NotificationTitle.Resolve(title), () => message ?? "", severity, actions)
    {
    }

    /// <summary>目錄用：標題與訊息每次取值都用目前的語言，換語言後顯示中的提醒可以重畫。</summary>
    internal NotificationPrompt(string key, Func<string> title, Func<string> message, NotificationSeverity severity,
        params NotificationAction[] actions)
        : this(key, NotificationTitle.Live(title), message, severity, actions)
    {
    }

    [Localizable(false)]
    private NotificationPrompt(string key, NotificationTitle title, Func<string> message, NotificationSeverity severity,
        NotificationAction[] actions)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("提醒需要鍵。", nameof(key));
        if (string.IsNullOrWhiteSpace(title.Text)) throw new ArgumentException("提醒需要標題。", nameof(title));
        if (actions is null || actions.Length == 0)
            throw new ArgumentException("提醒至少要有一顆按鈕；沒有要決定的事就用 Post。", nameof(actions));
        var primary = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (action is null) throw new ArgumentException("按鈕不得為 null。", nameof(actions));
            if (!ids.Add(action.Id)) throw new ArgumentException("同一則提醒的按鈕識別字重複：" + action.Id, nameof(actions));
            if (action.Role == NotificationActionRole.Primary) primary++;
        }

        // 兩顆主要動作等於沒有主要動作：使用者分不出哪一顆是建議的那一條路。
        if (primary > 1) throw new ArgumentException("每則提醒最多一個主要動作。", nameof(actions));
        Key = key; TitleSource = title; _message = message ?? throw new ArgumentNullException(nameof(message));
        Severity = severity;
        Actions = Array.AsReadOnly((NotificationAction[])actions.Clone());
    }

    /// <summary>同一個 (<see cref="NotificationKind"/>、鍵) 只留最新的一則。</summary>
    public string Key { get; }

    public string Title => TitleSource.Text;
    public string Message => _message() ?? "";

    internal NotificationTitle TitleSource { get; }

    private readonly Func<string> _message;
    public NotificationSeverity Severity { get; }
    public IReadOnlyList<NotificationAction> Actions { get; }
}
