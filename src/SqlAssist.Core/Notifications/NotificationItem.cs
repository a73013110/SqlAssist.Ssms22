using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Notifications;

/// <summary>不可變的通知快照；不保存例外、連線字串或編輯器。</summary>
public sealed class NotificationItem
{
    internal NotificationItem(long id, string title, string subject, string document, string source,
        DateTimeOffset started, NotificationStatus status, DateTimeOffset? finished, string message,
        NotificationKind kind, NotificationOrigin origin, NotificationLevel level, int repeat = 1,
        string key = "", IReadOnlyList<NotificationAction>? actions = null, NotificationSeverity? severity = null)
    {
        Id = id; Title = title; Subject = subject; Document = document; Source = source; Started = started;
        Status = status; Finished = finished; Message = message; Kind = kind; Origin = origin; Level = level;
        Repeat = repeat; Key = key; Actions = actions ?? NoActions; _severity = severity;
    }

    private static readonly IReadOnlyList<NotificationAction> NoActions = Array.AsReadOnly(new NotificationAction[0]);
    private readonly NotificationSeverity? _severity;

    public long Id { get; }

    /// <summary>動詞開頭的常數短語；物件名稱放 <see cref="Subject"/>，不在這裡組字串。</summary>
    public string Title { get; }

    /// <summary>這件事作用在哪個物件（限定名稱、伺服器、片段名稱）。</summary>
    public string Subject { get; }

    /// <summary>
    /// 從哪一份文件發起；沒有檔名時是穩定的編輯區編號，拿不到編輯器的工作留空。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Source"/> 分成兩格，是因為抬頭那一行要回答「這一批是在哪個文件上
    /// 發生的」。兩者共用一格時，一列資料庫名或一列根本沒有來源的工作就會讓那一行
    /// 比不相等而整個收掉——畫面上看起來是檔名時有時無。
    /// </remarks>
    public string Document { get; }

    /// <summary>資料從哪裡來（資料庫）；不查資料庫的工作留空。</summary>
    public string Source { get; }

    public string Message { get; }
    public NotificationKind Kind { get; }
    public NotificationOrigin Origin { get; }
    public NotificationLevel Level { get; }

    /// <summary>取消是正常生命週期，不是警告；降級才是。提醒的嚴重度由目錄明定。</summary>
    public NotificationSeverity Severity => _severity ?? Status switch
    {
        NotificationStatus.Failed => NotificationSeverity.Error,
        NotificationStatus.Degraded => NotificationSeverity.Warning,
        _ => NotificationSeverity.Info,
    };

    public DateTimeOffset Started { get; }
    public NotificationStatus Status { get; }
    public DateTimeOffset? Finished { get; }

    /// <summary>畫面上這一列代表幾次呼叫；未合併時是 1。</summary>
    /// <remarks>
    /// 只有 <see cref="NotificationMerge"/> 產生的代表列會大於 1，而合併只影響畫面：
    /// 統計與詳細診斷仍逐次計入，看得到重複幾次的地方是工作階段統計，不是這裡。
    /// </remarks>
    public int Repeat { get; }

    /// <summary>提醒的鍵；同一個 (<see cref="Kind"/>、鍵) 只留最新的一則。活動是空字串。</summary>
    public string Key { get; }

    /// <summary>提醒上的按鈕；不是空的就是提醒，不另外加一軸。</summary>
    public IReadOnlyList<NotificationAction> Actions { get; }

    /// <summary>
    /// 需要使用者決定的事：不逾時、不合併，按了按鈕或叉號才消失。
    /// </summary>
    /// <remarks>
    /// 以「有沒有按鈕」判斷而不是另開一個列舉：沒有按鈕的提醒只能等叉號，那就是
    /// 一則不會到期的事件；有按鈕卻會到期的活動則是使用者還沒看到就消失的決定。
    /// 兩種都是錯的，所以這兩件事本來就綁在一起。
    /// </remarks>
    public bool IsPrompt => Actions.Count > 0;

    internal NotificationItem With(NotificationStatus status, DateTimeOffset? finished, string message) =>
        new(Id, Title, Subject, Document, Source, Started, status, finished, message, Kind, Origin, Level, Repeat,
            Key, Actions, _severity);

    internal NotificationItem WithRepeat(int repeat) =>
        new(Id, Title, Subject, Document, Source, Started, Status, Finished, Message, Kind, Origin, Level, repeat,
            Key, Actions, _severity);
}
