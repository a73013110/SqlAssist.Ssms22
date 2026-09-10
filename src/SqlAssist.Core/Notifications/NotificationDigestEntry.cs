using System;

namespace SqlAssist.Core.Notifications;

/// <summary>工作階段統計的一列：同一件事在這次工作階段累計的次數與耗時。</summary>
public sealed class NotificationDigestEntry
{
    internal NotificationDigestEntry(NotificationKind kind, string title, string subject, int count,
        TimeSpan total, TimeSpan max, int failed, int degraded, bool isOther)
    {
        Kind = kind; Title = title; Subject = subject; Count = count;
        Total = total; Max = max; Failed = failed; Degraded = degraded; IsOther = isOther;
    }

    public NotificationKind Kind { get; }
    public string Title { get; }

    /// <summary>作用的物件；合併成「其他」那一列時為空。</summary>
    public string Subject { get; }

    /// <summary>呼叫次數；通知隱藏、快取命中與畫面上被合併的重複都算在內。</summary>
    public int Count { get; }

    public TimeSpan Total { get; }

    /// <summary>平均耗時；由 <see cref="Total"/> 與 <see cref="Count"/> 推導，不另存一份。</summary>
    public TimeSpan Average => Count == 0 ? TimeSpan.Zero : new TimeSpan(Total.Ticks / Count);

    public TimeSpan Max { get; }
    public int Failed { get; }
    public int Degraded { get; }

    /// <summary>超出統計上限後併進來的那一列；種類混雜，排序時固定在最後。</summary>
    public bool IsOther { get; }
}
