using SqlAssist.Core.Preview;
using Xunit;

namespace SqlAssist.Core.Tests.Preview;

public sealed class PreviewResizeEngineTests
{
    private static readonly PreviewRectangle Available = new(0, 0, 1200, 900);
    private static readonly PreviewRectangle Initial = new(300, 200, 600, 420);

    [Fact]
    public void 右下角拖曳時固定左邊界與上邊界()
    {
        var result = Resize(PreviewResizeCorner.BottomRight, 120, 80);

        Assert.Equal(Initial.Left, result.Left);
        Assert.Equal(Initial.Top, result.Top);
        Assert.Equal(720, result.Width);
        Assert.Equal(500, result.Height);
    }

    [Fact]
    public void 左下角拖曳時固定右邊界與上邊界()
    {
        var result = Resize(PreviewResizeCorner.BottomLeft, -120, 80);

        Assert.Equal(Initial.Right, result.Right);
        Assert.Equal(Initial.Top, result.Top);
        Assert.Equal(720, result.Width);
        Assert.Equal(500, result.Height);
    }

    [Fact]
    public void 上方落點用左上角增高時固定右邊界與下邊界()
    {
        var result = Resize(PreviewResizeCorner.TopLeft, -120, -100);

        Assert.Equal(Initial.Right, result.Right);
        Assert.Equal(Initial.Bottom, result.Bottom);
        Assert.Equal(720, result.Width);
        Assert.Equal(520, result.Height);
        Assert.Equal(100, result.Top);
    }

    [Fact]
    public void 上方落點縮小後仍可沿上緣拉回且不移動下邊界()
    {
        var shrunk = Resize(PreviewResizeCorner.TopRight, -100, 120);
        var restored = PreviewResizeEngine.Resize(
            shrunk,
            PreviewResizeCorner.TopRight,
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
        var left = Resize(PreviewResizeCorner.BottomLeft, 9999, -9999);
        var right = Resize(PreviewResizeCorner.BottomRight, -9999, -9999);

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
        var left = Resize(PreviewResizeCorner.BottomLeft, -9999, 9999);
        var right = Resize(PreviewResizeCorner.BottomRight, 9999, 9999);

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
        var first = Resize(PreviewResizeCorner.BottomRight, 100, 50);
        var second = Resize(PreviewResizeCorner.BottomRight, 100, 50);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 左右角落的水平操作互為鏡像()
    {
        var left = Resize(PreviewResizeCorner.BottomLeft, -100, 0);
        var right = Resize(PreviewResizeCorner.BottomRight, 100, 0);

        Assert.Equal(left.Width, right.Width);
        Assert.Equal(left.Height, right.Height);
        Assert.Equal(Initial.Right, left.Right);
        Assert.Equal(Initial.Left, right.Left);
    }

    private static PreviewRectangle Resize(
        PreviewResizeCorner corner,
        double horizontal,
        double vertical) =>
        PreviewResizeEngine.Resize(
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
