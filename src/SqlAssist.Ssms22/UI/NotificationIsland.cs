using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22.Notifications;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知島：單一表面，以彈簧同時變形寬、高與圓角，承載膠囊、活動清單與提醒。
/// </summary>
/// <remarks>
/// 形態由 <see cref="NotificationIslandState"/> 決定，這裡只負責畫出那個形態。內容一律依目標
/// 尺寸排版、靠右上對齊，再用動畫中的圓角裁切露出來：變形過程中量到的是固定的目標寬度，
/// 文字不跟著每一幀重新換行。表面錨在右下角往左上長，內容則貼著表面的上緣走——
/// 高度變化時標題跟著上緣移動，底部由裁切露出或收掉，附條收起時標題不會跳。
///
/// 只認得 <see cref="NotificationIslandContent"/>；按鈕、叉號與附條都只是把事件交出去，
/// 處理在呼叫端。循環動畫同一時間只有一個：膠囊、清單抬頭或提醒卡附條上的進度圈，各列是靜態的。
/// 對齊基準見 <see cref="NotificationLayout"/>。
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

    public const double DetailMaxHeight = 240;

    /// <summary>疊起來的提醒，後面每一層往上露出多少。</summary>
    public const double StackPeek = 5;

    /// <summary>
    /// 島嶼最大的外框（含疊層，不含柔影）。
    /// </summary>
    /// <remarks>
    /// 浮層視窗依它固定大小：變形途中改視窗大小，每一影格都是一次 SetWindowPos 加上整個分層視窗重新合成。
    /// 高度的上限是暫看活動時的清單：上距 8、抬頭 24、文件列約 15、進度條上距 8 與高 3、明細上距 6 與
    /// <see cref="DetailMaxHeight"/>、底部附條 30（貼底時吃掉 5 DIP 下距）、下距 6，共約 341，取整到 352
    /// 留給字型行高的差異；三行訊息的提醒加上附條與兩層疊層不到 180。
    /// </remarks>
    public static readonly Size MaxExtent = new(PanelWidth, 352);

    /// <summary>膠囊：左距、圖示與文字之間、右距。</summary>
    private const double CapsulePadding = 12;
    private const double CapsuleGap = 8;
    private const double CapsuleEnd = 14;

    /// <summary>清單的下距；最後一列自己還有 6 DIP 的內距。</summary>
    private const double ListBottom = 6;

    /// <summary>進度條的高度；全部結束後收成 1 DIP 的分隔線。</summary>
    private const double TrackHeight = 3;

    private const string Cross = "M1,1 L11,11 M11,1 L1,11";

    private readonly Grid _island = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _surface;
    private readonly Border _sheen;
    private readonly Border[] _layers = new Border[2];
    private readonly Grid _viewport;
    private readonly RectangleGeometry _clip = new();
    private readonly SpringMotion _width;
    private readonly SpringMotion _height;
    private readonly SpringMotion _radius;

    private readonly Grid _capsule;
    private readonly NotificationStatusIcon _capsuleIcon = new() { Margin = new Thickness(CapsulePadding, 0, 0, 0) };
    private readonly NotificationTicker _capsuleText;

    private readonly Grid _list;
    private readonly NotificationStatusIcon _listIcon = new() { Margin = NotificationLayout.StatusIconInset };
    private readonly NotificationTicker _listSummary;
    private readonly Grid _failureChip;
    private readonly TextBlock _failureText;
    private readonly TextBlock _listDocument;
    private readonly ScaleTransform _track = new(1, 1);
    private readonly Border _trackBase;
    private readonly Border _progressFill;
    private readonly ScaleTransform _progress = new(0, 1);
    private readonly Border _divider;
    private readonly ScrollViewer _details;
    private readonly StackPanel _rows = new();
    private readonly Dictionary<long, NotificationRow> _rowMap = new();
    private readonly Dictionary<long, NotificationRow> _exiting = new();
    private readonly NotificationActivityStrip _footer = new() { Visibility = Visibility.Collapsed };

    private readonly NotificationPromptView[] _prompts = new NotificationPromptView[2];
    private int _activePrompt;

    private FrameworkElement? _current;
    private bool _shown;
    private bool _hiding;
    private bool _motion;
    private bool _stopping;
    private bool? _settled;
    private int _lastShake;
    private string _announced = "";
    private bool? _glass;

    public NotificationIsland()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        SnapsToDevicePixels = true; UseLayoutRounding = true;
        Visibility = Visibility.Collapsed;

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
        Children.Add(_island);

        DismissButton = SqlAssistChrome.CreateNotificationButton(NotificationCatalog.DismissActivities, Cross);
        DismissButton.VerticalAlignment = VerticalAlignment.Center;
        DismissButton.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        _capsuleText = new NotificationTicker(() =>
        {
            var text = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
            text.FontSize = 12; text.FontWeight = FontWeights.Normal;
            return text;
        });
        _listSummary = new NotificationTicker(() =>
        {
            var text = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
            text.FontSize = 12;
            return text;
        });
        _failureChip = CreateFailureChip(out _failureText);
        _listDocument = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _trackBase = new Border { CornerRadius = new CornerRadius(TrackHeight / 2) }.WithTheme(Border.BackgroundProperty, ThemeBrush.SegmentTrack);
        _progressFill = new Border
        {
            CornerRadius = new CornerRadius(TrackHeight / 2), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _progress,
        }.WithThemeKey(Border.BackgroundProperty, ThemeResourceSet.NotificationSpinnerKey);
        _divider = new Border { Opacity = 0 }.WithTheme(Border.BackgroundProperty, ThemeBrush.Hairline);
        _details = new ScrollViewer
        {
            Margin = new Thickness(-NotificationLayout.Bleed, 6, -NotificationLayout.Bleed, 0),
            MaxHeight = DetailMaxHeight, Content = _rows, Focusable = false,
        };
        _capsule = CreateCapsule();
        _list = CreateList();
        _viewport.Children.Add(_capsule);
        _viewport.Children.Add(_list);
        _footer.Click += (_, _) => PeekRequested?.Invoke(this, EventArgs.Empty);
        for (var index = 0; index < _prompts.Length; index++)
        {
            var view = new NotificationPromptView
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(1, 0),
                RenderTransform = Group(new ScaleTransform(1, 1), new TranslateTransform()),
            };
            view.ActionInvoked += (id, action) => PromptResolved?.Invoke(id, action);
            view.ActivityStrip.Click += (_, _) => PeekRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>按了附條：呼叫端交給狀態機切去看活動，或從活動切回提醒。</summary>
    public event EventHandler? PeekRequested;

    /// <summary>活動清單的叉號：這一批看完了，不取消工作。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>收場播完、整個收起來了；浮層在這時才隱藏視窗，否則收場動畫會被一起藏掉。</summary>
    public event EventHandler? Vanished;

    /// <summary>這一輪的形態；由狀態機給。</summary>
    public NotificationIslandShape Shape { get; private set; } = NotificationIslandShape.Hidden;

    /// <summary>島嶼變形完之後的大小（含疊層），給浮層與測試確認放得進固定外框。</summary>
    public Size TargetSize { get; private set; }

    internal FrameworkElement? CurrentContent => _current;
    internal Button DismissButton { get; }
    internal NotificationPromptView ActivePrompt => _prompts[_activePrompt];
    internal Border Surface => _surface;
    internal IReadOnlyList<Border> StackLayers => _layers;
    internal ScrollViewer Details => _details;
    internal StackPanel Rows => _rows;
    internal TextBlock DocumentLabel => _listDocument;
    internal NotificationTicker ListSummary => _listSummary;
    internal UIElement FailureChip => _failureChip;
    internal NotificationActivityStrip ListFooter => _footer;
    internal NotificationStatusIcon ListIcon => _listIcon;
    internal ScaleTransform Progress => _progress;

    /// <summary>進度條已經收成分隔線。</summary>
    internal bool ProgressSettled => _settled == true;

    internal bool IsSpinning =>
        _capsuleIcon.IsSpinning || _listIcon.IsSpinning || _prompts.Any(view => view.ActivityStrip.Icon.IsSpinning);

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
    }

    /// <summary>畫出這一輪的形態與內容。</summary>
    /// <param name="motion">動畫開著；關著時所有尺寸直接到位、不播任何回饋。</param>
    public void Update(NotificationIslandContent content, NotificationIslandState state, bool motion)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        if (state is null) throw new ArgumentNullException(nameof(state));
        _motion = motion;
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
        IReadOnlyList<NotificationRow> added = Array.Empty<NotificationRow>();
        switch (Shape)
        {
            case NotificationIslandShape.Compact:
            case NotificationIslandShape.Done:
                UpdateCapsule(content, motion);
                target = new Size(CapsuleWidth(content.Summary), CapsuleHeight);
                next = _capsule;
                break;
            case NotificationIslandShape.Expanded:
                added = UpdateList(content, motion);
                target = new Size(PanelWidth, Measure(_list));
                next = _list;
                break;
            default:
                var top = content.Prompts[0];
                if (ActivePrompt.Item?.Id != top.Id && ActivePrompt.Item is not null) _activePrompt = 1 - _activePrompt;
                var view = ActivePrompt;
                view.Update(top);
                UpdatePromptStrips(content, state, motion);
                target = new Size(PanelWidth, Measure(view));
                next = view;
                // 疊著的提醒處理掉一則：下一則從下面滑上來，而不是原地換字。
                slide = IsPrompt(previous) && previousPrompt is not null && previousPrompt.Id != top.Id &&
                    top.Count < previousPrompt.Count;
                break;
        }

        FitTo(next, target);
        var radius = Shape is NotificationIslandShape.Compact or NotificationIslandShape.Done ? CapsuleRadius : PanelRadius;
        var layers = Shape == NotificationIslandShape.PromptStack ? Math.Min(_layers.Length, content.Prompts.Count - 1) : 0;
        for (var index = 0; index < _layers.Length; index++)
            _layers[index].Visibility = index < layers ? Visibility.Visible : Visibility.Collapsed;

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
        var enteringList = ReferenceEquals(next, _list) && !ReferenceEquals(_current, _list);
        Swap(next, motion && !appearing, slide);
        // 量完高度才開始長：先開始的話量到的是高度 0 的新列，外框會少一列。
        if (ReferenceEquals(next, _list) && motion)
        {
            if (enteringList) StaggerRows();
            else foreach (var row in added) row.Enter(motion: true, RowWidth);
        }

        UpdateSpin(content, motion);
        UpdateShake(state, motion);
        UpdateAnnouncement(content, previous);
        TargetSize = new Size(target.Width, target.Height + StackPeek * layers);
    }

    /// <summary>停掉所有動畫並把尺寸放到目標；表面離開畫面或動畫關掉時用。</summary>
    public void StopMotion()
    {
        _stopping = true;
        _width.Jump(_width.Target); _height.Jump(_height.Target); _radius.Jump(_radius.Target);
        _capsuleIcon.StopMotion(); _capsuleText.StopMotion();
        _listIcon.StopMotion(); _listSummary.StopMotion();
        _footer.StopMotion();
        foreach (var view in _prompts) view.ActivityStrip.StopMotion();
        foreach (var row in _rowMap.Values.Concat(_exiting.Values).ToArray()) row.SuspendMotion();
        _progress.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _track.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        foreach (var part in new UIElement[] { _trackBase, _progressFill, _divider }) part.BeginAnimation(OpacityProperty, null);
        _island.BeginAnimation(OpacityProperty, null); _island.Opacity = 1;
        foreach (FrameworkElement child in _viewport.Children)
        {
            child.BeginAnimation(OpacityProperty, null);
            child.Opacity = 1;
            if (!ReferenceEquals(child, _current)) child.Visibility = Visibility.Collapsed;
        }

        _stopping = false;
        if (!_shown) Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 立刻收起、不播收場，也不發 <see cref="Vanished"/>；下一次 <see cref="Update"/> 從圓點重新長出來。
    /// </summary>
    /// <remarks>換擁有者時用：浮層先隱藏再換位置，舊位置上的收場沒有人看得到。</remarks>
    public void Reset()
    {
        _shown = false; _hiding = false;
        TargetSize = new Size(DotSize, DotSize);
        StopMotion();
        Visibility = Visibility.Collapsed;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SqlAssistChrome.UpdateNotificationShadowCache(_surface);
    }

    /// <summary>清單列的寬度：文字欄再往左右各延伸停駐底色的量。</summary>
    private static double RowWidth => PanelWidth - NotificationLayout.Left - NotificationLayout.Right + NotificationLayout.Bleed * 2;

    private void Hide(bool motion)
    {
        if (!_shown) return;
        _shown = false;
        _capsuleIcon.Spin(false); _listIcon.Spin(false);
        foreach (var view in _prompts) view.ActivityStrip.Icon.Spin(false);
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
            // 同一份內容剛被換走又換回來（例如附條來回點）：從目前的透明度接回 1。
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

    /// <summary>
    /// 內容量出來的高度變了（列離場播完、附條收起），但形態沒變：只把外框追過去，不換內容。
    /// </summary>
    private void Relayout()
    {
        if (_stopping || !_shown || _hiding || _current is null || ReferenceEquals(_current, _capsule)) return;
        var height = Measure(_current);
        FitTo(_current, new Size(PanelWidth, height));
        _height.AnimateTo(height, _motion);
        var layers = _layers.Count(layer => layer.Visibility == Visibility.Visible);
        TargetSize = new Size(TargetSize.Width, height + StackPeek * layers);
    }

    private static bool IsPrompt(NotificationIslandShape shape) => shape is NotificationIslandShape.Prompt
        or NotificationIslandShape.PromptWithActivity or NotificationIslandShape.PromptStack;

    private static (ScaleTransform?, TranslateTransform?) Transforms(UIElement element) =>
        element.RenderTransform is TransformGroup group
            ? (group.Children.OfType<ScaleTransform>().FirstOrDefault(), group.Children.OfType<TranslateTransform>().FirstOrDefault())
            : (null, null);

    private void UpdateCapsule(NotificationIslandContent content, bool motion)
    {
        var visible = motion && ReferenceEquals(_current, _capsule);
        var status = content.Status;
        // 失敗的短震由 ShakeCount 決定，這裡只補「執行中 → 成功」那一刻的描勾。
        _capsuleIcon.SetStatus(status, visible && _capsuleIcon.Status == NotificationVisualStatus.Running &&
            status == NotificationVisualStatus.Completed);
        _capsuleText.SetText(content.Summary, visible);
    }

    private IReadOnlyList<NotificationRow> UpdateList(NotificationIslandContent content, bool motion)
    {
        var items = content.Activities;
        // 清單已經在畫面上才播換字、回饋與離場；剛要換上來的那一輪交給依序進場。
        var visible = motion && ReferenceEquals(_current, _list);
        _listIcon.SetStatus(content.Status, feedback: false);
        _listSummary.SetText(NotificationCatalog.ProgressSummary(content.Completed, items.Count), visible);
        var failure = NotificationCatalog.FailureSummary(content.Failed);
        _failureText.Text = failure; _failureChip.ToolTip = failure;
        AutomationProperties.SetName(_failureChip, failure);
        _failureChip.Visibility = content.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        var document = NotificationRow.CommonDocument(items);
        _listDocument.Text = document; _listDocument.ToolTip = document;
        _listDocument.Visibility = document.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress(items.Count == 0 ? 0 : (double)content.Completed / items.Count, motion);
        Settle(content.Running == 0 && items.Count > 0, visible);

        // 暫看活動時提醒還在等：清單底部接一條回去的路。
        if (content.Prompts.Count > 0)
            _footer.Update(NotificationCatalog.PendingPrompts(content.Prompts.Count), NotificationVisualStatus.Pending,
                NotificationCatalog.BackToPrompts, feedback: false, visible);
        else _footer.Collapse();

        foreach (var id in _rowMap.Keys.Where(id => items.All(x => x.Id != id)).ToArray())
        {
            var row = _rowMap[id];
            _rowMap.Remove(id);
            if (!visible) { row.StopMotion(); _rows.Children.Remove(row); continue; }
            _exiting[id] = row;
            row.Exit(motion: true, () =>
            {
                if (_exiting.TryGetValue(id, out var leaving) && ReferenceEquals(leaving, row)) _exiting.Remove(id);
                _rows.Children.Remove(row);
                Relayout();
            });
        }

        var added = new List<NotificationRow>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!_rowMap.TryGetValue(item.Id, out var row))
            {
                if (_exiting.TryGetValue(item.Id, out row)) { _exiting.Remove(item.Id); row.CancelExit(); }
                else { row = new NotificationRow(item); added.Add(row); }
                _rowMap.Add(item.Id, row);
            }

            Place(row, index);
            row.Update(item, document.Length == 0, visible);
        }

        return added;
    }

    /// <summary>把列放到第 <paramref name="index"/> 個現役位置；離場中的列還在畫面上，但不算位置。</summary>
    private void Place(NotificationRow row, int index)
    {
        var position = 0;
        for (var live = 0; position < _rows.Children.Count; position++)
        {
            var child = (NotificationRow)_rows.Children[position];
            if (child.IsExiting) continue;
            if (live == index) break;
            live++;
        }

        var current = _rows.Children.IndexOf(row);
        if (current == position) return;
        if (current >= 0)
        {
            _rows.Children.RemoveAt(current);
            if (current < position) position--;
        }

        _rows.Children.Insert(Math.Min(position, _rows.Children.Count), row);
    }

    private void StaggerRows()
    {
        var index = 0;
        foreach (NotificationRow row in _rows.Children)
            if (!row.IsExiting) row.Stagger(index++, motion: true);
    }

    private void UpdateProgress(double fraction, bool motion)
    {
        _progress.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (motion && Math.Abs(_progress.ScaleX - fraction) > 0.001)
        {
            var animation = NotificationMotion.Ease(_progress.ScaleX, fraction, NotificationMotion.Progress);
            animation.FillBehavior = FillBehavior.Stop;
            _progress.ScaleX = fraction;
            _progress.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        }
        else _progress.ScaleX = fraction;
    }

    /// <summary>
    /// 全部結束之後，進度條停一下再收成 1 DIP 的分隔線；有新工作進來時長回去。
    /// </summary>
    /// <remarks>
    /// 走完的進度條留在那裡沒有資訊，只是一條顏色不明的線；直接拿掉的話，抬頭與明細之間又少了界線。
    /// 只縮 <see cref="ScaleTransform.ScaleY"/> 並交叉淡入髮絲線，不改版面高度，明細不會跟著跳。
    /// </remarks>
    private void Settle(bool settled, bool motion)
    {
        if (_settled == settled) return;
        _settled = settled;
        var delay = settled ? NotificationMotion.ProgressSettleDelay : 0;
        SettleTo(_track, ScaleTransform.ScaleYProperty, settled ? 1 / TrackHeight : 1, delay, motion);
        SettleTo(_trackBase, OpacityProperty, settled ? 0 : 1, delay, motion);
        SettleTo(_progressFill, OpacityProperty, settled ? 0 : 1, delay, motion);
        SettleTo(_divider, OpacityProperty, settled ? 1 : 0, delay, motion);
    }

    /// <summary>從畫面上的目前值接到 <paramref name="to"/>；基底值先寫好，動畫停下來時就停在終點。</summary>
    private static void SettleTo(DependencyObject target, DependencyProperty property, double to, int delay, bool motion)
    {
        var from = (double)target.GetValue(property);
        target.SetValue(property, to);
        ((IAnimatable)target).BeginAnimation(property, motion
            ? NotificationMotion.Delayed(from, to, delay, NotificationMotion.ProgressSettle)
            : null);
    }

    /// <summary>
    /// 提醒卡底部的附條：只有最上面那一張、而且有活動時才顯示；沒有活動了就淡出再收起。
    /// </summary>
    private void UpdatePromptStrips(NotificationIslandContent content, NotificationIslandState state, bool motion)
    {
        foreach (var view in _prompts)
        {
            var strip = view.ActivityStrip;
            var active = ReferenceEquals(view, ActivePrompt);
            var visible = motion && ReferenceEquals(_current, view) && strip.Visibility == Visibility.Visible && !strip.IsLeaving;
            if (!active || !state.ActivityStrip)
            {
                if (active && ReferenceEquals(_current, view)) strip.Leave(motion, Relayout);
                else strip.Collapse();
                continue;
            }

            var status = content.Status;
            // 失敗的短震由 ShakeCount 決定；這裡只補「執行中 → 成功」那一刻的描勾。
            var feedback = visible && strip.Icon.Status == NotificationVisualStatus.Running &&
                status == NotificationVisualStatus.Completed;
            strip.Update(content.Summary, status, NotificationCatalog.ViewActivities, feedback, visible);
        }
    }

    private void UpdateSpin(NotificationIslandContent content, bool motion)
    {
        var running = content.Running > 0 && motion;
        _capsuleIcon.Spin(running && Shape == NotificationIslandShape.Compact);
        _listIcon.Spin(running && Shape == NotificationIslandShape.Expanded);
        foreach (var view in _prompts)
        {
            var strip = view.ActivityStrip;
            strip.Icon.Spin(running && IsPrompt(Shape) && ReferenceEquals(view, ActivePrompt) &&
                strip.Visibility == Visibility.Visible && !strip.IsLeaving);
        }
    }

    private void UpdateShake(NotificationIslandState state, bool motion)
    {
        if (state.ShakeCount == _lastShake) return;
        _lastShake = state.ShakeCount;
        if (!motion) return;
        if (ReferenceEquals(_current, _capsule)) _capsuleIcon.Shake();
        else if (ReferenceEquals(_current, ActivePrompt) && state.ActivityStrip) ActivePrompt.ActivityStrip.Icon.Shake();
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
        var chrome = CapsulePadding + NotificationStatusIcon.Size + CapsuleGap + CapsuleEnd;
        return Math.Ceiling(Math.Max(CapsuleMinWidth, Math.Min(CapsuleMaxWidth, chrome + probe.DesiredSize.Width)));
    }

    private static double Measure(FrameworkElement content)
    {
        // 收起來的元素量出來是 0；換上的內容本來就要顯示，先打開再量。
        content.Visibility = Visibility.Visible;
        content.Width = double.NaN; content.Height = double.NaN;
        content.Measure(new Size(PanelWidth, double.PositiveInfinity));
        return Math.Ceiling(content.DesiredSize.Height);
    }

    /// <summary>目標尺寸含內容自己的外距；排版寬高要扣掉，否則靠右上對齊時整份內容往左下多推出一圈。</summary>
    private static void FitTo(FrameworkElement content, Size target)
    {
        content.Width = Math.Max(0, target.Width - content.Margin.Left - content.Margin.Right);
        content.Height = Math.Max(0, target.Height - content.Margin.Top - content.Margin.Bottom);
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

    private Grid CreateCapsule()
    {
        var capsule = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(1, 0),
            RenderTransform = Group(new ScaleTransform(1, 1), new TranslateTransform()),
        };
        capsule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CapsulePadding + NotificationStatusIcon.Size + CapsuleGap) });
        capsule.ColumnDefinitions.Add(new ColumnDefinition());
        capsule.Children.Add(_capsuleIcon);
        _capsuleText.Margin = new Thickness(0, 0, CapsuleEnd, 0);
        _capsuleText.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_capsuleText, 1);
        capsule.Children.Add(_capsuleText);
        AutomationProperties.SetLiveSetting(capsule, AutomationLiveSetting.Polite);
        return capsule;
    }

    /// <summary>
    /// 展開清單：抬頭（狀態、摘要、失敗標記、叉號）、文件列、進度條、明細與暫看時的底部附條。
    /// </summary>
    /// <remarks>圖示、文字與右側收邊都照 <see cref="NotificationLayout"/>，與提醒卡的標題列同一個位置。</remarks>
    private Grid CreateList()
    {
        var list = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(NotificationLayout.Left, NotificationLayout.Top, NotificationLayout.Right, ListBottom),
            RenderTransformOrigin = new Point(1, 0),
            RenderTransform = Group(new ScaleTransform(1, 1), new TranslateTransform()),
        };
        for (var row = 0; row < 5; row++) list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Height = NotificationLayout.HeaderHeight };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NotificationLayout.IconColumn) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_listIcon);
        _listSummary.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_listSummary, 1);
        header.Children.Add(_listSummary);
        SetColumn(_failureChip, 2);
        header.Children.Add(_failureChip);
        SetColumn(DismissButton, 3);
        header.Children.Add(DismissButton);
        list.Children.Add(header);

        // 抬頭下方只回答文件；資料庫留在各列上。
        _listDocument.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
        _listDocument.FontSize = 11;
        _listDocument.Margin = new Thickness(NotificationLayout.IconColumn, 0, NotificationLayout.TextEnd - NotificationLayout.Right, 0);
        _listDocument.TextWrapping = TextWrapping.NoWrap; _listDocument.TextTrimming = TextTrimming.CharacterEllipsis;
        _listDocument.Visibility = Visibility.Collapsed;
        SetRow(_listDocument, 1);
        list.Children.Add(_listDocument);

        var track = new Grid
        {
            Height = TrackHeight, Margin = new Thickness(0, SqlAssistChrome.Spacing.Group, 0, 0), ClipToBounds = true,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _track,
        };
        track.Children.Add(_trackBase);
        track.Children.Add(_progressFill);
        track.Children.Add(_divider);
        SetRow(track, 2);
        list.Children.Add(track);

        SqlAssistChrome.ApplyOverlayScroll(_details, fadeWhenIdle: true, fadeEdges: true);
        SetRow(_details, 3);
        list.Children.Add(_details);

        _footer.Place(gap: 6, bottom: ListBottom);
        SetRow(_footer, 4);
        list.Children.Add(_footer);
        AutomationProperties.SetLiveSetting(list, AutomationLiveSetting.Polite);
        return list;
    }

    /// <summary>
    /// 抬頭右側的失敗標記：失敗色的圖示與淡底，字維持一般前景。
    /// </summary>
    /// <remarks>失敗色只保證圖形對比（3:1），拿來寫 11 DIP 的字會低於文字的 4.5:1。</remarks>
    private static Grid CreateFailureChip(out TextBlock text)
    {
        var chip = new Grid
        {
            Height = 18, Margin = new Thickness(SqlAssistChrome.Spacing.Group, 0, SqlAssistChrome.Spacing.Tight, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        chip.Children.Add(new Border { CornerRadius = new CornerRadius(9), Opacity = 0.12 }
            .WithTheme(Border.BackgroundProperty, ThemeBrush.NotificationFailure));
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 7, 0) };
        var icon = new NotificationStatusIcon();
        icon.SetStatus(NotificationVisualStatus.Failed, feedback: false);
        content.Children.Add(icon);
        text = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
        text.FontSize = 11; text.FontWeight = FontWeights.Normal;
        text.Margin = new Thickness(SqlAssistChrome.Spacing.Tight, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(text);
        chip.Children.Add(content);
        return chip;
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
