using System;
using System.Linq;
using SqlAssist.Core.Preview;
using Xunit;

namespace SqlAssist.Core.Tests.Preview;

public sealed class PreviewLifecycleTests
{
    private static readonly PreviewMode[] Visible = { PreviewMode.Browse, PreviewMode.Named, PreviewMode.Pinned };

    private static readonly PreviewSignal[] Signals = Enum.GetValues(typeof(PreviewSignal)).Cast<PreviewSignal>().ToArray();

    [Fact]
    public void 使用者自己關一律收()
    {
        foreach (var mode in Visible)
        {
            Assert.True(PreviewLifecycle.Closes(mode, PreviewSignal.Dismiss));
        }
    }

    [Fact]
    public void 沒有預覽時什麼都不收()
    {
        foreach (var signal in Signals)
        {
            Assert.False(PreviewLifecycle.Closes(PreviewMode.Hidden, signal));
        }
    }

    [Theory]
    [InlineData(PreviewSignal.SessionEnded, true)]
    [InlineData(PreviewSignal.SessionStarted, true)]
    [InlineData(PreviewSignal.CaretLeftAnchor, false)]
    [InlineData(PreviewSignal.AnchorEdited, false)]
    public void 清單上展開的跟著清單走(PreviewSignal signal, bool closes)
    {
        Assert.Equal(closes, PreviewLifecycle.Closes(PreviewMode.Browse, signal));
    }

    [Theory]
    [InlineData(PreviewSignal.CaretLeftAnchor, true)]
    [InlineData(PreviewSignal.AnchorEdited, true)]
    [InlineData(PreviewSignal.SessionEnded, false)]
    [InlineData(PreviewSignal.SessionStarted, false)]
    public void 指名打開的跟著錨點走(PreviewSignal signal, bool closes)
    {
        Assert.Equal(closes, PreviewLifecycle.Closes(PreviewMode.Named, signal));
    }

    [Fact]
    public void 釘住的只有使用者自己關()
    {
        foreach (var signal in Signals)
        {
            Assert.Equal(signal == PreviewSignal.Dismiss, PreviewLifecycle.Closes(PreviewMode.Pinned, signal));
        }
    }

    [Theory]
    [InlineData(PreviewTrigger.CompletionArrow, PreviewMode.Browse)]
    [InlineData(PreviewTrigger.CompletionDelay, PreviewMode.Browse)]
    [InlineData(PreviewTrigger.Command, PreviewMode.Named)]
    [InlineData(PreviewTrigger.HoverLink, PreviewMode.Named)]
    public void 打開方式決定狀態(PreviewTrigger trigger, PreviewMode mode)
    {
        Assert.Equal(mode, PreviewLifecycle.ModeFor(trigger));
    }

    [Fact]
    public void 釘住的只擋自動展開()
    {
        foreach (var trigger in Enum.GetValues(typeof(PreviewTrigger)).Cast<PreviewTrigger>())
        {
            Assert.Equal(
                trigger != PreviewTrigger.CompletionDelay,
                PreviewLifecycle.CanReplace(PreviewMode.Pinned, trigger));
            Assert.True(PreviewLifecycle.CanReplace(PreviewMode.Named, trigger));
            Assert.True(PreviewLifecycle.CanReplace(PreviewMode.Browse, trigger));
        }
    }

    [Theory]
    [InlineData(PreviewMode.Browse, PreviewMode.Pinned)]
    [InlineData(PreviewMode.Named, PreviewMode.Pinned)]
    [InlineData(PreviewMode.Pinned, PreviewMode.Named)]
    [InlineData(PreviewMode.Hidden, PreviewMode.Hidden)]
    public void 圖釘在釘住與指名之間切換(PreviewMode from, PreviewMode to)
    {
        Assert.Equal(to, PreviewLifecycle.TogglePin(from));
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
