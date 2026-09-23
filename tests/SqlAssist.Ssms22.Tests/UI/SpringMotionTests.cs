using System;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SpringMotionTests
{
    private const double Frame = 1.0 / 60;

    [Fact]
    public void 預設參數回彈在百分之三以內且四百毫秒內收斂()
    {
        var state = new SpringState(0, 0);
        var peak = 0d;
        var settledAt = double.NaN;
        for (var time = Frame; time <= 1; time += Frame)
        {
            state = state.Step(100, SpringParameters.Default, Frame);
            peak = Math.Max(peak, state.Value);
            if (double.IsNaN(settledAt) && Math.Abs(state.Value - 100) < 1) settledAt = time;
            if (Math.Abs(state.Value - 100) >= 1) settledAt = double.NaN;
        }

        Assert.True(peak > 100, "阻尼小於 1 應該有一點回彈");
        Assert.True(peak <= 103, $"回彈 {peak - 100:0.00}% 超過 3%");
        Assert.True(settledAt <= 0.4, $"{settledAt:0.000} 秒才收斂到 1% 以內");
        Assert.True(state.IsSettled(100));
    }

    [Fact]
    public void 積分與影格長短無關()
    {
        var coarse = new SpringState(0, 0).Step(1, SpringParameters.Default, 0.2);
        var fine = new SpringState(0, 0);
        for (var i = 0; i < 48; i++) fine = fine.Step(1, SpringParameters.Default, 0.2 / 48);
        Assert.Equal(fine.Value, coarse.Value, 3);
        Assert.Equal(new SpringState(5, 2), new SpringState(5, 2).Step(0, SpringParameters.Default, 0));
    }

    [Fact]
    public void 中途改目標保留速度可以反向()
    {
        // STA 上訂閱得到每幀事件，但測試執行緒不跑訊息迴圈，時間只由 Advance 餵。
        WpfTest.Run(() =>
        {
            var spring = new SpringMotion(0, _ => { });
            spring.AnimateTo(100, motion: true);
            for (var i = 0; i < 6; i++) spring.Advance(TimeSpan.FromSeconds(Frame));
            var velocity = spring.Velocity;
            var reversedAt = spring.Value;
            Assert.True(velocity > 0);
            spring.AnimateTo(0, motion: true);
            Assert.Equal(velocity, spring.Velocity);
            Assert.True(spring.IsActive);
            spring.Advance(TimeSpan.FromSeconds(Frame));
            Assert.True(spring.Velocity < velocity, "反向後速度要從原本的值往回減，不是歸零重來");
            var peak = spring.Value;
            for (var i = 0; i < 120 && spring.IsActive; i++)
            {
                spring.Advance(TimeSpan.FromSeconds(Frame));
                peak = Math.Max(peak, spring.Value);
            }

            Assert.True(peak > reversedAt, "帶著往前的速度反向，會先再往前一點才折返");
            Assert.False(spring.IsActive);
            Assert.Equal(0, spring.Value);
        });
    }

    [Fact]
    public void 動畫關閉時直接跳到目標且靜止後通知()
    {
        WpfTest.Run(() =>
        {
            var applied = double.NaN;
            var settled = 0;
            var spring = new SpringMotion(10, value => applied = value);
            spring.Settled += (_, _) => settled++;
            spring.AnimateTo(40, motion: false);
            Assert.Equal(40, applied);
            Assert.False(spring.IsActive);
            Assert.Equal(0, settled);

            spring.AnimateTo(80, motion: true);
            Assert.True(spring.IsActive);
            for (var i = 0; i < 120 && spring.IsActive; i++) spring.Advance(TimeSpan.FromSeconds(Frame));
            Assert.False(spring.IsActive);
            Assert.Equal(80, applied);
            Assert.Equal(1, settled);

            // 已經在目標上就不訂閱每幀事件：閒置零成本。
            spring.AnimateTo(80, motion: true);
            Assert.False(spring.IsActive);
            spring.AnimateTo(20, motion: true);
            spring.Stop();
            Assert.False(spring.IsActive);
            Assert.Equal(0, spring.Velocity);
        });
    }

    [Fact]
    public void 掉格時一次最多積分五十毫秒()
    {
        WpfTest.Run(() =>
        {
            var spring = new SpringMotion(0, _ => { });
            spring.AnimateTo(100, motion: true);
            spring.Advance(TimeSpan.FromSeconds(2));
            var reference = new SpringState(0, 0).Step(100, SpringParameters.Default, 0.05).Value;
            Assert.Equal(reference, spring.Value, 6);
            spring.Stop();
        });
    }
}
