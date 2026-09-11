using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知的視覺表面：抬頭、進度、明細列與進出場動畫。
/// </summary>
/// <remarks>
/// 只認得 <see cref="NotificationCardItem"/>。措辭、可見度、合併與期限都在呈現端決定完
/// 才交過來，所以這裡沒有任何通知來源的型別；要接第二個來源時，產生同一個記錄就夠了。
/// </remarks>
internal partial class NotificationCard : Border
{
    private readonly Dictionary<long, NotificationRow> _rows = new();
    internal static readonly DependencyProperty HighContrastProperty = DependencyProperty.Register(
        nameof(HighContrast), typeof(bool), typeof(NotificationCard), new PropertyMetadata(false));
    public bool HighContrast { get => (bool)GetValue(HighContrastProperty); set => SetValue(HighContrastProperty, value); }
    private bool _expanded;
    private bool? _glass;
    private bool _spinning;
    private int _summaryState = -1;
    private readonly HashSet<long> _summaryResults = new();
    private double _progressTarget = -1;
    private readonly RotateTransform _chevronRotation = new();
    private int _detailTransition;
    internal Button SummaryButton { get; }
    internal Button CloseButton { get; }
    internal Button ToggleButton { get; }

    public NotificationCard()
    {
        InitializeComponent();
        SummaryButton = SqlAssistChrome.CreateButton("通知", SqlAssistChrome.DefaultMetrics);
        SummaryButton.MinWidth = 0; SummaryButton.Padding = new Thickness(0); SummaryButton.Margin = new Thickness(0);
        SqlAssistChrome.ApplyNotificationCursor(SummaryButton);
        SummaryButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        SummaryButton.HorizontalAlignment = HorizontalAlignment.Left;
        var title = SqlAssistChrome.CreateLabel("通知", SqlAssistChrome.DefaultMetrics);
        ContextLabel.FontFamily = title.FontFamily;
        title.Margin = new Thickness(0); title.TextTrimming = TextTrimming.CharacterEllipsis;
        SummaryButton.Content = title; SummaryHost.Content = SummaryButton;
        CloseButton = SqlAssistChrome.CreateNotificationButton("關閉通知（不取消工作）", "M1,1 L11,11 M11,1 L1,11");
        ToggleButton = SqlAssistChrome.CreateNotificationButton("展開或收合明細", "M1,3 L5,7 L9,3");
        // Chevron 以固定方形畫布的中心旋轉，不依扁平路徑拉伸，避免上下跳位。
        ((Path)ToggleButton.Content).Stretch = Stretch.None;
        ((Path)ToggleButton.Content).RenderTransformOrigin = new Point(0.5, 0.5);
        ((Path)ToggleButton.Content).RenderTransform = _chevronRotation;
        CloseHost.Content = CloseButton; ToggleHost.Content = ToggleButton;
        SetOptions(glass: true, highContrast: SystemParameters.HighContrast);
    }

    internal void SetOptions(bool glass, bool highContrast)
    {
        HighContrast = highContrast;
        glass &= !highContrast;
        if (_glass == glass) return;
        _glass = glass;
        Sheen.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        if (glass) SetResourceReference(BorderBrushProperty, ThemeResourceSet.NotificationRimKey);
        else this.WithTheme(BorderBrushProperty, ThemeBrush.Border);
        Body.SetResourceReference(BackgroundProperty, glass ? (object)ThemeResourceSet.NotificationGlassKey : ThemeBrush.ListBackground);
        // 單層柔影保留浮起感，避免小面板四周形成厚重光暈。
        Effect = glass ? new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.14 } : null;
        Body.Effect = null;
        UpdateShadowCache();
    }

    /// <summary>
    /// 柔影只在內容變了才重算。
    /// </summary>
    /// <remarks>
    /// 卡片疊在編輯器的 adornment 層上，捲動時整張表面都要重新合成。沒有快取的話，
    /// 每一個捲動影格都會把半徑 16 的模糊重跑一次；改成點陣快取之後捲動只是搬一張圖。
    /// 依 DPI 給 <see cref="BitmapCache.RenderAtScale"/>，150%／200% 才不會糊掉；
    /// 高對比關掉玻璃時沒有柔影，也就不需要快取，文字回到原生算繪。
    /// </remarks>
    private void UpdateShadowCache() =>
        CacheMode = Effect is null ? null : new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX, SnapsToDevicePixels = true };

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateShadowCache();
    }

    internal void Update(IReadOnlyList<NotificationCardItem> items, bool expanded, bool motion)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        var running = items.Count(x => x.Status == NotificationVisualStatus.Running);
        var completed = items.Count(x => x.Status == NotificationVisualStatus.Completed);
        var failed = items.Count(x => x.Status == NotificationVisualStatus.Failed);
        var summaryState = running > 0 ? 0 : failed > 0 ? 1 : completed > 0 ? 2 : 3;
        var summaryResults = items.Where(x => x.Status == (failed > 0 ? NotificationVisualStatus.Failed : NotificationVisualStatus.Completed))
            .Select(x => x.Id).ToArray();
        // 短工作可能在兩次 UI 更新之間完成；整體形狀相同也要回饋新的結果。
        if (_summaryState != summaryState || (running == 0 && summaryResults.Any(id => !_summaryResults.Contains(id))))
        {
            _summaryState = summaryState;
            StatusScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            StatusScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            StatusShake.BeginAnimation(TranslateTransform.XProperty, null);
            StatusIcon.Data = SqlAssistChrome.NotificationGeometry(running > 0 ? "M8,1 A7,7 0 1 1 1,8" :
                failed > 0 ? "M8,1 L15,14 L1,14 Z M8,5 L8,9 M8,11 L8,12" :
                completed > 0 ? "M3,8 L6.5,11.5 L13,4.5" : "M3,3 L13,13 M13,3 L3,13");
            StatusIcon.WithTheme(Shape.StrokeProperty, running > 0 ? ThemeBrush.AccentBorder :
                failed > 0 ? ThemeBrush.NotificationFailure : completed > 0 ? ThemeBrush.NotificationSuccess : ThemeBrush.DimForeground);
            if (running > 0) StatusIcon.SetResourceReference(Shape.StrokeProperty, ThemeResourceSet.NotificationSpinnerKey);
            if (motion && running == 0)
                SqlAssistChrome.AnimateNotificationResult(StatusScale, StatusShake,
                    failed > 0 ? NotificationVisualStatus.Failed : completed > 0 ? NotificationVisualStatus.Completed : NotificationVisualStatus.Canceled);
        }
        _summaryResults.Clear();
        foreach (var id in summaryResults) _summaryResults.Add(id);
        // 整張卡片只有這一個循環動畫；各列的執行中改用靜態光環。
        if (_spinning != (running > 0 && motion))
        {
            _spinning = running > 0 && motion;
            StatusRotation.BeginAnimation(RotateTransform.AngleProperty, _spinning ?
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        }
        SqlAssistChrome.SetNotificationSummary(SummaryButton, $"已完成 ({completed}/{items.Count})");
        // 維持成功計數語意：失敗與取消不冒充成功，細節放在提示與輔助技術名稱。
        var progress = items.Count == 0 ? 0 : (double)completed / items.Count;
        if (_progressTarget != progress || !motion)
        {
            var current = Progress.Value;
            _progressTarget = progress;
            Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            Progress.Value = progress;
            // 增減都從目前畫面值接續，100 ms 刷新不重啟相同目標的動畫。
            if (motion && current != progress)
            {
                var animation = SqlAssistChrome.NotificationAnimation(current, progress, 320);
                animation.FillBehavior = FillBehavior.Stop;
                Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
            }
        }
        var description = $"成功 {completed}，共 {items.Count} 項；執行中 {running}，失敗 {failed}，取消 {items.Count - running - completed - failed}";
        Progress.ToolTip = description;
        AutomationProperties.SetName(Progress, description);
        AutomationProperties.SetName(this, description);
        // 全部指向同一份文件時只在抬頭下方顯示一次；指向不同文件才保留各列的歸屬。
        var commonDocument = CommonDocument(items);
        ContextLabel.Text = commonDocument; ContextLabel.ToolTip = commonDocument;
        ContextLabel.Visibility = commonDocument.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var id in _rows.Keys.Where(id => items.All(x => x.Id != id)).ToArray())
        { _rows[id].StopMotion(); DetailsPanel.Children.Remove(_rows[id]); _rows.Remove(id); }
        var index = 0;
        // 狀態變更不搬動列，避免圖示動畫同時被清單重新排序打斷視線。
        foreach (var item in items)
        {
            var added = !_rows.TryGetValue(item.Id, out var row);
            if (row is null) { row = new NotificationRow(item); _rows.Add(item.Id, row); }
            if (DetailsPanel.Children.IndexOf(row) != index)
            { DetailsPanel.Children.Remove(row); DetailsPanel.Children.Insert(index, row); }
            row.Update(item, commonDocument.Length == 0, motion && expanded);
            // 首次展開由整個明細區動畫；同時把各列高度歸零會量到零高度而在結尾跳動。
            if (added && expanded && _expanded)
                row.Reveal(motion, DetailsPanel.ActualWidth > 0 ? DetailsPanel.ActualWidth : Math.Max(0, Math.Min(Width, MaxWidth) - 18));
            index++;
        }
        if (_expanded != expanded)
        {
            var currentHeight = DetailScroll.ActualHeight;
            _expanded = expanded;
            var transition = ++_detailTransition;
            DetailScroll.BeginAnimation(HeightProperty, null);
            var angle = _chevronRotation.Angle;
            _chevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _chevronRotation.Angle = expanded ? 180 : 0;
            if (motion)
            {
                _chevronRotation.BeginAnimation(RotateTransform.AngleProperty,
                    SqlAssistChrome.NotificationAnimation(angle, expanded ? 180 : 0, 200));
                DetailScroll.Visibility = Visibility.Visible;
                DetailScroll.Measure(new Size(Math.Max(0, Math.Min(Width, MaxWidth) - 18), double.PositiveInfinity));
                var reveal = SqlAssistChrome.NotificationAnimation(currentHeight, expanded ? DetailScroll.DesiredSize.Height : 0, 260);
                // 反向點擊使舊回呼失效；只有最新轉場可以收起明細。
                reveal.Completed += (_, _) =>
                {
                    if (transition != _detailTransition) return;
                    DetailScroll.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
                    DetailScroll.BeginAnimation(HeightProperty, null);
                };
                DetailScroll.BeginAnimation(HeightProperty, reveal);
                DetailScroll.BeginAnimation(OpacityProperty, SqlAssistChrome.NotificationAnimation(
                    expanded && currentHeight == 0 ? 0 : DetailScroll.Opacity, expanded ? 1 : 0, 220));
            }
        }
        ToggleButton.ToolTip = expanded ? "收合明細" : "展開明細";
        AutomationProperties.SetName(ToggleButton, (string)ToggleButton.ToolTip);
        if (!motion || !DetailScroll.HasAnimatedProperties)
            DetailScroll.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (!motion)
        {
            ++_detailTransition;
            StatusScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            StatusScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            StatusShake.BeginAnimation(TranslateTransform.XProperty, null);
            _chevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _chevronRotation.Angle = expanded ? 180 : 0;
            DetailScroll.BeginAnimation(OpacityProperty, null);
            DetailScroll.BeginAnimation(HeightProperty, null);
        }
    }

    /// <summary>
    /// 這一批共同的文件；指向兩份以上文件時回空字串。
    /// </summary>
    /// <remarks>
    /// 沒有文件的列（套件初始化、重建主題筆刷、中繼資料查詢）不參與比較。它們算進來的話，
    /// 一列不屬於任何文件的背景工作就會把抬頭那一行整個收掉，而畫面上的其他列明明都來自
    /// 同一份查詢——那正是檔名時有時無的成因。
    /// </remarks>
    private static string CommonDocument(IReadOnlyList<NotificationCardItem> items)
    {
        var common = "";
        for (var index = 0; index < items.Count; index++)
        {
            var document = items[index].Document;
            if (document.Length == 0) continue;
            if (common.Length == 0) common = document;
            else if (!string.Equals(common, document, StringComparison.Ordinal)) return "";
        }

        return common;
    }

    internal void Transition(bool show, bool fresh, bool motion, NotificationPosition position)
    {
        if (fresh) { Opacity = 0; SurfaceSlide.Y = position == NotificationPosition.TopRight ? -20 : 20; }
        var duration = motion ? (show ? 300 : 220) : 0;
        // 從目前有效值反轉淡出，新工作不先跳回起點或閃現。
        BeginAnimation(OpacityProperty, SqlAssistChrome.NotificationAnimation(Opacity, show ? 1 : 0, duration));
        SurfaceSlide.BeginAnimation(TranslateTransform.YProperty, SqlAssistChrome.NotificationAnimation(SurfaceSlide.Y, 0, duration));
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, SqlAssistChrome.NotificationAnimation(SurfaceScale.ScaleX, show ? 1 : 0.96, duration));
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, SqlAssistChrome.NotificationAnimation(SurfaceScale.ScaleY, show ? 1 : 0.96, duration));
    }

    internal void StopMotion()
    {
        foreach (var row in _rows.Values) row.SuspendMotion();
        _spinning = false;
        StatusRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        StatusScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StatusScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        StatusShake.BeginAnimation(TranslateTransform.XProperty, null);
        Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        _chevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        ResetTransition();
    }

    internal void ResetTransition()
    {
        ++_detailTransition;
        DetailScroll.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null); Opacity = 1;
        SurfaceSlide.BeginAnimation(TranslateTransform.YProperty, null); SurfaceSlide.Y = 0;
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); SurfaceScale.ScaleX = 1;
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); SurfaceScale.ScaleY = 1;
        DetailScroll.BeginAnimation(OpacityProperty, null);
        DetailScroll.BeginAnimation(HeightProperty, null);
    }

    internal void Constrain(Size viewport)
    {
        MaxWidth = Math.Max(0, viewport.Width - 8);
        MaxHeight = Math.Max(0, viewport.Height - 16);
        var contentWidth = Math.Max(0, Math.Min(Width, MaxWidth) - 18);
        Header.Measure(new Size(contentWidth, double.PositiveInfinity));
        ContextLabel.Measure(new Size(contentWidth, double.PositiveInfinity));
        // 只有明細捲動，短編輯區仍保留標題與關閉鈕；極端高度則裁切於內容區。
        var chromeHeight = Header.DesiredSize.Height + ContextLabel.DesiredSize.Height + 24;
        DetailScroll.MaxHeight = Math.Max(0, Math.Min(140, MaxHeight - chromeHeight));
        ClipToBounds = MaxHeight < chromeHeight;
    }
}
