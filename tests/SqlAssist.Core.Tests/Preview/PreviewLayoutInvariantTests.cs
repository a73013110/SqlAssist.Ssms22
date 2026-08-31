using System;
using SqlAssist.Core.Preview;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Preview;

public sealed class PreviewLayoutInvariantTests
{
    [Fact]
    public void 合法輸入的結果永遠有限且位於文件界內()
    {
        foreach (var width in new[] { 240.0, 320, 640, 1200, 2200 })
        foreach (var height in new[] { 160.0, 240, 500, 900 })
        foreach (var anchorRatio in new[] { 0.0, 0.25, 0.5, 0.9, 1.0 })
        foreach (var placement in new[] { SqlPreviewPlacement.Stacked, SqlPreviewPlacement.Beside })
        {
            var document = new PreviewRectangle(-700, 40, width, height);
            var anchorLeft = document.Left + Math.Max(0, document.Width - 20) * anchorRatio;
            var anchor = new PreviewRectangle(anchorLeft, document.Top + 30, 20, 18);
            var completion = new PreviewRectangle(
                Math.Min(anchor.Left, document.Right - Math.Min(280, document.Width)),
                anchor.Bottom,
                Math.Min(280, document.Width),
                Math.Min(180, Math.Max(1, document.Bottom - anchor.Bottom)));
            var result = PreviewPlacementEngine.Calculate(
                new PreviewLayoutRequest
                {
                    Placement = placement,
                    AvailableBounds = document,
                    Anchor = anchor,
                    Obstacles = new[] { anchor, completion },
                    DesiredWidth = 620,
                    DesiredHeight = 420,
                    MinimumWidth = 320,
                    MinimumHeight = 180,
                    MaximumWidth = 2000,
                    MaximumHeight = 1400,
                    StretchStackedWidth = placement == SqlPreviewPlacement.Stacked,
                    Gap = 4
                });

            AssertFinite(result.Bounds);
            Assert.True(result.Bounds.Left >= document.Left - 0.001);
            Assert.True(result.Bounds.Top >= document.Top - 0.001);
            Assert.True(result.Bounds.Right <= document.Right + 0.001);
            Assert.True(result.Bounds.Bottom <= document.Bottom + 0.001);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 所有幾何同倍率轉成實體像素後邏輯結果不變(double scale)
    {
        var result = PreviewPlacementEngine.Calculate(
            new PreviewLayoutRequest
            {
                Placement = SqlPreviewPlacement.Stacked,
                AvailableBounds = Scale(new PreviewRectangle(100, 80, 1200, 760), scale),
                Anchor = Scale(new PreviewRectangle(560, 100, 60, 20), scale),
                Obstacles = new[] { Scale(new PreviewRectangle(600, 120, 320, 200), scale) },
                DesiredWidth = 620 * scale,
                DesiredHeight = 420 * scale,
                MinimumWidth = 320 * scale,
                MinimumHeight = 180 * scale,
                MaximumWidth = 2000 * scale,
                MaximumHeight = 1400 * scale,
                Gap = 4 * scale
            });

        Assert.Equal(560, result.Bounds.Left / scale, 6);
        Assert.Equal(324, result.Bounds.Top / scale, 6);
        Assert.Equal(620, result.Bounds.Width / scale, 6);
        Assert.Equal(420, result.Bounds.Height / scale, 6);
    }

    [Fact]
    public void 非數值錨點不會產生傳給Wpf的壞矩形()
    {
        var result = PreviewPlacementEngine.Calculate(
            new PreviewLayoutRequest
            {
                Placement = SqlPreviewPlacement.Stacked,
                AvailableBounds = new PreviewRectangle(0, 0, 1000, 800),
                Anchor = new PreviewRectangle(double.NaN, 20, 10, 10),
                DesiredWidth = 620,
                DesiredHeight = 420,
                MinimumWidth = 320,
                MinimumHeight = 180
            });

        Assert.True(result.Bounds.IsEmpty);
        AssertFinite(result.Bounds);
    }

    [Fact]
    public void 縮放收到NaN位移時視為沒有移動()
    {
        var initial = new PreviewRectangle(100, 100, 620, 420);
        var result = PreviewResizeEngine.Resize(
            initial,
            PreviewResizeCorner.BottomRight,
            double.NaN,
            double.NaN,
            new PreviewRectangle(0, 0, 1200, 900),
            320,
            180,
            2000,
            1400);

        Assert.Equal(initial, result);
    }

    private static PreviewRectangle Scale(PreviewRectangle rectangle, double scale) =>
        new(
            rectangle.Left * scale,
            rectangle.Top * scale,
            rectangle.Width * scale,
            rectangle.Height * scale);

    private static void AssertFinite(PreviewRectangle rectangle)
    {
        Assert.False(double.IsNaN(rectangle.Left));
        Assert.False(double.IsNaN(rectangle.Top));
        Assert.False(double.IsNaN(rectangle.Width));
        Assert.False(double.IsNaN(rectangle.Height));
        Assert.False(double.IsInfinity(rectangle.Left));
        Assert.False(double.IsInfinity(rectangle.Top));
        Assert.False(double.IsInfinity(rectangle.Width));
        Assert.False(double.IsInfinity(rectangle.Height));
    }
}
