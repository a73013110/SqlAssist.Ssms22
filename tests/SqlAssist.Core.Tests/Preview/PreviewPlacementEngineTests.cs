using System;
using System.Collections.Generic;
using SqlAssist.Core.Preview;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Preview;

public sealed class PreviewPlacementEngineTests
{
    private static readonly PreviewRectangle Document = new(100, 80, 1200, 760);
    private static readonly PreviewRectangle Anchor = new(560, 100, 60, 20);
    private static readonly PreviewRectangle Completion = new(600, 120, 320, 200);

    [Fact]
    public void 結果窗縮小文字Viewport時仍以完整文件區保留偏好高度()
    {
        var result = PreviewPlacementEngine.Calculate(Stacked());

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.Equal(420, result.Bounds.Height);
        Assert.Equal(324, result.Bounds.Top);
        AssertContained(result.Bounds, Document);
    }

    [Fact]
    public void 錨點靠右時平移視窗而非縮小或移出文件()
    {
        var result = PreviewPlacementEngine.Calculate(Stacked(
            anchor: new PreviewRectangle(1240, 100, 40, 20),
            desiredWidth: 620,
            stretch: false,
            obstacles: Array.Empty<PreviewRectangle>()));

        Assert.Equal(620, result.Bounds.Width);
        Assert.Equal(Document.Right, result.Bounds.Right);
        Assert.Equal(680, result.Bounds.Left);
        AssertContained(result.Bounds, Document);
    }

    [Fact]
    public void 上下擺放優先避開建議清單後放在下方()
    {
        var result = PreviewPlacementEngine.Calculate(Stacked());

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.False(result.Bounds.Intersects(Completion));
        Assert.Equal(Completion.Bottom + 4, result.Bounds.Top);
    }

    [Fact]
    public void 上一輪在上方且仍放得下時不因微小重排翻回下方()
    {
        var request = Stacked(
            available: new PreviewRectangle(0, 0, 1200, 1200),
            anchor: new PreviewRectangle(500, 600, 50, 20),
            obstacles: Array.Empty<PreviewRectangle>());
        request = new PreviewLayoutRequest
        {
            Placement = request.Placement,
            AvailableBounds = request.AvailableBounds,
            Anchor = request.Anchor,
            Obstacles = request.Obstacles,
            DesiredWidth = request.DesiredWidth,
            DesiredHeight = request.DesiredHeight,
            MinimumWidth = request.MinimumWidth,
            MinimumHeight = request.MinimumHeight,
            MaximumWidth = request.MaximumWidth,
            MaximumHeight = request.MaximumHeight,
            Gap = request.Gap,
            PreviousSide = PreviewPlacementSide.Above
        };

        var result = PreviewPlacementEngine.Calculate(request);

        Assert.Equal(PreviewPlacementSide.Above, result.Side);
    }

    [Fact]
    public void 下方不足時改放上方且不縮偏好高度()
    {
        var document = new PreviewRectangle(0, 0, 1200, 900);
        var anchor = new PreviewRectangle(500, 700, 50, 20);
        var completion = new PreviewRectangle(500, 720, 300, 160);
        var result = PreviewPlacementEngine.Calculate(Stacked(
            available: document,
            anchor: anchor,
            desiredHeight: 420,
            obstacles: new[] { anchor, completion }));

        Assert.Equal(PreviewPlacementSide.Above, result.Side);
        Assert.Equal(420, result.Bounds.Height);
        Assert.True(result.Bounds.Bottom <= anchor.Top - 4);
    }

    [Fact]
    public void 上下完整高度都放不下時選較大的上方而非最小下方()
    {
        var document = new PreviewRectangle(0, 0, 1200, 608);
        var anchor = new PreviewRectangle(500, 404, 50, 20);
        var result = PreviewPlacementEngine.Calculate(Stacked(
            available: document,
            anchor: anchor,
            desiredHeight: 420,
            obstacles: Array.Empty<PreviewRectangle>()));

        Assert.Equal(PreviewPlacementSide.Above, result.Side);
        Assert.Equal(400, result.Bounds.Height);
        Assert.Equal(anchor.Top - 4, result.Bounds.Bottom);
    }

    [Fact]
    public void 上下都放不下時只縮有效高度且不覆蓋清單()
    {
        var document = new PreviewRectangle(0, 0, 1000, 500);
        var anchor = new PreviewRectangle(400, 120, 50, 20);
        var completion = new PreviewRectangle(400, 140, 300, 200);
        var result = PreviewPlacementEngine.Calculate(Stacked(
            available: document,
            anchor: anchor,
            desiredHeight: 420,
            obstacles: new[] { anchor, completion }));

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.Equal(156, result.Bounds.Height);
        Assert.False(result.Bounds.Intersects(completion));
        AssertContained(result.Bounds, document);
    }

    [Fact]
    public void 自動寬度從錨點延伸到文件右界()
    {
        var result = PreviewPlacementEngine.Calculate(Stacked(
            desiredWidth: 620,
            stretch: true,
            obstacles: Array.Empty<PreviewRectangle>()));

        Assert.Equal(740, result.Bounds.Width);
        Assert.Equal(Anchor.Left, result.Bounds.Left);
        Assert.Equal(Document.Right, result.Bounds.Right);
    }

    [Fact]
    public void 自動寬度仍受絕對最大值限制()
    {
        var result = PreviewPlacementEngine.Calculate(Stacked(
            anchor: new PreviewRectangle(120, 100, 40, 20),
            desiredWidth: 620,
            stretch: true,
            maximumWidth: 800,
            obstacles: Array.Empty<PreviewRectangle>()));

        Assert.Equal(800, result.Bounds.Width);
        Assert.Equal(120, result.Bounds.Left);
    }

    [Fact]
    public void 側邊右側足夠時優先放右邊並避開清單()
    {
        var result = PreviewPlacementEngine.Calculate(Beside());

        Assert.Equal(PreviewPlacementSide.Right, result.Side);
        Assert.Equal(Completion.Right + 4, result.Bounds.Left);
        Assert.False(result.Bounds.Intersects(Completion));
        AssertContained(result.Bounds, Document);
    }

    [Fact]
    public void 側邊右側不足時穩定翻到左邊()
    {
        var document = new PreviewRectangle(0, 0, 1100, 800);
        var anchor = new PreviewRectangle(760, 100, 50, 20);
        var completion = new PreviewRectangle(760, 120, 300, 220);
        var result = PreviewPlacementEngine.Calculate(Beside(
            available: document,
            anchor: anchor,
            obstacles: new[] { anchor, completion }));

        Assert.Equal(PreviewPlacementSide.Left, result.Side);
        Assert.Equal(anchor.Left - 4, result.Bounds.Right);
        AssertContained(result.Bounds, document);
    }

    [Fact]
    public void 側邊兩側都不足時回退上下而不是覆蓋文件外面板()
    {
        var document = new PreviewRectangle(200, 0, 700, 900);
        var anchor = new PreviewRectangle(480, 100, 40, 20);
        var completion = new PreviewRectangle(360, 120, 380, 220);
        var result = PreviewPlacementEngine.Calculate(Beside(
            available: document,
            anchor: anchor,
            desiredWidth: 620,
            obstacles: new[] { anchor, completion }));

        Assert.True(result.UsedFallback);
        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        AssertContained(result.Bounds, document);
    }

    [Fact]
    public void 側邊完整寬度都放不下時選較大的左側而非最小右側()
    {
        var document = new PreviewRectangle(0, 0, 964, 900);
        var anchor = new PreviewRectangle(600, 100, 40, 20);
        var result = PreviewPlacementEngine.Calculate(Beside(
            available: document,
            anchor: anchor,
            desiredWidth: 620,
            obstacles: Array.Empty<PreviewRectangle>()));

        Assert.Equal(PreviewPlacementSide.Left, result.Side);
        Assert.Equal(596, result.Bounds.Width);
        Assert.Equal(anchor.Left - 4, result.Bounds.Right);
    }

    [Fact]
    public void 側邊已回退下方時空間明顯增加會回到使用者選擇的右側()
    {
        var request = Beside(
            available: new PreviewRectangle(0, 0, 1500, 900),
            anchor: new PreviewRectangle(500, 100, 40, 20),
            desiredWidth: 320,
            obstacles: Array.Empty<PreviewRectangle>());
        request = new PreviewLayoutRequest
        {
            Placement = request.Placement,
            AvailableBounds = request.AvailableBounds,
            Anchor = request.Anchor,
            Obstacles = request.Obstacles,
            DesiredWidth = request.DesiredWidth,
            DesiredHeight = request.DesiredHeight,
            MinimumWidth = request.MinimumWidth,
            MinimumHeight = request.MinimumHeight,
            MaximumWidth = request.MaximumWidth,
            MaximumHeight = request.MaximumHeight,
            Gap = request.Gap,
            PreviousSide = PreviewPlacementSide.Below
        };

        var result = PreviewPlacementEngine.Calculate(request);

        Assert.Equal(PreviewPlacementSide.Right, result.Side);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void 側邊可用寬度明顯大於最小值時不必等到完整偏好寬度才恢復()
    {
        var request = Beside(
            available: new PreviewRectangle(0, 0, 1204, 900),
            anchor: new PreviewRectangle(600, 100, 40, 20),
            desiredWidth: 620,
            obstacles: Array.Empty<PreviewRectangle>());
        request = new PreviewLayoutRequest
        {
            Placement = request.Placement,
            AvailableBounds = request.AvailableBounds,
            Anchor = request.Anchor,
            Obstacles = request.Obstacles,
            DesiredWidth = request.DesiredWidth,
            DesiredHeight = request.DesiredHeight,
            MinimumWidth = request.MinimumWidth,
            MinimumHeight = request.MinimumHeight,
            MaximumWidth = request.MaximumWidth,
            MaximumHeight = request.MaximumHeight,
            Gap = request.Gap,
            PreviousSide = PreviewPlacementSide.Below
        };

        var result = PreviewPlacementEngine.Calculate(request);

        Assert.Equal(PreviewPlacementSide.Left, result.Side);
        Assert.Equal(596, result.Bounds.Width);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void 側邊已回退下方時只多幾個像素不會反覆翻面()
    {
        var result = PreviewPlacementEngine.Calculate(
            new PreviewLayoutRequest
            {
                Placement = SqlPreviewPlacement.Beside,
                AvailableBounds = new PreviewRectangle(490, 0, 376, 900),
                Anchor = new PreviewRectangle(500, 100, 40, 20),
                Obstacles = Array.Empty<PreviewRectangle>(),
                DesiredWidth = 320,
                DesiredHeight = 420,
                MinimumWidth = 320,
                MinimumHeight = 180,
                MaximumWidth = 2000,
                MaximumHeight = 1400,
                Gap = 4,
                PreviousSide = PreviewPlacementSide.Below
            });

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void 多個分離保留區不會被粗略外框誤判成整段不可用()
    {
        var obstacles = new[]
        {
            new PreviewRectangle(100, 120, 120, 180),
            new PreviewRectangle(1190, 120, 80, 180)
        };
        var result = PreviewPlacementEngine.Calculate(Stacked(obstacles: obstacles));

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.Equal(Anchor.Bottom + 4, result.Bounds.Top);
    }

    [Fact]
    public void 負螢幕座標不影響包含與定位規則()
    {
        var document = new PreviewRectangle(-1920, 40, 1500, 900);
        var anchor = new PreviewRectangle(-900, 100, 40, 20);
        var result = PreviewPlacementEngine.Calculate(Stacked(
            available: document,
            anchor: anchor,
            obstacles: Array.Empty<PreviewRectangle>()));

        AssertContained(result.Bounds, document);
        Assert.Equal(620, result.Bounds.Width);
        Assert.Equal(document.Right, result.Bounds.Right);
    }

    [Fact]
    public void 整體平移輸入時輸出也同量平移()
    {
        var original = PreviewPlacementEngine.Calculate(Stacked());
        var moved = PreviewPlacementEngine.Calculate(Stacked(
            available: Move(Document, -700, 300),
            anchor: Move(Anchor, -700, 300),
            obstacles: new[] { Move(Anchor, -700, 300), Move(Completion, -700, 300) }));

        Assert.Equal(original.Bounds.Left - 700, moved.Bounds.Left);
        Assert.Equal(original.Bounds.Top + 300, moved.Bounds.Top);
        Assert.Equal(original.Bounds.Width, moved.Bounds.Width);
        Assert.Equal(original.Bounds.Height, moved.Bounds.Height);
        Assert.Equal(original.Side, moved.Side);
    }

    private static PreviewLayoutRequest Stacked(
        PreviewRectangle? available = null,
        PreviewRectangle? anchor = null,
        double desiredWidth = 620,
        double desiredHeight = 420,
        double maximumWidth = 2000,
        bool stretch = false,
        IReadOnlyList<PreviewRectangle>? obstacles = null) =>
        Request(
            SqlPreviewPlacement.Stacked,
            available ?? Document,
            anchor ?? Anchor,
            desiredWidth,
            desiredHeight,
            maximumWidth,
            stretch,
            obstacles ?? new[] { Anchor, Completion });

    private static PreviewLayoutRequest Beside(
        PreviewRectangle? available = null,
        PreviewRectangle? anchor = null,
        double desiredWidth = 320,
        double desiredHeight = 420,
        IReadOnlyList<PreviewRectangle>? obstacles = null) =>
        Request(
            SqlPreviewPlacement.Beside,
            available ?? Document,
            anchor ?? Anchor,
            desiredWidth,
            desiredHeight,
            2000,
            false,
            obstacles ?? new[] { Anchor, Completion });

    private static PreviewLayoutRequest Request(
        SqlPreviewPlacement placement,
        PreviewRectangle available,
        PreviewRectangle anchor,
        double desiredWidth,
        double desiredHeight,
        double maximumWidth,
        bool stretch,
        IReadOnlyList<PreviewRectangle> obstacles) => new()
    {
        Placement = placement,
        AvailableBounds = available,
        Anchor = anchor,
        Obstacles = obstacles,
        DesiredWidth = desiredWidth,
        DesiredHeight = desiredHeight,
        MinimumWidth = 320,
        MinimumHeight = 180,
        MaximumWidth = maximumWidth,
        MaximumHeight = 1400,
        StretchStackedWidth = stretch,
        Gap = 4
    };

    private static PreviewRectangle Move(PreviewRectangle rectangle, double x, double y) =>
        new(rectangle.Left + x, rectangle.Top + y, rectangle.Width, rectangle.Height);

    private static void AssertContained(PreviewRectangle inner, PreviewRectangle outer)
    {
        Assert.True(inner.Left >= outer.Left - 0.001);
        Assert.True(inner.Top >= outer.Top - 0.001);
        Assert.True(inner.Right <= outer.Right + 0.001);
        Assert.True(inner.Bottom <= outer.Bottom + 0.001);
    }
}
