using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SqlAssist.Core.Preview;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Preview;

/// <summary>拖曳抬頭或角落握把的一次事件。</summary>
internal sealed class PreviewDragEventArgs : EventArgs
{
    public PreviewDragEventArgs(
        PreviewDragHandle handle,
        double horizontalChange,
        double verticalChange,
        bool canceled = false)
    {
        Handle = handle;
        HorizontalChange = horizontalChange;
        VerticalChange = verticalChange;
        Canceled = canceled;
    }

    public PreviewDragHandle Handle { get; }

    /// <summary>相對按下瞬間的總位移，不是上一幀到這一幀的增量。</summary>
    public double HorizontalChange { get; }

    public double VerticalChange { get; }

    public bool Canceled { get; }
}

/// <summary>
/// 浮在編輯器上的預覽外殼：圓角與柔影、角落握把、抬頭拖曳、窗的三顆工具，以及進出場。
/// </summary>
/// <remarks>
/// 內容（<see cref="SqlStructurePanel"/>）與停靠的工具視窗共用；這一層只屬於浮動預覽。兩件事
/// 混在同一個控制項裡時，停靠的那一份得一路帶著用不到的陰影邊、握把與彈簧，還要記得把它們關掉。
///
/// 進出場與通知島同一套語言（<see cref="SurfaceMotion"/>、<see cref="SurfaceCapsule"/>）：
/// 從圓點長成膠囊，內容到了才攤開；內容還沒到時膠囊寫著名稱、轉著進度圈等，停多久由
/// <see cref="PreviewReveal"/> 決定。表面依最終尺寸排好版，外形只由裁切露出，資料格不重排。
/// </remarks>
internal sealed class PreviewSurface : UserControl, IDisposable
{
    /// <summary>
    /// 角落握把的邊長。
    /// </summary>
    /// <remarks>
    /// 不只是外觀尺寸：呼叫端拿它當「這一軸算不算被拖過」的門檻，所以寫死在兩邊
    /// 會出現「握把改大了、門檻沒跟著改」這種看不出關聯的失準。
    /// </remarks>
    public const double GripSize = 16;

    /// <summary>
    /// Popup 外圈留給柔影的透明邊。
    /// </summary>
    /// <remarks>
    /// Popup 以外畫不出東西，影子要在自己的矩形裡長；這一圈同時就是預覽與錨點、
    /// 建議清單之間的間距，定位那一端因此不再另加間距。
    /// </remarks>
    public const double ShadowMargin = 10;

    /// <summary>圓點先長成膠囊；比外形展開快，攤開時膠囊已經站穩。</summary>
    private static readonly SpringParameters SeedSpring = new(0.28, 0.8);

    /// <summary>寬先展開、高隨後落下；阻尼接近 1，外形衝過頭的那一點也畫不出來。</summary>
    private static readonly SpringParameters GrowWidth = new(0.30, 0.9);

    private static readonly SpringParameters GrowHeight = new(0.38, 0.9);

    /// <summary>收起時不回彈、比出現快：使用者已經決定不看了，不該再多等一段。</summary>
    private static readonly SpringParameters ExitSpring = new(0.22, 1);

    /// <summary>收起時整個表面淡掉的長度；晚內容一步開始，看得出內容先走。</summary>
    private const int ExitFade = 160;

    private readonly SqlStructurePanel _panel;

    /// <summary>整個 Popup 的內容：外圈是透明的陰影邊，裡面疊著柔影層與表面。</summary>
    private readonly Grid _frame;

    /// <summary>只有底色與柔影的一層；影子不掛在內容上，否則文字也會帶著一圈模糊。</summary>
    private readonly Border _shadow;

    private readonly Border _root;

    /// <summary>內容與握把；進場時晚一點淡入並微微放大，外形先長。</summary>
    private readonly Grid _content;

    private readonly ScaleTransform _contentScale = new(1, 1);

    /// <summary>等內容時的膠囊：進度圈與名稱，位置跟著外形的出發點。</summary>
    private readonly Grid _capsule;

    private readonly SurfaceStatusIcon _capsuleIcon = new();
    private readonly TextBlock _capsuleText;
    private readonly Thumb _resizeLeft;
    private readonly Thumb _resizeRight;
    private readonly ToggleButton _pin;

    /// <summary>等內容的計時：停夠了或到了上限就攤開。</summary>
    private readonly DispatcherTimer _holdTimer;

    private readonly Stopwatch _holdClock = new();

    /// <summary>圓點長成膠囊的進度；0 是圓點，1 是整顆膠囊。</summary>
    private SpringMotion? _seed;

    /// <summary>
    /// 外形的寬與高各自的揭露進度；0 是錨點旁的一顆膠囊，1 是整個表面。
    /// </summary>
    /// <remarks>
    /// 兩軸分開走：寬先展開、高隨後落下，看起來是膠囊拉長再往下攤開，而不是一個矩形等比放大。
    /// </remarks>
    private SpringMotion? _revealWidth;

    private SpringMotion? _revealHeight;

    /// <summary>膠囊出發的位置：錨點在表面裡的水平位置，以及是從上緣還是下緣長出來。</summary>
    private double _revealOrigin;

    private bool _revealFromBottom;

    private double _capsuleWidth = SurfaceCapsule.MinWidth;

    /// <summary>內容畫得出來了（不只標題）；還沒的話進場先停在膠囊。</summary>
    private bool _ready = true;

    /// <summary>正停在膠囊等內容。</summary>
    private bool _holding;

    /// <summary>縮回之後要做的事（關掉 Popup、放開內容或換到新位置）；還在縮時不是 null。</summary>
    private Action? _exitDone;

    /// <summary>目前的握把配置；null 是還沒配置過。</summary>
    private (bool OnTop, bool Pinned)? _grips;

    /// <summary>拖曳開始當下的游標螢幕位置；每一步都以它為基準算總位移。</summary>
    private Point _dragOrigin;

    /// <summary>正在拖的把手；沒在拖時 null。</summary>
    private PreviewDragHandle? _dragHandle;

    private Vector _dragChange;

    /// <summary>抬頭上按下但還沒拖出門檻的位置；一般的點擊不能被當成搬動（搬動會順便釘住）。</summary>
    private Point? _headerPress;

    public PreviewSurface(SqlStructurePanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        VsThemeBrushes.Apply(this);

        // 圖釘、移到工具視窗與關閉固定在右上角：預覽什麼時候會收、怎麼收、能不能留下來，
        // 看這三顆就知道。只靠 Esc 的話，沒讀過說明的人不知道怎麼關，也不知道它為什麼有時候自己收掉。
        _pin = SqlAssistChrome.CreateIconToggle(SqlIcon.Pin, PreviewText.PinToggle);
        _pin.Focusable = false;
        _pin.Click += (_, _) => SqlAssistPlatformGuard.Run("切換結構預覽圖釘", () => PinToggled?.Invoke(this, EventArgs.Empty));

        var dock = SqlAssistChrome.CreateIconButton(SqlIcon.Dock, PreviewText.DockButton);
        dock.Focusable = false;
        dock.Click += (_, _) => SqlAssistPlatformGuard.Run("結構預覽移到工具視窗", () => DockRequested?.Invoke(this, EventArgs.Empty));

        var close = SqlAssistChrome.CreateIconButton(SqlIcon.Close, PreviewText.CloseButton);
        close.Focusable = false;
        close.Click += (_, _) => SqlAssistPlatformGuard.Run("關閉結構預覽", () => CloseRequested?.Invoke(this, EventArgs.Empty));

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_pin);
        tools.Children.Add(dock);
        tools.Children.Add(close);
        _panel.HeaderTools = tools;
        _panel.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var header = _panel.Header;
        header.MouseLeftButtonDown += OnHeaderMouseDown;
        header.MouseMove += OnHeaderMouseMove;
        header.MouseLeftButtonUp += (_, _) => header.ReleaseMouseCapture();
        header.LostMouseCapture += (_, _) => SqlAssistPlatformGuard.Run("結束搬動結構預覽", () =>
        {
            _headerPress = null;
            EndDrag(canceled: false);
        });

        _resizeLeft = CreateResizeThumb();
        _resizeLeft.HorizontalAlignment = HorizontalAlignment.Left;

        _resizeRight = CreateResizeThumb();
        _resizeRight.HorizontalAlignment = HorizontalAlignment.Right;

        // 握把放在整個內容的 overlay，落在上方時才能移到上緣而不受頁尾限制。
        _content = new Grid { RenderTransform = _contentScale };
        _content.Children.Add(_panel);
        _content.Children.Add(_resizeLeft);
        _content.Children.Add(_resizeRight);
        SetResizeGrips(onTop: false, pinned: false);

        _capsuleText = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = SurfaceCapsule.TextSize,
            TextTrimming = TextTrimming.CharacterEllipsis
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        _capsuleIcon.SetStatus(NotificationVisualStatus.Running, feedback: false);
        _capsule = SurfaceCapsule.CreateLayout(_capsuleIcon, _capsuleText);
        _capsule.Height = SurfaceCapsule.Height;
        _capsule.HorizontalAlignment = HorizontalAlignment.Left;
        _capsule.VerticalAlignment = VerticalAlignment.Top;
        _capsule.IsHitTestVisible = false;
        _capsule.Opacity = 0;
        AutomationProperties.SetLiveSetting(_capsule, AutomationLiveSetting.Polite);

        var surface = new Grid();
        surface.Children.Add(_content);
        surface.Children.Add(_capsule);

        var radius = new CornerRadius(SqlAssistChrome.FloatingSurfaceRadius);
        _root = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = radius,
            SnapsToDevicePixels = true,
            Child = surface
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground)
            .WithTheme(Border.BorderBrushProperty, ThemeBrush.Border);
        _root.SizeChanged += (_, _) => SqlAssistPlatformGuard.Probe("裁切結構預覽圓角", ApplyReveal);

        // 與通知島同一種浮層：柔影只掛在底色層並點陣快取，高對比退回實色、不畫影子。
        _shadow = new Border { CornerRadius = radius, IsHitTestVisible = false }
            .WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
        SqlAssistChrome.SetSurfaceShadow(_shadow, on: true);

        _frame = new Grid { Margin = new Thickness(ShadowMargin) };
        _frame.Children.Add(_shadow);
        _frame.Children.Add(_root);

        _holdTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _holdTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("展開結構預覽", Expand);

        Content = _frame;
        Focusable = false;
    }

    /// <summary>開始拖抬頭或角落握把；位移一律是相對按下瞬間的總量。</summary>
    public event EventHandler<PreviewDragEventArgs>? DragStarted;

    public event EventHandler<PreviewDragEventArgs>? DragDelta;

    public event EventHandler<PreviewDragEventArgs>? DragCompleted;

    public event EventHandler? SizeResetRequested;

    /// <summary>使用者按了圖釘；釘不釘由擁有者決定，再用 <see cref="SetPinned"/> 回寫。</summary>
    public event EventHandler? PinToggled;

    /// <summary>使用者要把這一份移到停靠的工具視窗。</summary>
    public event EventHandler? DockRequested;

    /// <summary>關閉鈕，或內容裡按下 Esc。</summary>
    public event EventHandler? CloseRequested;

    public SqlStructurePanel Panel => _panel;

    /// <summary>正在縮回；那段時間內容還掛在上一個承載視窗上。</summary>
    public bool IsExiting => _exitDone is not null;

    public bool HasOpenContextMenu => _panel.HasOpenContextMenu;

    /// <summary>只套用這一輪真正顯示的尺寸；不代表使用者的持久偏好。</summary>
    public void SetEffectiveSize(double width, double height)
    {
        _frame.Width = Math.Max(0, width - ShadowMargin * 2);
        _frame.Height = Math.Max(0, height - ShadowMargin * 2);
    }

    /// <summary>圖釘的外觀跟著擁有者的狀態，不跟著按鍵本身；釘住時抬頭顯示成可搬動。</summary>
    public void SetPinned(bool pinned)
    {
        _pin.IsChecked = pinned;
        _panel.Header.Cursor = pinned ? Cursors.SizeAll : null;
    }

    /// <summary>
    /// 握把擺在哪裡：錨在名稱上時只有遠離錨點那一側的右角，釘住時是下緣兩角。
    /// </summary>
    /// <remarks>
    /// 錨在名稱上的視窗左緣跟著錨點、靠錨點的那一緣跟著錨點行，使用者能決定的只有寬與高；
    /// 在那兩條邊上放握把，放開之後定位又把邊拉回錨點，看起來就是「怎麼拖都沒用」。
    /// 釘住的視窗整個矩形都歸使用者，抬頭負責搬、下緣兩角負責改尺寸。
    /// </remarks>
    public void SetResizeGrips(bool onTop, bool pinned)
    {
        // 每一輪定位都會走到；配置沒變就不重建變換。
        if (_grips == (onTop, pinned))
        {
            return;
        }

        _grips = (onTop, pinned);
        _resizeLeft.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        _resizeLeft.Tag = PreviewDragHandle.BottomLeft;
        _resizeLeft.VerticalAlignment = VerticalAlignment.Bottom;
        _resizeLeft.Cursor = Cursors.SizeNESW;
        _resizeLeft.RenderTransform = new ScaleTransform(-1, 1, GripSize / 2, GripSize / 2);
        AutomationProperties.SetName(_resizeLeft, PreviewText.ResizeBottomLeft);

        _resizeRight.Tag = onTop ? PreviewDragHandle.TopRight : PreviewDragHandle.BottomRight;
        _resizeRight.VerticalAlignment = onTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _resizeRight.Cursor = onTop ? Cursors.SizeNESW : Cursors.SizeNWSE;
        _resizeRight.RenderTransform = onTop ? new ScaleTransform(1, -1, GripSize / 2, GripSize / 2) : Transform.Identity;
        AutomationProperties.SetName(_resizeRight, onTop ? PreviewText.ResizeTopRight : PreviewText.ResizeBottomRight);

        // 下緣有握把時頁尾讓出一個握把寬，文字才不會壓在斜線底下。
        _panel.StatusInset = onTop ? 14 : 24;
    }

    public void CloseTransientPopups() => _panel.CloseTransientPopups();

    /// <summary>
    /// 眼前要畫的東西叫什麼、畫不畫得出來。
    /// </summary>
    /// <remarks>
    /// 名稱寫在等內容的膠囊上：使用者按下去之後第一個想確認的是「它要給我看的是不是這個」。
    /// 正停在膠囊時內容到了，照 <see cref="PreviewReveal"/> 停夠了才攤開。
    /// </remarks>
    public void SetContentState(string label, bool ready)
    {
        if (!string.Equals(_capsuleText.Text, label, StringComparison.Ordinal))
        {
            _capsuleText.Text = label;
            _capsuleWidth = SurfaceCapsule.Width(label);
            AutomationProperties.SetName(_capsule, PreviewText.CapsuleLoading(label));
            ApplyReveal();
        }

        _ready = ready;
        if (ready && _holding)
        {
            ScheduleExpand();
        }
    }

    /// <summary>
    /// 從錨點旁長出來：圓點先長成膠囊，內容到了才攤開，內容晚一點淡入並微微放大。
    /// </summary>
    /// <remarks>
    /// 內容已經在手上（快取命中是常態）時膠囊只是出發的形狀，一路攤開，與通知島展開同一個節奏。
    /// 內容還沒到時停在膠囊轉著進度圈，不先攤開一片只有標題的空表面——那一片是在等，卻看起來像
    /// 這個物件什麼都沒有。
    /// </remarks>
    /// <param name="origin">錨點在表面裡的水平位置（DIP）；膠囊從那裡出發。</param>
    /// <param name="fromBottom">預覽在錨點上方時從下緣長上去。</param>
    public void PlayEnter(double origin, bool fromBottom)
    {
        CompleteExit();
        StopHold();
        _revealOrigin = origin;
        _revealFromBottom = fromBottom;
        _contentScale.CenterX = Math.Max(0, origin);
        _contentScale.CenterY = fromBottom ? Math.Max(0, _root.ActualHeight > 0 ? _root.ActualHeight : _frame.Height) : 0;
        SqlAssistChrome.PlayAppear(_frame);
        if (!SqlAssistChrome.MotionEnabled)
        {
            StopReveal();
            HideCapsule(motion: false);
            SurfaceMotion.ResetContent(_content, _contentScale);
            ApplyReveal();
            return;
        }

        StopReveal();
        _seed = new SpringMotion(0, _ => ApplyReveal(), SeedSpring);
        _revealWidth = new SpringMotion(0, _ => ApplyReveal(), GrowWidth);
        _revealHeight = new SpringMotion(0, _ => ApplyReveal(), GrowHeight);
        ApplyReveal();
        _seed.AnimateTo(1, motion: true);

        if (_ready)
        {
            HideCapsule(motion: false);
            Expand();
            return;
        }

        BeginHold();
    }

    /// <summary>
    /// 縮回膠囊，收完才呼叫 <paramref name="done"/>；動畫關著時立刻呼叫。
    /// </summary>
    /// <remarks>
    /// 內容先淡出、外形再縮，順序與通知島收起相同；整個表面同時淡掉，收起不必等。
    /// 換到別的名稱時膠囊上寫著下一個名稱：舊位置收成它，新位置再從它長出來，看得出是同一扇窗換了地方。
    /// </remarks>
    /// <param name="relocating">要換到新位置：膠囊寫著下一個名稱，而不是空著收掉。</param>
    public void PlayExit(bool relocating, Action done)
    {
        CompleteExit();
        StopHold();
        if (!SqlAssistChrome.MotionEnabled || _root.ActualWidth <= 0)
        {
            HideCapsule(motion: false);
            done();
            return;
        }

        _exitDone = done;
        SurfaceMotion.ExitContent(_content);
        if (relocating)
        {
            ShowCapsule(spin: false);
        }
        else
        {
            HideCapsule(motion: true);
        }

        var fade = SurfaceMotion.Ease(_frame.Opacity, 0, ExitFade);
        fade.BeginTime = SurfaceMotion.Duration(SurfaceMotion.ContentDelay);
        _frame.BeginAnimation(OpacityProperty, fade);
        Reveal(to: 0, ExitSpring, ExitSpring, done: CompleteExit);
    }

    /// <summary>
    /// 立刻結束縮回：內容只有一份，下一個承載視窗要掛上它之前必須先收完。
    /// </summary>
    public void CompleteExit()
    {
        if (_exitDone is not { } done)
        {
            return;
        }

        _exitDone = null;
        StopReveal();
        _frame.BeginAnimation(OpacityProperty, null);
        _frame.Opacity = 1;
        HideCapsule(motion: false);
        SurfaceMotion.ResetContent(_content, _contentScale);
        ApplyReveal();
        done();
    }

    /// <summary>釘住的窗原地換成另一個物件：外形不動，新內容浮上來。</summary>
    public void PlaySwap()
    {
        if (_exitDone is not null || _holding || !SqlAssistChrome.MotionEnabled)
        {
            return;
        }

        SurfaceMotion.SwapContent(_content);
    }

    public void Dispose()
    {
        StopHold();
        CompleteExit();
        StopReveal();
        _capsuleIcon.StopMotion();
        _panel.Dispose();
    }

    /// <summary>停在膠囊等內容：名稱先出來，進度圈過了寬限才浮現，到上限還沒到就照樣攤開。</summary>
    private void BeginHold()
    {
        _holding = true;
        _holdClock.Restart();
        _content.BeginAnimation(OpacityProperty, null);
        _content.Opacity = 0;
        ShowCapsule(spin: true);
        _holdTimer.Interval = PreviewReveal.Ceiling;
        _holdTimer.Start();
    }

    private void ScheduleExpand()
    {
        var wait = PreviewReveal.HoldAfterReady(_holdClock.Elapsed);
        _holdTimer.Stop();
        if (wait <= TimeSpan.Zero)
        {
            Expand();
            return;
        }

        _holdTimer.Interval = wait;
        _holdTimer.Start();
    }

    /// <summary>從膠囊攤開成整個表面；內容晚一步淡入並微微放大，膠囊上的字同時淡掉。</summary>
    private void Expand()
    {
        StopHold();
        if (_revealWidth is null || _revealHeight is null)
        {
            SurfaceMotion.ResetContent(_content, _contentScale);
            ApplyReveal();
            return;
        }

        HideCapsule(motion: true);
        _revealWidth.AnimateTo(1, motion: true);
        _revealHeight.AnimateTo(1, motion: true);
        SurfaceMotion.EnterContent(_content, _contentScale);
    }

    private void StopHold()
    {
        _holding = false;
        _holdTimer.Stop();
        _holdClock.Reset();
    }

    /// <summary>膠囊的名稱立刻出來；進度圈過了寬限才浮現，快取命中時根本看不到它。</summary>
    private void ShowCapsule(bool spin)
    {
        _capsule.BeginAnimation(OpacityProperty, null);
        _capsule.Opacity = 1;
        _capsule.BeginAnimation(OpacityProperty, SurfaceMotion.Ease(0, 1, SurfaceMotion.ContentFadeIn));

        _capsuleIcon.BeginAnimation(OpacityProperty, null);
        _capsuleIcon.Opacity = spin ? 1 : 0;
        _capsuleIcon.Spin(spin);
        if (spin)
        {
            _capsuleIcon.BeginAnimation(OpacityProperty, SurfaceMotion.Delayed(
                0, 1, (int)PreviewReveal.Grace.TotalMilliseconds, SurfaceMotion.ContentFadeIn));
        }
    }

    private void HideCapsule(bool motion)
    {
        _capsuleIcon.Spin(false);
        if (!motion || _capsule.Opacity <= 0)
        {
            _capsule.BeginAnimation(OpacityProperty, null);
            _capsule.Opacity = 0;
            return;
        }

        SurfaceMotion.ExitContent(_capsule);
    }

    private void Reveal(double to, SpringParameters width, SpringParameters height, Action? done)
    {
        var startWidth = _revealWidth?.Value ?? 1;
        var startHeight = _revealHeight?.Value ?? 1;
        var seed = _seed?.Value ?? 1;
        StopReveal();
        _seed = new SpringMotion(seed, _ => ApplyReveal(), SeedSpring);
        _revealWidth = new SpringMotion(startWidth, _ => ApplyReveal(), width);
        _revealHeight = new SpringMotion(startHeight, _ => ApplyReveal(), height);

        // 高度那一條比較慢，它停下來才算收完。
        if (done is not null)
        {
            _revealHeight.Settled += (_, _) => SqlAssistPlatformGuard.Run("收起結構預覽", done);
        }

        ApplyReveal();
        _revealWidth.AnimateTo(to, motion: true);
        _revealHeight.AnimateTo(to, motion: true);
    }

    private void StopReveal()
    {
        _seed?.Stop();
        _revealWidth?.Stop();
        _revealHeight?.Stop();
        _seed = null;
        _revealWidth = null;
        _revealHeight = null;
    }

    /// <summary>依圓點、寬與高的揭露進度裁切表面與柔影；沒有動畫時只留圓角裁切。</summary>
    private void ApplyReveal()
    {
        var width = _root.ActualWidth > 0 ? _root.ActualWidth : _frame.Width;
        var height = _root.ActualHeight > 0 ? _root.ActualHeight : _frame.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            return;
        }

        // 彈簧會微微衝過頭，但表面之外畫不出東西，外形在 1 停住；圓點可以多長一點點，那是它的回彈。
        var seed = Math.Max(0, Math.Min(1.03, _seed?.Value ?? 1));
        var px = Math.Max(0, Math.Min(1, _revealWidth?.Value ?? 1));
        var py = Math.Max(0, Math.Min(1, _revealHeight?.Value ?? 1));
        var fullCapsuleWidth = Math.Min(width, _capsuleWidth);
        var fullCapsuleHeight = Math.Min(height, SurfaceCapsule.Height);
        var dot = Math.Min(SurfaceCapsule.DotSize, fullCapsuleHeight);
        var capsuleWidth = Math.Min(width, dot + (fullCapsuleWidth - dot) * seed);
        var capsuleHeight = Math.Min(height, dot + (fullCapsuleHeight - dot) * seed);
        var visibleWidth = capsuleWidth + (width - capsuleWidth) * px;
        var visibleHeight = capsuleHeight + (height - capsuleHeight) * py;

        // 膠囊從錨點下方出發，展開時左緣一路退回表面左緣。
        var start = Math.Max(0, Math.Min(width - fullCapsuleWidth, _revealOrigin));
        var left = start * (1 - px);
        var top = _revealFromBottom ? height - visibleHeight : 0;

        // 膠囊的字停在膠囊站穩的位置，外形往外攤開時它原地淡掉，不跟著滑走。
        _capsule.Width = fullCapsuleWidth;
        _capsule.Margin = new Thickness(start, _revealFromBottom ? height - fullCapsuleHeight : 0, 0, 0);

        // 膠囊是全圓角，長開之後收成表面的圓角；圓角不超過目前高度的一半。
        var surfaceRadius = SqlAssistChrome.FloatingSurfaceRadius;
        var radius = Math.Min(visibleHeight / 2, capsuleHeight / 2 + (surfaceRadius - capsuleHeight / 2) * py);

        var surface = new RectangleGeometry(new Rect(left, top, visibleWidth, visibleHeight), radius, radius);
        surface.Freeze();
        _root.Clip = surface;

        if (px >= 1 && py >= 1)
        {
            _shadow.Clip = null;
            return;
        }

        // 影子跟著露出的那一塊走，外面多留一圈讓模糊長得出來。
        var shadow = new RectangleGeometry(
            new Rect(left - ShadowMargin, top - ShadowMargin, visibleWidth + ShadowMargin * 2, visibleHeight + ShadowMargin * 2),
            radius + ShadowMargin,
            radius + ShadowMargin);
        shadow.Freeze();
        _shadow.Clip = shadow;
    }

    private void OnResizeDragStarted(object sender, DragStartedEventArgs eventArgs) =>
        BeginDrag(sender is Thumb { Tag: PreviewDragHandle handle } ? handle : PreviewDragHandle.BottomRight, CursorOnScreen());

    /// <summary>
    /// 依游標相對於按下瞬間的位移重算。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <see cref="DragDeltaEventArgs"/> 帶來的位移量：那是相對於握把的父代
    /// 算出來的，而浮動視窗在調整大小的過程中會被平台重新定位，父代自己在動，
    /// 於是視窗的移動會被誤算成滑鼠的移動而形成回授，畫面就開始亂跳。
    /// 以游標的螢幕座標重算，結果是「起始矩形 ＋ 游標位移」這個純函式，不受視窗移動影響。
    /// </remarks>
    private void OnResizeDragDelta(object sender, DragDeltaEventArgs eventArgs) => ReportDrag(CursorOnScreen());

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs eventArgs) =>
        EndDrag(eventArgs.Canceled);

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        // 按鈕自己會把按下標成已處理；雙擊留給以後，不當成搬動。
        if (eventArgs.Handled || eventArgs.ClickCount > 1)
        {
            return;
        }

        eventArgs.Handled = true;
        SqlAssistPlatformGuard.Run("按下結構預覽抬頭", () =>
        {
            _headerPress = CursorOnScreen();
            _panel.Header.CaptureMouse();
        });
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (_headerPress is not { } press || !_panel.Header.IsMouseCaptured)
        {
            return;
        }

        SqlAssistPlatformGuard.Run("搬動結構預覽", () =>
        {
            var current = CursorOnScreen();
            if (_dragHandle is null)
            {
                // 沒拖出系統的拖曳門檻就只是點一下；門檻是 DIP，游標位置是實體像素。
                var dpi = VisualTreeHelper.GetDpi(this);
                var moved = current - press;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
                    Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)
                {
                    return;
                }

                BeginDrag(PreviewDragHandle.Move, press);
            }

            ReportDrag(current);
        });
    }

    /// <summary>
    /// 游標的螢幕實體像素位置；定位那一端用的也是實體像素。
    /// </summary>
    /// <remarks>
    /// 原生游標取不到時退回 WPF 的滑鼠位置換到螢幕：兩者都是絕對位置，與元素本身有沒有被移動無關。
    /// </remarks>
    private Point CursorOnScreen() =>
        NativeCursor.TryGetPosition() ?? PointToScreen(Mouse.GetPosition(this));

    private void BeginDrag(PreviewDragHandle handle, Point origin)
    {
        _dragHandle = handle;
        _dragOrigin = origin;
        _dragChange = default;
        DragStarted?.Invoke(this, new PreviewDragEventArgs(handle, 0, 0));
    }

    private void ReportDrag(Point cursor)
    {
        if (_dragHandle is not { } handle)
        {
            return;
        }

        _dragChange = cursor - _dragOrigin;
        DragDelta?.Invoke(this, new PreviewDragEventArgs(handle, _dragChange.X, _dragChange.Y));
    }

    private void EndDrag(bool canceled)
    {
        if (_dragHandle is not { } handle)
        {
            return;
        }

        _dragHandle = null;
        DragCompleted?.Invoke(this, new PreviewDragEventArgs(handle, _dragChange.X, _dragChange.Y, canceled));
    }

    private void OnResizeDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        _panel.ShowStatus(PreviewText.SizeReset);
        SizeResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>角落握把；擺在哪一角、朗讀名稱與游標由 <see cref="SetResizeGrips"/> 決定。</summary>
    private Thumb CreateResizeThumb()
    {
        var thumb = new Thumb
        {
            Width = GripSize,
            Height = GripSize,
            Focusable = false,
            Template = CreateResizeGripTemplate(),
            ToolTip = PreviewText.ResizeToolTip
        };
        thumb.DragStarted += OnResizeDragStarted;
        thumb.DragDelta += OnResizeDragDelta;
        thumb.DragCompleted += OnResizeDragCompleted;
        thumb.MouseDoubleClick += OnResizeDoubleClick;
        return thumb;
    }

    /// <summary>
    /// 左右兩側、上下落點共用的縮放握把。
    /// </summary>
    /// <remarks>
    /// 自己畫三條斜線而不是用 <see cref="ResizeGrip"/>：後者的預設樣式假設自己在
    /// 視窗的狀態列裡，放在浮動視窗上不一定畫得出來。
    /// </remarks>
    private static ControlTemplate CreateResizeGripTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));

        // 透明底色讓整個 16×16 都吃得到滑鼠，只有線條本身可以拖曳會很難點。
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var lines = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        lines.SetValue(
            System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M 2,14 L 14,2 M 6,14 L 14,6 M 10,14 L 14,10"));
        lines.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, ThemeBrush.DimForeground);
        lines.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.0);
        lines.SetValue(IsHitTestVisibleProperty, false);
        root.AppendChild(lines);

        template.VisualTree = root;
        return template;
    }
}
