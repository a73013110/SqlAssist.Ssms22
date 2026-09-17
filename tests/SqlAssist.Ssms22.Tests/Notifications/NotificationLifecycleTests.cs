using System;
using SqlAssist.Ssms22.Notifications;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

/// <summary>卡片的延遲、最短可見、淡出與到期。</summary>
public sealed class NotificationLifecycleTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 第一次出現要等延遲顯示()
    {
        Assert.Equal(NotificationStep.Hold, Next(items: 1, attached: false, withinDelay: true));
        Assert.Equal(NotificationStep.Show, Next(items: 1, attached: false, withinDelay: false));
    }

    /// <summary>換宿主或新工作加入時，延遲早就付過了。</summary>
    [Fact]
    public void 已經掛著就不再等延遲()
    {
        Assert.Equal(NotificationStep.Show, Next(items: 1, attached: true, withinDelay: true));
    }

    [Fact]
    public void 沒有內容也沒掛著就停下計時器()
    {
        Assert.Equal(NotificationStep.Idle, Next(items: 0, attached: false));
    }

    [Fact]
    public void 最短可見時間內不收()
    {
        var justBefore = Origin + NotificationLifecycle.MinimumVisible - TimeSpan.FromMilliseconds(1);
        Assert.Equal(NotificationStep.Hold, Next(items: 0, attached: true, now: justBefore));
        Assert.Equal(NotificationStep.FadeOut, Next(items: 0, attached: true, now: Origin + NotificationLifecycle.MinimumVisible));
    }

    [Fact]
    public void 淡出播完才收掉()
    {
        var hiding = Origin + TimeSpan.FromSeconds(2);
        Assert.Equal(NotificationStep.Hold, Next(items: 0, attached: true, now: hiding + TimeSpan.FromMilliseconds(219), hidingAt: hiding));
        Assert.Equal(NotificationStep.Retire, Next(items: 0, attached: true, now: hiding + NotificationLifecycle.FadeOutDuration, hidingAt: hiding));
    }

    [Fact]
    public void 關閉動畫時直接收掉()
    {
        Assert.Equal(NotificationStep.Retire, Next(items: 0, attached: true, now: Origin + TimeSpan.FromSeconds(1), motion: false));
    }

    /// <summary>淡出途中加入的工作從目前狀態接續，不先收掉再重新入場。</summary>
    [Fact]
    public void 淡出途中有新工作就接續顯示()
    {
        var hiding = Origin + TimeSpan.FromSeconds(2);
        Assert.Equal(NotificationStep.Show, Next(items: 1, attached: true, now: hiding + TimeSpan.FromMilliseconds(100), hidingAt: hiding, withinDelay: true));
    }

    private static NotificationStep Next(int items, bool attached, DateTimeOffset? now = null,
        DateTimeOffset? hidingAt = null, bool motion = true, bool withinDelay = false) =>
        NotificationLifecycle.Next(items, attached, now ?? Origin, Origin, hidingAt, motion, withinDelay);
}
