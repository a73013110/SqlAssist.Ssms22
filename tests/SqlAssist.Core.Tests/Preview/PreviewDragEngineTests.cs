using SqlAssist.Core.Preview;
using Xunit;

namespace SqlAssist.Core.Tests.Preview;

public sealed class PreviewDragEngineTests
{
    private static readonly PreviewRectangle Available = new(0, 0, 1200, 900);
    private static readonly PreviewRectangle Initial = new(300, 200, 600, 420);

    [Fact]
    public void 右下角拖曳時固定左邊界與上邊界()
    {
        var result = Resize(PreviewDragHandle.BottomRight, 120, 80);

        Assert.Equal(Initial.Left, result.Left);
        Assert.Equal(Initial.Top, result.Top);
        Assert.Equal(720, result.Width);
        Assert.Equal(500, result.Height);
    }

    [Fact]
    public void 左下角拖曳時固定右邊界與上邊界()
    {
        var result = Resize(PreviewDragHandle.BottomLeft, -120, 80);

        Assert.Equal(Initial.Right, result.Right);
        Assert.Equal(Initial.Top, result.Top);
        Assert.Equal(720, result.Width);
        Assert.Equal(500, result.Height);
    }

    [Fact]
    public void 上方落點用左上角增高時固定右邊界與下邊界()
    {
        var result = Resize(PreviewDragHandle.TopLeft, -120, -100);

        Assert.Equal(Initial.Right, result.Right);
        Assert.Equal(Initial.Bottom, result.Bottom);
        Assert.Equal(720, result.Width);
        Assert.Equal(520, result.Height);
        Assert.Equal(100, result.Top);
    }

    [Fact]
    public void 上方落點縮小後仍可沿上緣拉回且不移動下邊界()
    {
        var shrunk = Resize(PreviewDragHandle.TopRight, -100, 120);
        var restored = PreviewDragEngine.Resize(
            shrunk,
            PreviewDragHandle.TopRight,
            0,
            -120,
            Available,
            minimumWidth: 320,
            minimumHeight: 180,
            maximumWidth: 2000,
            maximumHeight: 1400);

        Assert.Equal(shrunk.Bottom, restored.Bottom);
        Assert.Equal(Initial.Height, restored.Height);
    }

    [Fact]
    public void 兩個角落縮小時都收斂到最小尺寸()
    {
        var left = Resize(PreviewDragHandle.BottomLeft, 9999, -9999);
        var right = Resize(PreviewDragHandle.BottomRight, -9999, -9999);

        Assert.Equal(320, left.Width);
        Assert.Equal(320, right.Width);
        Assert.Equal(180, left.Height);
        Assert.Equal(180, right.Height);
        Assert.Equal(Initial.Right, left.Right);
        Assert.Equal(Initial.Left, right.Left);
    }

    [Fact]
    public void 拖過文件邊界時邊界安全優先()
    {
        var left = Resize(PreviewDragHandle.BottomLeft, -9999, 9999);
        var right = Resize(PreviewDragHandle.BottomRight, 9999, 9999);

        Assert.Equal(Available.Left, left.Left);
        Assert.Equal(Initial.Right, left.Right);
        Assert.Equal(Initial.Left, right.Left);
        Assert.Equal(Available.Right, right.Right);
        Assert.Equal(Available.Bottom, left.Bottom);
        Assert.Equal(Available.Bottom, right.Bottom);
    }

    [Fact]
    public void 相同總位移重複計算不會累積回授()
    {
        var first = Resize(PreviewDragHandle.BottomRight, 100, 50);
        var second = Resize(PreviewDragHandle.BottomRight, 100, 50);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 左右角落的水平操作互為鏡像()
    {
        var left = Resize(PreviewDragHandle.BottomLeft, -100, 0);
        var right = Resize(PreviewDragHandle.BottomRight, 100, 0);

        Assert.Equal(left.Width, right.Width);
        Assert.Equal(left.Height, right.Height);
        Assert.Equal(Initial.Right, left.Right);
        Assert.Equal(Initial.Left, right.Left);
    }

    [Fact]
    public void 抬頭拖曳只平移不改尺寸()
    {
        var result = PreviewDragEngine.Drag(Initial, PreviewDragHandle.Move, -120, 60, Available, 320, 180, 2000, 1400);

        Assert.Equal(new PreviewRectangle(180, 260, Initial.Width, Initial.Height), result);
    }

    [Fact]
    public void 搬到邊界外時停在邊界上而不縮小()
    {
        var result = PreviewDragEngine.Move(Initial, 9999, -9999, Available);

        Assert.Equal(Available.Right, result.Right);
        Assert.Equal(Available.Top, result.Top);
        Assert.Equal(Initial.Width, result.Width);
        Assert.Equal(Initial.Height, result.Height);
    }

    [Fact]
    public void 可用範圍變小時先平移再縮小且不小於最小尺寸()
    {
        var shifted = PreviewDragEngine.Contain(new PreviewRectangle(900, 700, 600, 420), Available, 320, 180);
        var shrunk = PreviewDragEngine.Contain(Initial, new PreviewRectangle(0, 0, 500, 300), 320, 180);
        var floor = PreviewDragEngine.Contain(Initial, new PreviewRectangle(0, 0, 200, 100), 320, 180);

        Assert.Equal(new PreviewRectangle(600, 480, 600, 420), shifted);
        Assert.Equal(new PreviewRectangle(0, 0, 500, 300), shrunk);
        Assert.Equal(new PreviewRectangle(0, 0, 200, 100), floor);
    }

    private static PreviewRectangle Resize(
        PreviewDragHandle corner,
        double horizontal,
        double vertical) =>
        PreviewDragEngine.Resize(
            Initial,
            corner,
            horizontal,
            vertical,
            Available,
            minimumWidth: 320,
            minimumHeight: 180,
            maximumWidth: 2000,
            maximumHeight: 1400);
}
