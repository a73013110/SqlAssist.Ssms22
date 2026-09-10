using System;

namespace SqlAssist.Core.Notifications;

/// <summary>不可變的通知快照；不保存例外、連線字串或編輯器。</summary>
public sealed class NotificationItem
{
    internal NotificationItem(long id, string title, string subject, string context, DateTimeOffset started,
        NotificationStatus status, DateTimeOffset? finished, string message,
        NotificationKind kind, NotificationOrigin origin, NotificationLevel level, int repeat = 1)
    {
        Id = id; Title = title; Subject = subject; Context = context; Started = started;
        Status = status; Finished = finished; Message = message; Kind = kind; Origin = origin; Level = level;
        Repeat = repeat;
    }

    public long Id { get; }

    /// <summary>動詞開頭的常數短語；物件名稱放 <see cref="Subject"/>，不在這裡組字串。</summary>
    public string Title { get; }

    /// <summary>這件事作用在哪個物件（限定名稱、伺服器、片段名稱）。</summary>
    public string Subject { get; }

    /// <summary>從哪個文件或資料庫發起。</summary>
    public string Context { get; }

    public string Message { get; }
    public NotificationKind Kind { get; }
    public NotificationOrigin Origin { get; }
    public NotificationLevel Level { get; }

    /// <summary>取消是正常生命週期，不是警告；降級才是。</summary>
    public NotificationSeverity Severity => Status switch
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

    internal NotificationItem With(NotificationStatus status, DateTimeOffset? finished, string message) =>
        new(Id, Title, Subject, Context, Started, status, finished, message, Kind, Origin, Level, Repeat);

    internal NotificationItem WithRepeat(int repeat) =>
        new(Id, Title, Subject, Context, Started, Status, Finished, Message, Kind, Origin, Level, repeat);
}
