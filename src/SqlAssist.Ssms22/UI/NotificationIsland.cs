using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22.Notifications;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知島：單一表面，以彈簧同時變形寬、高與圓角，承載膠囊、活動清單與提醒。
/// </summary>
/// <remarks>
/// 形態由 <see cref="NotificationIslandState"/> 決定，這裡只負責畫出那個形態。內容一律依目標
/// 尺寸排版、靠右下對齊，再用動畫中的圓角裁切露出來：變形過程中量到的是固定的目標寬度，
/// 文字不跟著每一幀重新換行。錨點是右下角，展開與提醒都往左上長。
///
/// 只認得 <see cref="NotificationIslandContent"/>；按鈕、叉號與衛星都只是把事件交出去，
/// 處理在呼叫端。循環動畫只允許膠囊或清單抬頭上的那一個進度圈，衛星與各列都是靜態的。
/// </remarks>
internal sealed class NotificationIsland : Grid
{
    public const double CapsuleHeight = 32;
    public const double CapsuleRadius = 16;
    public const double CapsuleMinWidth = 160;
    public const double CapsuleMaxWidth = 320;
    public const double PanelWidth = 320;
    public const double PanelRadius = 14;

    /// <summary>出現時從這麼大的圓點長出來，消失時縮回它再淡掉。</summary>
    public const double DotSize = 12;

    public const double SatelliteSize = 32;
    public const double SatelliteGap = 8;
    public const double DetailMaxHeight = 240;

    /// <summary>疊起來的提醒，後面每一層往上露出多少。</summary>
    public const double StackPeek = 5;

    /// <summary>
    /// 島嶼最大的外框（含衛星與疊層，不含柔影）。
    /// </summary>
    /// <remarks>
    /// 浮層視窗依它固定大小：變形途中改視窗大小，每一影格都是一次 SetWindowPos 加上整個分層視窗重新合成。
    /// 高度的上限是展開清單：外距 14、抬頭 24、文件列 19、漸層條 2、明細上距 8 與 <see cref="DetailMaxHeight"/>，
    /// 共 307，取整到 320；三行訊息的提醒加上兩層疊層不到 150。
    /// </remarks>
    public static readonly Size MaxExtent = new(PanelWidth + SatelliteGap + SatelliteSize, 320);

    private const string Cross = "M1,1 L11,11 M11,1 L1,11";
    private const string Running = "M8,1 A7,7 0 1 1 1,8";
    private const string Check = "M3,8 L6.5,11.5 L13,4.5";
    private const string Alert = "M8,1 L15,14 L1,14 Z M8,5 L8,9 M8,11 L8,12";

    private readonly Grid _island = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _surface;
    private readonly Border _sheen;
    private readonly Border[] _layers = new Border[2];
    private readonly Grid _viewport;
    private readonly RectangleGeometry _clip = new();
    private readonly SpringMotion _width;
    private readonly SpringMotion _height;
    private readonly SpringMotion _radius;

    private readonly Button _satellite;
    private readonly Path _satelliteRing;
    private readonly SqlIconImage _satelliteWarning;
    private readonly ScaleTransform _satelliteScale = new(1, 1);
    private readonly TranslateTransform _satelliteShake = new();

    private readonly Grid _capsule;
    private readonly Path _capsuleIcon;
    private readonly TextBlock _capsuleText;
    private readonly RotateTransform _capsuleSpin = new();
    private readonly ScaleTransform _capsuleScale = new(1, 1);
    private readonly TranslateTransform _capsuleShake = new();

    private readonly Grid _list;
    private readonly Path _listIcon;
    private readonly RotateTransform _listSpin = new();
    private readonly TextBlock _listSummary;
    private readonly TextBlock _listDocument;
    private readonly ScaleTransform _progress = new(0, 1);
    private readonly ScrollViewer _details;
    private readonly StackPanel _rows = new();
    private readonly Dictionary<long, NotificationRow> _rowMap = new();

    private readonly NotificationPromptView[] _prompts = new NotificationPromptView[2];
    private int _activePrompt;

    private FrameworkElement? _current;
    private bool _shown;
    private bool _hiding;
    private bool _capsuleSpinning;
    private bool _listSpinning;
    private int _lastShake;
    private int _capsuleState = -1;
    private string _announced = "";
    private bool? _glass;

    public NotificationIsland()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        SnapsToDevicePixels = true; UseLayoutRounding = true;
        Visibility = Visibility.Collapsed;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 衛星先加入：它在島嶼左邊，Tab 順序跟視覺順序一致。
        _satelliteRing = new Path { StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _satelliteRing.SetResourceReference(Path.StrokeProperty, ThemeResourceSet.NotificationSpinnerKey);
        var track = new Ellipse { Width = 26, Height = 26, StrokeThickness = 2.5 }.WithTheme(Path.StrokeProperty, ThemeBrush.SegmentTrack);
        _satelliteWarning = SqlAssistChrome.CreateIcon(SqlIcon.Warning);
        _satelliteWarning.Visibility = Visibility.Collapsed;
        var ring = new Grid { Width = SatelliteSize, Height = SatelliteSize };
        ring.Children.Add(track); ring.Children.Add(_satelliteRing); ring.Children.Add(_satelliteWarning);
        _satellite = SqlAssistChrome.CreateNotificationSatellite("通知");
        _satellite.Content = ring;
        _satellite.VerticalAlignment = VerticalAlignment.Bottom;
        _satellite.Margin = new Thickness(0, 0, SatelliteGap, 0);
        _satellite.Visibility = Visibility.Collapsed;
        _satellite.RenderTransformOrigin = new Point(0.5, 0.5);
        _satellite.RenderTransform = Group(_satelliteScale, _satelliteShake);
        _satellite.Click += (_, _) => SatelliteClicked?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetLiveSetting(_satellite, AutomationLiveSetting.Polite);
        Children.Add(_satellite);

        for (var index = _layers.Length - 1; index >= 0; index--)
        {
            var scale = index == 0 ? 0.96 : 0.92;
            _layers[index] = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0),
                RenderTransform = Group(new ScaleTransform(scale, scale), new TranslateTransform(0, -StackPeek * (index + 1))),
            };
            _island.Children.Add(_layers[index]);
        }

        _sheen = new Border { IsHitTestVisible = false }.WithThemeKey(Border.BackgroundProperty, ThemeResourceSet.NotificationSheenKey);
        _surface = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            BorderThickness = new Thickness(1), Child = _sheen,
        };
        _island.Children.Add(_surface);
        _viewport = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Clip = _clip,
        };
        _island.Children.Add(_viewport);
        SetColumn(_island, 1);
        Children.Add(_island);

        DismissButton = SqlAssistChrome.CreateNotificationButton(NotificationCatalog.DismissActivities, Cross);
        DismissButton.VerticalAlignment = VerticalAlignment.Center;
        DismissButton.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        _capsule = CreateCapsule(out _capsuleIcon, out _capsuleText);
        _list = CreateList(out _listIcon, out _listSummary, out _listDocument, out _details);
        _viewport.Children.Add(_capsule);
        _viewport.Children.Add(_list);
        for (var index = 0; index < _prompts.Length; index++)
        {
            var view = new NotificationPromptView
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(1, 1),
                RenderTransform = Group(new ScaleTransform(1, 1), new TranslateTransform()),
            };
            view.ActionInvoked += (id, action) => PromptResolved?.Invoke(id, action);
            _prompts[index] = view;
            _viewport.Children.Add(view);
        }

        _width = new SpringMotion(DotSize, value => { SetWidth(value); UpdateClip(); });
        _height = new SpringMotion(DotSize, value => { SetHeight(value); UpdateClip(); });
        _radius = new SpringMotion(DotSize / 2, value => { SetRadius(value); UpdateClip(); });
        _width.Settled += OnShapeSettled;
        SetWidth(DotSize); SetHeight(DotSize); SetRadius(DotSize / 2); UpdateClip();
        SetOptions(glass: true, highContrast: SystemParameters.HighContrast);
    }

    /// <summary>按了提醒上的按鈕（識別字）或叉號（null）。</summary>
    public event Action<long, string?>? PromptResolved;

    /// <summary>點了衛星：呼叫端交給狀態機切去看活動或切回提醒。</summary>
    public event EventHandler? SatelliteClicked;

    /// <summary>活動清單的叉號：這一批看完了，不取消工作。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>收場播完、整個收起來了；浮層在這時才隱藏視窗，否則收場動畫會被一起藏掉。</summary>
    public event EventHandler? Vanished;

    /// <summary>這一輪的形態；由狀態機給。</summary>
    public NotificationIslandShape Shape { get; private set; } = NotificationIslandShape.Hidden;

    /// <summary>島嶼變形完之後的大小（含衛星與疊層），給浮層定位用。</summary>
    public Size TargetSize { get; private set; }

    internal FrameworkElement? CurrentContent => _current;
    internal Button Satellite => _satellite;
    internal Button DismissButton { get; }
    internal NotificationPromptView ActivePrompt => _prompts[_activePrompt];
    internal Border Surface => _surface;
    internal IReadOnlyList<Border> StackLayers => _layers;
    internal ScrollViewer Details => _details;
    internal StackPanel Rows => _rows;
    internal TextBlock DocumentLabel => _listDocument;
    internal ScaleTransform Progress => _progress;
    internal bool IsSpinning => _capsuleSpinning || _listSpinning;
    internal (SpringMotion Width, SpringMotion Height, SpringMotion Radius) Springs => (_width, _height, _radius);

    public void SetOptions(bool glass, bool highContrast)
    {
        glass &= !highContrast;
        if (_glass == glass) return;
        _glass = glass;
        SqlAssistChrome.ApplyNotificationMaterial(_surface, _sheen, glass);
        foreach (var layer in _layers)
        {
            // 疊層只有底色與邊緣，不帶柔影：三層柔影疊在一起會變成一團黑。
            if (glass)
            {
                layer.SetResourceReference(Border.BackgroundProperty, ThemeResourceSet.NotificationGlassKey);
                layer.SetResourceReference(Border.BorderBrushProperty, ThemeResourceSet.NotificationRimKey);
            }
            else
            {
                layer.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
                layer.WithTheme(Border.BorderBrushProperty, ThemeBrush.Border);
            }
        }

        if (glass)
        {
            _satellite.SetResourceReference(Control.BackgroundProperty, ThemeResourceSet.NotificationGlassKey);
            _satellite.SetResourceReference(Control.BorderBrushProperty, ThemeResourceSet.NotificationRimKey);
        }
        else
        {
            _satellite.WithTheme(Control.BackgroundProperty, ThemeBrush.ListBackground);
            _satellite.WithTheme(Control.BorderBrushProperty, ThemeBrush.Border);
        }

        _satellite.Effect = _surface.Effect is null ? null : new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.14 };
        SqlAssistChrome.UpdateNotificationShadowCache(_satellite);
    }

    /// <summary>畫出這一輪的形態與內容。</summary>
    /// <param name="motion">動畫開著；關著時所有尺寸直接到位、不播任何回饋。</param>
    public void Update(NotificationIslandContent content, NotificationIslandState state, bool motion)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        if (state is null) throw new ArgumentNullException(nameof(state));
        var previous = Shape;
        var previousPrompt = ActivePrompt.Item;
        Shape = state.Shape;
        if (Shape == NotificationIslandShape.Hidden) { Hide(motion); return; }

        var appearing = !_shown;
        _shown = true; _hiding = false;
        Visibility = Visibility.Visible;
        if (appearing)
        {
            // 只在出現的那一刻重設：週期刷新每 100 ms 一次，每次都重設會把 120 ms 的入場淡入砍掉。
            _island.BeginAnimation(OpacityProperty, null); _island.Opacity = 1;
            // 收場時內容先淡到 0；重新出現的是同一份內容的話要先交還不透明度。
            if (_current is { } shownBefore) { shownBefore.BeginAnimation(OpacityProperty, null); shownBefore.Opacity = 1; }
        }

        FrameworkElement next;
        Size target;
        var slide = false;
        switch (Shape)
        {
            case NotificationIslandShape.Compact:
            case NotificationIslandShape.Done:
                UpdateCapsule(content, state, motion);
                target = new Size(CapsuleWidth(_capsuleText.Text), CapsuleHeight);
                next = _capsule;
                break;
            case NotificationIslandShape.Expanded:
                UpdateList(content, motion);
                target = new Size(PanelWidth, Measure(_list));
                next = _list;
                break;
            default:
                var top = content.Prompts[0];
                if (ActivePrompt.Item?.Id != top.Id && ActivePrompt.Item is not null) _activePrompt = 1 - _activePrompt;
                var view = ActivePrompt;
                view.Update(top);
                target = new Size(PanelWidth, Measure(view));
                next = view;
                // 疊著的提醒處理掉一則：下一則從下面滑上來，而不是原地換字。
                slide = IsPrompt(previous) && previousPrompt is not null && previousPrompt.Id != top.Id &&
                    top.Count < previousPrompt.Count;
                break;
        }

        // 目標尺寸含內容自己的外距；排版寬高要扣掉，否則靠右下對齊時整份內容往左上多推出一圈。
        next.Width = Math.Max(0, target.Width - next.Margin.Left - next.Margin.Right);
        next.Height = Math.Max(0, target.Height - next.Margin.Top - next.Margin.Bottom);
        var radius = Shape is NotificationIslandShape.Compact or NotificationIslandShape.Done ? CapsuleRadius : PanelRadius;
        var layers = Shape == NotificationIslandShape.PromptStack ? Math.Min(_layers.Length, content.Prompts.Count - 1) : 0;
        for (var index = 0; index < _layers.Length; index++)
            _layers[index].Visibility = index < layers ? Visibility.Visible : Visibility.Collapsed;

        UpdateSatellite(content, state, motion);
        if (appearing)
        {
            _width.Jump(motion ? DotSize : target.Width);
            _height.Jump(motion ? DotSize : target.Height);
            _radius.Jump(motion ? DotSize / 2 : radius);
            if (motion) _island.BeginAnimation(OpacityProperty, NotificationMotion.Ease(0, 1, NotificationMotion.ContentFadeOut));
        }

        _width.AnimateTo(target.Width, motion);
        _height.AnimateTo(target.Height, motion);
        _radius.AnimateTo(radius, motion);
        Swap(next, motion && !appearing, slide);
        UpdateSpin(content, motion);
        UpdateShake(state, motion);
        UpdateAnnouncement(content, previous);
        TargetSize = new Size(
            target.Width + (state.Satellite ? SatelliteSize + SatelliteGap : 0),
            Math.Max(target.Height + StackPeek * layers, state.Satellite ? SatelliteSize : 0));
    }

    /// <summary>停掉所有動畫並把尺寸放到目標；表面離開畫面或動畫關掉時用。</summary>
    public void StopMotion()
    {
        _width.Jump(_width.Target); _height.Jump(_height.Target); _radius.Jump(_radius.Target);
        SetSpin(_capsuleSpin, ref _capsuleSpinning, false);
        SetSpin(_listSpin, ref _listSpinning, false);
        NotificationMotion.StopResult(_capsuleScale, _capsuleShake);
        NotificationMotion.StopResult(_satelliteScale, _satelliteShake);
        _satellite.BeginAnimation(OpacityProperty, null);
        _island.BeginAnimation(OpacityProperty, null); _island.Opacity = 1;
        foreach (var row in _rowMap.Values) row.SuspendMotion();
        foreach (FrameworkElement child in _viewport.Children)
        {
            child.BeginAnimation(OpacityProperty, null);
            child.Opacity = 1;
            if (!ReferenceEquals(child, _current)) child.Visibility = Visibility.Collapsed;
        }

        if (!_shown) Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 立刻收起、不播收場，也不發 <see cref="Vanished"/>；下一次 <see cref="Update"/> 從圓點重新長出來。
    /// </summary>
    /// <remarks>換擁有者時用：浮層先隱藏再換位置，舊位置上的收場沒有人看得到。</remarks>
    public void Reset()
    {
        _shown = false; _hiding = false;
        SetSpin(_capsuleSpin, ref _capsuleSpinning, false);
        SetSpin(_listSpin, ref _listSpinning, false);
        _satellite.Visibility = Visibility.Collapsed;
        TargetSize = new Size(DotSize, DotSize);
        StopMotion();
        Visibility = Visibility.Collapsed;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SqlAssistChrome.UpdateNotificationShadowCache(_surface);
        SqlAssistChrome.UpdateNotificationShadowCache(_satellite);
    }

    private void Hide(bool motion)
    {
        if (!_shown) return;
        _shown = false;
        SetSpin(_capsuleSpin, ref _capsuleSpinning, false);
        SetSpin(_listSpin, ref _listSpinning, false);
        _satellite.Visibility = Visibility.Collapsed;
        TargetSize = new Size(DotSize, DotSize);
        if (!motion)
        {
            StopMotion();
            Visibility = Visibility.Collapsed;
            Vanished?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 先縮回圓點，停下來之後才淡掉（OnShapeSettled）；內容先淡出，縮的過程中不露出被裁的半行字。
        _hiding = true;
        if (_current is { } current) current.BeginAnimation(OpacityProperty, NotificationMotion.Ease(current.Opacity, 0, NotificationMotion.ContentFadeOut));
        _width.AnimateTo(DotSize, motion: true);
        _height.AnimateTo(DotSize, motion: true);
        _radius.AnimateTo(DotSize / 2, motion: true);
        if (!_width.IsActive) OnShapeSettled(this, EventArgs.Empty);
    }

    private void OnShapeSettled(object? sender, EventArgs args)
    {
        if (!_hiding) return;
        _hiding = false;
        var fade = NotificationMotion.Ease(_island.Opacity, 0, NotificationMotion.Exit);
        fade.Completed += (_, _) =>
        {
            if (_shown) return;
            Visibility = Visibility.Collapsed;
            _island.BeginAnimation(OpacityProperty, null); _island.Opacity = 1;
            Vanished?.Invoke(this, EventArgs.Empty);
        };
        _island.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// 換內容：舊的 120 ms 淡出；新的晚 60 ms 才開始，180 ms 淡入並從 0.96 放大到 1。
    /// </summary>
    private void Swap(FrameworkElement next, bool motion, bool slide)
    {
        var old = _current;
        _current = next;
        next.Visibility = Visibility.Visible;
        var (scale, shift) = Transforms(next);
        if (ReferenceEquals(old, next))
        {
            // 同一份內容剛被換走又換回來（例如衛星來回點）：從目前的透明度接回 1。
            // 被換走的那一份基底值仍是 1、只有動畫在往 0 走；正在淡入的那一份基底值是 0，不去打斷它。
            if (next.Opacity < 1 && !motion) { next.BeginAnimation(OpacityProperty, null); next.Opacity = 1; }
            else if (next.Opacity < 1 && next.GetAnimationBaseValue(OpacityProperty) is double baseline && baseline >= 1)
                next.BeginAnimation(OpacityProperty, NotificationMotion.Ease(next.Opacity, 1, NotificationMotion.ContentFadeIn));
            return;
        }

        if (old is not null)
        {
            if (motion)
            {
                // 從目前畫面上的透明度接續；先清動畫會退回基底值，淡入到一半的那一份會先閃一下。
                var fade = NotificationMotion.Ease(old.Opacity, 0, NotificationMotion.ContentFadeOut);
                fade.Completed += (_, _) => { if (!ReferenceEquals(old, _current)) old.Visibility = Visibility.Collapsed; };
                old.BeginAnimation(OpacityProperty, fade);
            }
            else { old.BeginAnimation(OpacityProperty, null); old.Visibility = Visibility.Collapsed; }
        }

        next.BeginAnimation(OpacityProperty, null);
        scale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        shift?.BeginAnimation(TranslateTransform.YProperty, null);
        if (!motion) { next.Opacity = 1; return; }

        var delay = NotificationMotion.Duration(NotificationMotion.ContentDelay);
        var appear = NotificationMotion.Ease(0, 1, NotificationMotion.ContentFadeIn);
        appear.BeginTime = delay;
        next.Opacity = 0;
        next.BeginAnimation(OpacityProperty, appear);
        if (scale is not null)
        {
            var grow = NotificationMotion.Ease(NotificationMotion.ContentScaleFrom, 1, NotificationMotion.ContentFadeIn);
            grow.BeginTime = delay; grow.FillBehavior = FillBehavior.Stop;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        if (slide && shift is not null)
        {
            var rise = NotificationMotion.Ease(StackPeek * 2, 0, NotificationMotion.ContentFadeIn);
            rise.BeginTime = delay; rise.FillBehavior = FillBehavior.Stop;
            shift.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    private static bool IsPrompt(NotificationIslandShape shape) => shape is NotificationIslandShape.Prompt
        or NotificationIslandShape.PromptWithSatellite or NotificationIslandShape.PromptStack;

    private static (ScaleTransform?, TranslateTransform?) Transforms(UIElement element) =>
        element.RenderTransform is TransformGroup group
            ? (group.Children.OfType<ScaleTransform>().FirstOrDefault(), group.Children.OfType<TranslateTransform>().FirstOrDefault())
            : (null, null);

    private void UpdateCapsule(NotificationIslandContent content, NotificationIslandState state, bool motion)
    {
        _capsuleText.Text = content.Summary; _capsuleText.ToolTip = content.Summary;
        var running = content.Running > 0;
        // 0 執行中、1 有失敗、2 成功、3 其餘（取消）；只在變的那一刻換圖示與播回饋。
        var capsuleState = running ? 0 : state.Warning ? 1 : content.Completed > 0 ? 2 : 3;
        if (capsuleState == _capsuleState) return;
        var from = _capsuleState;
        _capsuleState = capsuleState;
        NotificationMotion.StopResult(_capsuleScale, _capsuleShake);
        ApplyStatusIcon(_capsuleIcon, capsuleState);
        // 失敗的短震由 ShakeCount 決定，這裡只補成功的微彈出。
        if (motion && from == 0 && capsuleState == 2) NotificationMotion.PlayPop(_capsuleScale);
    }

    private void UpdateList(NotificationIslandContent content, bool motion)
    {
        var items = content.Activities;
        ApplyStatusIcon(_listIcon, content.Running > 0 ? 0 : content.Failed > 0 ? 1 : content.Completed > 0 ? 2 : 3);
        var summary = NotificationCatalog.ProgressSummary(content.Completed, items.Count);
        _listSummary.Text = summary; _listSummary.ToolTip = summary;
        var document = NotificationRow.CommonDocument(items);
        _listDocument.Text = document; _listDocument.ToolTip = document;
        _listDocument.Visibility = document.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var fraction = items.Count == 0 ? 0 : (double)content.Completed / items.Count;
        _progress.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (motion && Math.Abs(_progress.ScaleX - fraction) > 0.001)
        {
            var animation = NotificationMotion.Ease(_progress.ScaleX, fraction, NotificationMotion.Progress);
            animation.FillBehavior = FillBehavior.Stop;
            _progress.ScaleX = fraction;
            _progress.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        }
        else _progress.ScaleX = fraction;

        foreach (var id in _rowMap.Keys.Where(id => items.All(x => x.Id != id)).ToArray())
        { _rowMap[id].StopMotion(); _rows.Children.Remove(_rowMap[id]); _rowMap.Remove(id); }
        var index = 0;
        var visible = Shape == NotificationIslandShape.Expanded;
        foreach (var item in items)
        {
            var added = !_rowMap.TryGetValue(item.Id, out var row);
            if (row is null) { row = new NotificationRow(item); _rowMap.Add(item.Id, row); }
            if (_rows.Children.IndexOf(row) != index) { _rows.Children.Remove(row); _rows.Children.Insert(index, row); }
            row.Update(item, document.Length == 0, motion && visible);
            if (added && motion && ReferenceEquals(_current, _list)) row.Reveal(motion, PanelWidth - 24);
            index++;
        }
    }

    private void UpdateSatellite(NotificationIslandContent content, NotificationIslandState state, bool motion)
    {
        var show = state.Satellite;
        var wasShown = _satellite.Visibility == Visibility.Visible;
        if (show)
        {
            var total = content.Activities.Count;
            _satelliteRing.Data = Arc(total == 0 ? 0 : (double)content.Completed / total);
            _satelliteWarning.Visibility = state.Warning ? Visibility.Visible : Visibility.Collapsed;
            var name = content.Summary + "\n按一下查看工作";
            _satellite.ToolTip = name;
            AutomationProperties.SetName(_satellite, name);
        }

        if (show == wasShown) return;
        _satellite.BeginAnimation(OpacityProperty, null);
        _satelliteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _satelliteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _satellite.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || !motion) { _satellite.Opacity = 1; return; }
        _satellite.BeginAnimation(OpacityProperty, NotificationMotion.Ease(0, 1, NotificationMotion.Enter));
        var grow = NotificationMotion.Ease(0.6, 1, NotificationMotion.Enter);
        grow.FillBehavior = FillBehavior.Stop;
        _satelliteScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _satelliteScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void UpdateSpin(NotificationIslandContent content, bool motion)
    {
        var running = content.Running > 0 && motion;
        SetSpin(_capsuleSpin, ref _capsuleSpinning, running && Shape == NotificationIslandShape.Compact);
        SetSpin(_listSpin, ref _listSpinning, running && Shape == NotificationIslandShape.Expanded);
    }

    private static void SetSpin(RotateTransform rotation, ref bool spinning, bool spin)
    {
        if (spinning == spin) return;
        spinning = spin;
        rotation.BeginAnimation(RotateTransform.AngleProperty, spin ? NotificationMotion.Spinner() : null);
    }

    private void UpdateShake(NotificationIslandState state, bool motion)
    {
        if (state.ShakeCount == _lastShake) return;
        _lastShake = state.ShakeCount;
        if (!motion) return;
        if (state.Satellite) NotificationMotion.PlayShake(_satelliteShake);
        else if (ReferenceEquals(_current, _capsule)) NotificationMotion.PlayShake(_capsuleShake);
    }

    /// <summary>提醒由自己的檢視以 Assertive 播報；活動的摘要換了才以 Polite 播報一次。</summary>
    private void UpdateAnnouncement(NotificationIslandContent content, NotificationIslandShape previous)
    {
        var name = Shape switch
        {
            NotificationIslandShape.Compact or NotificationIslandShape.Done => content.Summary,
            NotificationIslandShape.Expanded => _listSummary.Text,
            _ => ActivePrompt.Item?.Title ?? "",
        };
        AutomationProperties.SetName(this, name);
        if (Shape is NotificationIslandShape.Compact or NotificationIslandShape.Done &&
            (name != _announced || previous == NotificationIslandShape.Hidden))
            UIElementAutomationPeer.FromElement(_capsule)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _announced = name;
    }

    private static double CapsuleWidth(string text)
    {
        var probe = new TextBlock { Text = text, FontFamily = SqlAssistChrome.InterfaceFont, FontSize = 12 };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // 左 10、圖示 16、間距 8、右 14。
        return Math.Ceiling(Math.Max(CapsuleMinWidth, Math.Min(CapsuleMaxWidth, 10 + 16 + 8 + probe.DesiredSize.Width + 14)));
    }

    private static double Measure(FrameworkElement content)
    {
        // 收起來的元素量出來是 0；換上的內容本來就要顯示，先打開再量。
        content.Visibility = Visibility.Visible;
        content.Width = double.NaN; content.Height = double.NaN;
        content.Measure(new Size(PanelWidth, double.PositiveInfinity));
        return Math.Ceiling(content.DesiredSize.Height);
    }

    private void SetWidth(double value)
    {
        value = Math.Max(0, value);
        _surface.Width = value; _viewport.Width = value;
        foreach (var layer in _layers) layer.Width = value;
    }

    private void SetHeight(double value)
    {
        value = Math.Max(0, value);
        _surface.Height = value; _viewport.Height = value;
        foreach (var layer in _layers) layer.Height = value;
    }

    private void SetRadius(double value)
    {
        var radius = new CornerRadius(Math.Max(0, value));
        _surface.CornerRadius = radius;
        _sheen.CornerRadius = new CornerRadius(Math.Max(0, value - 1));
        foreach (var layer in _layers) layer.CornerRadius = radius;
    }

    private void UpdateClip()
    {
        var width = Math.Max(0, _surface.Width); var height = Math.Max(0, _surface.Height);
        var radius = Math.Min(_surface.CornerRadius.TopLeft, Math.Min(width, height) / 2);
        _clip.Rect = new Rect(0, 0, width, height);
        _clip.RadiusX = radius; _clip.RadiusY = radius;
    }

    private Grid CreateCapsule(out Path icon, out TextBlock text)
    {
        var capsule = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(1, 1),
            RenderTransform = Group(new ScaleTransform(1, 1), new TranslateTransform()),
        };
        capsule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10 + 16 + 8) });
        capsule.ColumnDefinitions.Add(new ColumnDefinition());
        icon = StatusIcon(Group(_capsuleSpin, _capsuleScale, _capsuleShake));
        icon.Margin = new Thickness(12, 0, 0, 0);
        capsule.Children.Add(icon);
        text = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
        text.Margin = new Thickness(0, 0, 14, 0); text.FontSize = 12; text.FontWeight = FontWeights.Normal;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
        SetColumn(text, 1);
        capsule.Children.Add(text);
        AutomationProperties.SetLiveSetting(capsule, AutomationLiveSetting.Polite);
        return capsule;
    }

    private Grid CreateList(out Path icon, out TextBlock summary, out TextBlock document, out ScrollViewer details)
    {
        var list = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed, Margin = new Thickness(12, 6, 10, 8),
            RenderTransformOrigin = new Point(1, 1),
            RenderTransform = Group(new ScaleTransform(1, 1), new TranslateTransform()),
        };
        for (var row = 0; row < 4; row++) list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Height = 24 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon = StatusIcon(_listSpin);
        header.Children.Add(icon);
        summary = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
        summary.Margin = new Thickness(0); summary.FontSize = 12; summary.VerticalAlignment = VerticalAlignment.Center;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        SetColumn(summary, 1);
        header.Children.Add(summary);
        SetColumn(DismissButton, 2);
        header.Children.Add(DismissButton);
        list.Children.Add(header);

        // 抬頭下方只回答文件；資料庫留在各列上。
        document = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        document.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        document.FontSize = 11; document.Margin = new Thickness(18, 0, 0, 4);
        document.TextWrapping = TextWrapping.NoWrap; document.TextTrimming = TextTrimming.CharacterEllipsis;
        document.Visibility = Visibility.Collapsed;
        SetRow(document, 1);
        list.Children.Add(document);

        var track = new Grid { Height = 2, ClipToBounds = true };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(1) }.WithTheme(Border.BackgroundProperty, ThemeBrush.SegmentTrack));
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(1), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _progress,
        }.WithThemeKey(Border.BackgroundProperty, ThemeResourceSet.NotificationSpinnerKey));
        SetRow(track, 2);
        list.Children.Add(track);

        details = new ScrollViewer { Margin = new Thickness(0, 8, 0, 0), MaxHeight = DetailMaxHeight, Content = _rows, Focusable = false };
        SqlAssistChrome.ApplyOverlayScroll(details);
        SetRow(details, 3);
        list.Children.Add(details);
        AutomationProperties.SetLiveSetting(list, AutomationLiveSetting.Polite);
        return list;
    }

    private static Path StatusIcon(Transform transform) => new()
    {
        Width = 12, Height = 12, Stretch = Stretch.None, StrokeThickness = 1.4,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = transform,
    };

    private static void ApplyStatusIcon(Path icon, int state)
    {
        icon.Data = SqlAssistChrome.NotificationGeometry(state switch { 0 => Running, 1 => Alert, 2 => Check, _ => Cross });
        icon.WithTheme(Path.StrokeProperty, state switch
        {
            1 => ThemeBrush.NotificationFailure,
            2 => ThemeBrush.NotificationSuccess,
            _ => ThemeBrush.DimForeground,
        });
        if (state == 0) icon.SetResourceReference(Path.StrokeProperty, ThemeResourceSet.NotificationSpinnerKey);
    }

    /// <summary>衛星的進度環：從 12 點鐘方向順時針畫到完成比例。</summary>
    private static Geometry Arc(double fraction)
    {
        const double center = SatelliteSize / 2, radius = 13;
        if (fraction <= 0) return Geometry.Empty;
        if (fraction >= 1) return new EllipseGeometry(new Point(center, center), radius, radius);
        var angle = fraction * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center, center - radius), isFilled: false, isClosed: false);
            context.ArcTo(new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static TransformGroup Group(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var transform in transforms) group.Children.Add(transform);
        return group;
    }
}

internal static class NotificationIslandTheme
{
    /// <summary>以資源鍵繫結；<c>WithTheme</c> 只收 <see cref="ThemeBrush"/>，通知專屬的漸層與玻璃是字串鍵。</summary>
    public static T WithThemeKey<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
