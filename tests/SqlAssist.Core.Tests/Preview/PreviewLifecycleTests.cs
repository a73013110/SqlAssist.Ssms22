using System;
using System.Linq;
using SqlAssist.Core.Preview;
using Xunit;

namespace SqlAssist.Core.Tests.Preview;

public sealed class PreviewLifecycleTests
{
    private static readonly PreviewMode[] Visible = { PreviewMode.Browse, PreviewMode.Named, PreviewMode.Pinned };

    private static readonly PreviewSignal[] Signals = Enum.GetValues(typeof(PreviewSignal)).Cast<PreviewSignal>().ToArray();

    private static readonly PreviewTrigger[] Triggers = Enum.GetValues(typeof(PreviewTrigger)).Cast<PreviewTrigger>().ToArray();

    [Fact]
    public void 使用者自己關一律收()
    {
        foreach (var mode in Visible)
        {
            Assert.Equal(PreviewOutcome.Close, PreviewLifecycle.Resolve(mode, pinned: false, PreviewSignal.Dismiss));
            Assert.Equal(PreviewOutcome.Close, PreviewLifecycle.Resolve(mode, pinned: true, PreviewSignal.Dismiss));
        }
    }

    [Fact]
    public void 沒有預覽時什麼都不收()
    {
        foreach (var signal in Signals)
        {
            Assert.Equal(PreviewOutcome.Keep, PreviewLifecycle.Resolve(PreviewMode.Hidden, pinned: false, signal));
        }
    }

    [Theory]
    [InlineData(PreviewSignal.SessionEnded, PreviewOutcome.Close)]
    [InlineData(PreviewSignal.SessionStarted, PreviewOutcome.Close)]
    [InlineData(PreviewSignal.Collapse, PreviewOutcome.Close)]
    [InlineData(PreviewSignal.CaretLeftAnchor, PreviewOutcome.Keep)]
    [InlineData(PreviewSignal.AnchorEdited, PreviewOutcome.Keep)]
    [InlineData(PreviewSignal.AnchorScrolledOut, PreviewOutcome.Keep)]
    public void 清單上展開的跟著清單走(PreviewSignal signal, PreviewOutcome outcome)
    {
        Assert.Equal(outcome, PreviewLifecycle.Resolve(PreviewMode.Browse, pinned: false, signal));
    }

    [Theory]
    [InlineData(PreviewSignal.SessionEnded)]
    [InlineData(PreviewSignal.SessionStarted)]
    [InlineData(PreviewSignal.Collapse)]
    public void 借用釘住的窗結束時還回釘住的那一份(PreviewSignal signal)
    {
        Assert.Equal(PreviewOutcome.ReturnToPin, PreviewLifecycle.Resolve(PreviewMode.Browse, pinned: true, signal));
    }

    [Theory]
    [InlineData(PreviewSignal.CaretLeftAnchor, PreviewOutcome.Close)]
    [InlineData(PreviewSignal.AnchorEdited, PreviewOutcome.Close)]
    [InlineData(PreviewSignal.AnchorScrolledOut, PreviewOutcome.Close)]
    [InlineData(PreviewSignal.SessionEnded, PreviewOutcome.Keep)]
    [InlineData(PreviewSignal.SessionStarted, PreviewOutcome.Keep)]
    [InlineData(PreviewSignal.Collapse, PreviewOutcome.Keep)]
    public void 指名打開的跟著錨點走(PreviewSignal signal, PreviewOutcome outcome)
    {
        Assert.Equal(outcome, PreviewLifecycle.Resolve(PreviewMode.Named, pinned: false, signal));
    }

    [Fact]
    public void 釘住的只有使用者自己關()
    {
        foreach (var signal in Signals)
        {
            Assert.Equal(
                signal == PreviewSignal.Dismiss ? PreviewOutcome.Close : PreviewOutcome.Keep,
                PreviewLifecycle.Resolve(PreviewMode.Pinned, pinned: true, signal));
        }
    }

    [Theory]
    [InlineData(PreviewTrigger.CompletionArrow, false, PreviewMode.Browse)]
    [InlineData(PreviewTrigger.CompletionDelay, false, PreviewMode.Browse)]
    [InlineData(PreviewTrigger.Command, false, PreviewMode.Named)]
    [InlineData(PreviewTrigger.HoverLink, false, PreviewMode.Named)]
    [InlineData(PreviewTrigger.CompletionArrow, true, PreviewMode.Browse)]
    [InlineData(PreviewTrigger.CompletionDelay, true, PreviewMode.Browse)]
    [InlineData(PreviewTrigger.Command, true, PreviewMode.Pinned)]
    [InlineData(PreviewTrigger.HoverLink, true, PreviewMode.Pinned)]
    public void 打開方式決定狀態_釘住時指名的換進釘住的窗(PreviewTrigger trigger, bool pinned, PreviewMode mode)
    {
        Assert.Equal(mode, PreviewLifecycle.ModeFor(trigger, pinned));
    }

    [Fact]
    public void 每一種來源都用得到釘住的窗()
    {
        // 釘住不擋任何來源：清單上的借用、指名的換進去，沒有一種會變成「按了沒反應」。
        foreach (var trigger in Triggers)
        {
            Assert.NotEqual(PreviewMode.Hidden, PreviewLifecycle.ModeFor(trigger, pinned: true));
        }
    }

    [Theory]
    [InlineData(PreviewMode.Browse, false, PreviewMode.Pinned, true)]
    [InlineData(PreviewMode.Named, false, PreviewMode.Pinned, true)]
    [InlineData(PreviewMode.Pinned, true, PreviewMode.Named, false)]
    [InlineData(PreviewMode.Browse, true, PreviewMode.Browse, false)]
    [InlineData(PreviewMode.Hidden, false, PreviewMode.Hidden, false)]
    public void 圖釘切換(PreviewMode from, bool pinned, PreviewMode to, bool pinnedAfter)
    {
        Assert.Equal((to, pinnedAfter), PreviewLifecycle.TogglePin(from, pinned));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void 錨點前後緣都算在名稱上(int caret, bool onAnchor)
    {
        Assert.Equal(onAnchor, PreviewLifecycle.IsOnAnchor(10, 15, caret));
    }
}
