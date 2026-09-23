using System;
using System.Windows;
using SqlAssist.Ssms22.Notifications;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

public sealed class NotificationPlacementTests
{
    [Fact]
    public void 錨在右下角狀態列上方()
    {
        var owner = new Rect(100, 50, 1600, 900);
        var placed = NotificationPlacement.Place(owner, 24, 1, new Size(240, 32));
        Assert.Equal(owner.Right - 16, placed.Right);
        Assert.Equal(owner.Bottom - 24 - 12, placed.Bottom);
        Assert.Equal(new Size(240, 32), placed.Size);
    }

    [Fact]
    public void 抓不到狀態列時用二十八DIP()
    {
        var owner = new Rect(0, 0, 1200, 800);
        foreach (double? missing in new double?[] { null, 0, -1 })
            Assert.Equal(800 - 28 - 12, NotificationPlacement.Place(owner, missing, 1, new Size(160, 32)).Bottom);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 邊距與尺寸依DPI換算成裝置像素(double scale)
    {
        var owner = new Rect(-1920, 0, 1920, 1080);
        var placed = NotificationPlacement.Place(owner, null, scale, new Size(320, 120));
        Assert.Equal(owner.Right - Math.Round(16 * scale), placed.Right);
        Assert.Equal(owner.Bottom - Math.Round(40 * scale), placed.Bottom);
        Assert.Equal(Math.Round(320 * scale), placed.Width);
        Assert.Equal(Math.Round(120 * scale), placed.Height);
    }

    [Fact]
    public void 往左上長右下角不動()
    {
        var owner = new Rect(0, 0, 1200, 800);
        var capsule = NotificationPlacement.Place(owner, 28, 1, new Size(160, 32));
        var expanded = NotificationPlacement.Place(owner, 28, 1, new Size(360, 260));
        Assert.Equal(capsule.BottomRight, expanded.BottomRight);
        Assert.True(expanded.Left < capsule.Left && expanded.Top < capsule.Top);
    }

    [Fact]
    public void 擁有者太小時不跑出擁有者之外()
    {
        var owner = new Rect(10, 20, 200, 120);
        var placed = NotificationPlacement.Place(owner, 28, 1, new Size(320, 240));
        Assert.True(placed.Left >= owner.Left && placed.Top >= owner.Top);
        Assert.True(placed.Right <= owner.Right && placed.Bottom <= owner.Bottom);
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationPlacement.Place(owner, 28, 0, new Size(1, 1)));
    }
}
