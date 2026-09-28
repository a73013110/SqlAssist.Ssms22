using System;
using SqlAssist.Core.Preview;
using Xunit;

namespace SqlAssist.Core.Tests.Preview;

public sealed class PreviewRevealTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(149)]
    public void 寬限內到齊的不顯示等待(int milliseconds)
    {
        Assert.Equal(TimeSpan.Zero, PreviewReveal.HoldAfterReady(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void 進度圈浮現後至少留滿停留時間()
    {
        var elapsed = PreviewReveal.Grace + TimeSpan.FromMilliseconds(50);

        Assert.Equal(
            PreviewReveal.MinimumHold - TimeSpan.FromMilliseconds(50),
            PreviewReveal.HoldAfterReady(elapsed));
    }

    [Fact]
    public void 已經等夠久的立刻展開()
    {
        var elapsed = PreviewReveal.Grace + PreviewReveal.MinimumHold + TimeSpan.FromMilliseconds(1);

        Assert.Equal(TimeSpan.Zero, PreviewReveal.HoldAfterReady(elapsed));
    }

    [Fact]
    public void 上限晚於停留時間()
    {
        Assert.True(PreviewReveal.Ceiling > PreviewReveal.Grace + PreviewReveal.MinimumHold);
    }
}
