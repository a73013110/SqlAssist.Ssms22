using System;
using SqlAssist.Ssms22.Editor;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Editor;

/// <summary>
/// 換編輯區時哪一次算「新出現」。
/// </summary>
/// <remarks>
/// 每個編輯區各一張卡片的版本，F12 開新查詢視窗會把同一份提示整個重建，於是滑入、
/// 淡入與每一列的狀態動畫全部重播——看起來就是提示消失了又跳出來。這裡守的是那件事
/// 的判斷本身，卡片與 adornment 層的搬移在 <c>NotificationSurface</c>。
/// </remarks>
public sealed class NotificationHandoverTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 第一次掛上就是新出現()
    {
        Assert.True(new NotificationHandover().Attach(attached: false, Origin));
    }

    [Fact]
    public void 直接從別的編輯區接手不算新出現()
    {
        Assert.False(new NotificationHandover().Attach(attached: true, Origin));
    }

    [Fact]
    public void 拔下來之後很快有人接手仍算同一次交接()
    {
        var handover = new NotificationHandover();
        handover.Detach(Origin, retire: false);
        Assert.False(handover.Attach(attached: false, Origin + TimeSpan.FromMilliseconds(120)));
    }

    /// <summary>交接只算一次；同一次拔下來不能讓後面每一批都少掉入場動畫。</summary>
    [Fact]
    public void 交接用掉之後下一批照樣是新出現()
    {
        var handover = new NotificationHandover();
        handover.Detach(Origin, retire: false);
        Assert.False(handover.Attach(attached: false, Origin + TimeSpan.FromMilliseconds(120)));
        Assert.True(handover.Attach(attached: false, Origin + TimeSpan.FromMilliseconds(240)));
    }

    [Fact]
    public void 隔太久沒有人接手就是新出現()
    {
        var handover = new NotificationHandover();
        handover.Detach(Origin, retire: false);
        Assert.True(handover.Attach(attached: false, Origin + NotificationHandover.Window));
    }

    /// <summary>到期、關閉與停用是這一批結束了，不是交接。</summary>
    [Fact]
    public void 這一批結束後不吃交接寬限()
    {
        var handover = new NotificationHandover();
        handover.Detach(Origin, retire: true);
        Assert.True(handover.Attach(attached: false, Origin + TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void 套件卸載後下一次是新出現()
    {
        var handover = new NotificationHandover();
        handover.Detach(Origin, retire: false);
        handover.Reset();
        Assert.True(handover.Attach(attached: false, Origin + TimeSpan.FromMilliseconds(10)));
    }
}
