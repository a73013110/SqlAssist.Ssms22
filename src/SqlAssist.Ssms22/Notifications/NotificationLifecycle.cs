using System;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>一輪刷新之後通知島要做的事。</summary>
internal enum NotificationStep
{
    /// <summary>沒有東西要顯示、島嶼也不在畫面上：停掉計時器。</summary>
    Idle,

    /// <summary>計時器繼續跑，這一輪什麼都不動（最短可見、淡出途中、延遲顯示）。</summary>
    Hold,

    /// <summary>開始收場。</summary>
    FadeOut,

    /// <summary>這一批結束：停掉計時器，下一批重新播出現動畫。</summary>
    Retire,

    /// <summary>更新內容並顯示在擁有者上。</summary>
    Show,
}

/// <summary>
/// 活動的顯示期限：延遲、最短可見、收場與到期。
/// </summary>
/// <remarks>
/// 與 <see cref="NotificationIslandController"/> 分開是為了測得到：那一份持有計時器與 WPF
/// 浮層，這裡只有時間與旗標。「擁有者看不到」與「通知停用」在呼叫之前就分流，因為那兩條
/// 不能去問通知來源——一問就會跑到期清理，把還沒看到的結果收掉。
/// </remarks>
internal static class NotificationLifecycle
{
    /// <summary>看得見之後至少留這麼久，免得一閃而過。</summary>
    internal static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(800);

    /// <summary>收場的長度；與表面的淡出共用 <see cref="NotificationMotion.Exit"/>。</summary>
    internal static readonly TimeSpan FadeOutDuration = NotificationMotion.Duration(NotificationMotion.Exit);

    /// <param name="items">這一輪看得見的列數。</param>
    /// <param name="attached">這一批目前在畫面上。</param>
    /// <param name="visibleAt">這一批從什麼時候開始看得到。</param>
    /// <param name="hidingAt">淡出從什麼時候開始；沒有在淡出時是 null。</param>
    /// <param name="motion">動畫開著。</param>
    /// <param name="withinDelay">這一批都還在延遲顯示的時間內。</param>
    internal static NotificationStep Next(int items, bool attached, DateTimeOffset now,
        DateTimeOffset visibleAt, DateTimeOffset? hidingAt, bool motion, bool withinDelay)
    {
        if (items > 0)
            // 已經在畫面上的話延遲早就付過了；這裡擋的是這一批第一次出現。
            return !attached && withinDelay ? NotificationStep.Hold : NotificationStep.Show;
        if (!attached) return NotificationStep.Idle;
        if (now - visibleAt < MinimumVisible) return NotificationStep.Hold;
        if (!motion) return NotificationStep.Retire;
        if (hidingAt is not { } hiding) return NotificationStep.FadeOut;
        return now - hiding >= FadeOutDuration ? NotificationStep.Retire : NotificationStep.Hold;
    }
}
