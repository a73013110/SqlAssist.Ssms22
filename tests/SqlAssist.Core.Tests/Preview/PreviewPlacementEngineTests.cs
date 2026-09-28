using System;
using System.Collections.Generic;
using SqlAssist.Core.Preview;
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
        var result = PreviewPlacementEngine.Calculate(Layout());

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.Equal(420, result.Bounds.Height);
        Assert.Equal(324, result.Bounds.Top);
        AssertContained(result.Bounds, Document);
    }

    [Fact]
    public void 錨點靠右時平移視窗而非縮小或移出文件()
    {
        var result = PreviewPlacementEngine.Calculate(Layout(
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
        var result = PreviewPlacementEngine.Calculate(Layout());

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.False(result.Bounds.Intersects(Completion));
        Assert.Equal(Completion.Bottom + 4, result.Bounds.Top);
    }

    [Fact]
    public void 上一輪在上方且仍放得下時不因微小重排翻回下方()
    {
        var request = Layout(
            available: new PreviewRectangle(0, 0, 1200, 1200),
            anchor: new PreviewRectangle(500, 600, 50, 20),
            obstacles: Array.Empty<PreviewRectangle>());
        request = new PreviewLayoutRequest
        {
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
        var result = PreviewPlacementEngine.Calculate(Layout(
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
        var result = PreviewPlacementEngine.Calculate(Layout(
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
        var result = PreviewPlacementEngine.Calculate(Layout(
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
        var result = PreviewPlacementEngine.Calculate(Layout(
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
        var result = PreviewPlacementEngine.Calculate(Layout(
            anchor: new PreviewRectangle(120, 100, 40, 20),
            desiredWidth: 620,
            stretch: true,
            maximumWidth: 800,
            obstacles: Array.Empty<PreviewRectangle>()));

        Assert.Equal(800, result.Bounds.Width);
        Assert.Equal(120, result.Bounds.Left);
    }

    [Fact]
    public void 多個分離保留區不會被粗略外框誤判成整段不可用()
    {
        var obstacles = new[]
        {
            new PreviewRectangle(100, 120, 120, 180),
            new PreviewRectangle(1190, 120, 80, 180)
        };
        var result = PreviewPlacementEngine.Calculate(Layout(obstacles: obstacles));

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.Equal(Anchor.Bottom + 4, result.Bounds.Top);
    }

    [Fact]
    public void 負螢幕座標不影響包含與定位規則()
    {
        var document = new PreviewRectangle(-1920, 40, 1500, 900);
        var anchor = new PreviewRectangle(-900, 100, 40, 20);
        var result = PreviewPlacementEngine.Calculate(Layout(
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
        var original = PreviewPlacementEngine.Calculate(Layout());
        var moved = PreviewPlacementEngine.Calculate(Layout(
            available: Move(Document, -700, 300),
            anchor: Move(Anchor, -700, 300),
            obstacles: new[] { Move(Anchor, -700, 300), Move(Completion, -700, 300) }));

        Assert.Equal(original.Bounds.Left - 700, moved.Bounds.Left);
        Assert.Equal(original.Bounds.Top + 300, moved.Bounds.Top);
        Assert.Equal(original.Bounds.Width, moved.Bounds.Width);
        Assert.Equal(original.Bounds.Height, moved.Bounds.Height);
        Assert.Equal(original.Side, moved.Side);
    }

    [Fact]
    public void 蓋住整個錨點的保留區不算障礙而建議清單仍要讓開()
    {
        // 平台在建議清單開著時連錨點所在的一整行都保留（下面第一塊，橫跨整個文件寬）；
        // 排除那一塊之後，真正該讓開的建議清單仍在錨點下方。數值取自實測記錄。
        var result = PreviewPlacementEngine.Calculate(Layout(
            available: new PreviewRectangle(487, 100, 1288, 833),
            anchor: new PreviewRectangle(601, 100, 24, 18),
            desiredWidth: 1288,
            desiredHeight: 553,
            obstacles: new[]
            {
                new PreviewRectangle(487, 100, 1288, 21),
                new PreviewRectangle(601, 100, 24, 18),
                new PreviewRectangle(601, 121, 329, 255)
            }));

        Assert.Equal(PreviewPlacementSide.Below, result.Side);
        Assert.Equal(380, result.Bounds.Top);
    }

    private static PreviewLayoutRequest Layout(
        PreviewRectangle? available = null,
        PreviewRectangle? anchor = null,
        double desiredWidth = 620,
        double desiredHeight = 420,
        double maximumWidth = 2000,
        bool stretch = false,
        IReadOnlyList<PreviewRectangle>? obstacles = null) =>
        Request(
            available ?? Document,
            anchor ?? Anchor,
            desiredWidth,
            desiredHeight,
            maximumWidth,
            stretch,
            obstacles ?? new[] { Anchor, Completion });

    private static PreviewLayoutRequest Request(
        PreviewRectangle available,
        PreviewRectangle anchor,
        double desiredWidth,
        double desiredHeight,
        double maximumWidth,
        bool stretch,
        IReadOnlyList<PreviewRectangle> obstacles) => new()
    {
        AvailableBounds = available,
        Anchor = anchor,
        Obstacles = obstacles,
        DesiredWidth = desiredWidth,
        DesiredHeight = desiredHeight,
        MinimumWidth = 320,
        MinimumHeight = 180,
        MaximumWidth = maximumWidth,
        MaximumHeight = 1400,
        StretchWidth = stretch,
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
