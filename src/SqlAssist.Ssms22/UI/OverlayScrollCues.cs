using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 覆蓋式捲軸的兩個提示：握把靜止後淡出，還能捲的那一端讓內容淡出一小段。
/// </summary>
/// <remarks>
/// 由 <see cref="SqlAssistChrome.ApplyOverlayScroll"/> 掛上，不另外建立。握把只在捲動或停在握把上時出現：
/// 通知島本來就要停駐才會展開，「停駐就顯示」等於永遠顯示。改由邊緣淡出回答「還有沒有」——
/// 看不到握把時使用者仍然知道下面還有列。
/// </remarks>
internal sealed class OverlayScrollCues
{
    /// <summary>可捲那一端淡出的長度（DIP）。</summary>
    public const double EdgeFade = 14;

    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ThumbIn = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ThumbOut = TimeSpan.FromMilliseconds(300);

    private readonly ScrollViewer _scroll;
    private readonly bool _fadeWhenIdle;
    private readonly bool _fadeEdges;
    private DispatcherTimer? _timer;
    private ScrollBar? _bar;

    private OverlayScrollCues(ScrollViewer scroll, bool fadeWhenIdle, bool fadeEdges)
    {
        _scroll = scroll; _fadeWhenIdle = fadeWhenIdle; _fadeEdges = fadeEdges;
    }

    public static void Attach(ScrollViewer scroll, bool fadeWhenIdle, bool fadeEdges)
    {
        if (scroll is null) throw new ArgumentNullException(nameof(scroll));
        var cues = new OverlayScrollCues(scroll, fadeWhenIdle, fadeEdges);
        scroll.ScrollChanged += (_, args) => cues.OnScrollChanged(args);
        scroll.Loaded += (_, _) => cues.Refresh();
        scroll.SizeChanged += (_, _) => cues.UpdateEdges();
    }

    /// <summary>握把的不透明度；測試與截圖用來確認靜止時確實收起。</summary>
    internal static double ThumbOpacity(ScrollViewer scroll) => Bar(scroll)?.Opacity ?? 1;

    private void Refresh()
    {
        // 只在第一次拿到這一個握把時收起；列增減也會進來，那時握把可能正顯示著。
        if (_fadeWhenIdle && Bar(_scroll) is { } bar && !ReferenceEquals(bar, _bar))
        {
            _bar = bar;
            bar.Opacity = 0;
            bar.MouseEnter += OnBarEnter;
        }

        UpdateEdges();
    }

    private void OnScrollChanged(ScrollChangedEventArgs args)
    {
        // 範本在第一次捲動事件之前一定已經套上；Loaded 沒來得及的話（例如一直在看不見的樹裡）在這裡補。
        if (_fadeWhenIdle && Bar(_scroll) is { } bar && args.VerticalChange != 0) Reveal(bar);
        else if (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0) Refresh();
        UpdateEdges();
    }

    private void OnBarEnter(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (sender is ScrollBar bar) Reveal(bar);
    }

    private void Reveal(ScrollBar bar)
    {
        Fade(bar, 1, ThumbIn);
        _timer ??= CreateTimer();
        _timer.Stop(); _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _scroll.Dispatcher) { Interval = Idle };
        timer.Tick += (_, _) =>
        {
            if (Bar(_scroll) is not { } bar) { timer.Stop(); return; }
            // 還停在握把上或正在拖：再等一輪，不在使用者手底下消失。
            if (bar.IsMouseOver || bar.IsMouseCaptureWithin) return;
            timer.Stop();
            Fade(bar, 0, ThumbOut);
        };
        return timer;
    }

    private static void Fade(UIElement element, double to, TimeSpan duration)
    {
        if (!SqlAssistChrome.MotionEnabled) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = to; return; }
        var fade = new DoubleAnimation(element.Opacity, to, duration) { FillBehavior = FillBehavior.Stop };
        element.Opacity = to;
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void UpdateEdges()
    {
        if (!_fadeEdges || Presenter(_scroll) is not { } presenter) return;
        var height = presenter.ActualHeight;
        var above = _scroll.VerticalOffset > 0.5;
        var below = _scroll.VerticalOffset < _scroll.ScrollableHeight - 0.5;
        if (height <= EdgeFade * 2 || (!above && !below)) { presenter.OpacityMask = null; return; }
        // 沒有溢出的時候不掛遮罩：透明分層視窗上多一層 OpacityMask 每一影格都要多算一次。
        var edge = EdgeFade / height;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(above ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
        mask.GradientStops.Add(new GradientStop(below ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        presenter.OpacityMask = mask;
    }

    private static ScrollBar? Bar(ScrollViewer scroll) =>
        scroll.Template?.FindName("PART_VerticalScrollBar", scroll) as ScrollBar;

    private static ScrollContentPresenter? Presenter(ScrollViewer scroll) =>
        scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;
}
