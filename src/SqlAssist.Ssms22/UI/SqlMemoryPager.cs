using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// History／Favorites 清單的頁尾：細線夾著的摘要膠囊、淡色說明，以及膠囊形的續頁按鈕。
/// </summary>
/// <remarks>
/// 狀態與文案全部來自 <see cref="SqlMemoryFooter"/>，這裡只負責呈現與動畫。
/// 續頁時進度留在按鈕原地（圖示換成轉動的弧線、文字換成「載入中…」），尺寸不變，
/// 不另開一條載入文字，也不用覆蓋整份清單的載入圖示遮住已經讀得到的列。
///
/// 動畫只有三種，全部受全域動畫設定控制並走 RenderTransform／Opacity，不改版面：
/// 狀態改變時的淡入上移、續頁中的弧線轉動，以及停駐時向下箭頭的輕推。
/// </remarks>
internal sealed class SqlMemoryPager : StackPanel
{
    private static readonly Geometry DownGeometry = Frozen(Geometry.Parse("M 3,6 L 8,11 13,6"));
    private static readonly Geometry ArcGeometry = Frozen(Geometry.Parse("M 13.5,8 A 5.5,5.5 0 1 1 8,2.5"));

    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _hint;
    private readonly Border _capsule;
    private readonly Path _glyph;
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly RotateTransform _spin = new();
    private readonly TranslateTransform _nudge = new();
    private readonly TranslateTransform _reveal = new();
    private SqlMemoryFooterKind _kind = SqlMemoryFooterKind.Hidden;

    public SqlMemoryPager()
    {
        Margin = new Thickness(0, 6, 2, 10);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        RenderTransform = _reveal;
        AutomationProperties.SetName(this, "清單頁尾");

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 12 });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 12 });
        heading.Children.Add(SqlAssistChrome.CreateMemoryPagerRule());
        _summary.FontFamily = SqlAssistChrome.InterfaceFont;
        _summary.FontSize = SqlAssistChrome.DefaultMetrics.Caption;
        _summary.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        _capsule = new Border
        {
            Child = _summary, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(8, 0, 8, 0)
        };
        _capsule.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground);
        _capsule.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        Grid.SetColumn(_capsule, 1); heading.Children.Add(_capsule);
        var right = SqlAssistChrome.CreateMemoryPagerRule();
        Grid.SetColumn(right, 2); heading.Children.Add(right);
        Children.Add(heading);

        _hint = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
        _hint.TextAlignment = TextAlignment.Center;
        _hint.Margin = new Thickness(8, 4, 8, 0);
        Children.Add(_hint);

        _glyph = new Path
        {
            Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.4, VerticalAlignment = VerticalAlignment.Center,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), Margin = new Thickness(0, 0, 6, 0), IsHitTestVisible = false
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(_glyph); content.Children.Add(_label);
        Button = SqlAssistChrome.CreateMemoryPagerButton();
        Button.Content = content;
        Button.Margin = new Thickness(0, 8, 0, 0);
        // 前景跟隨按鈕的停駐配對色；箭頭與文字一起換色。
        var foreground = new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = Button };
        _glyph.SetBinding(Shape.StrokeProperty, foreground);
        _label.SetBinding(TextBlock.ForegroundProperty, foreground);
        Button.Click += (_, _) => { if (_kind is SqlMemoryFooterKind.More or SqlMemoryFooterKind.ContinueSearch) LoadMoreRequested?.Invoke(this, EventArgs.Empty); };
        Button.MouseEnter += (_, _) => Nudge(2);
        Button.MouseLeave += (_, _) => Nudge(0);
        Children.Add(Button);

        IsVisibleChanged += (_, _) => UpdateSpin();
        Unloaded += (_, _) => _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        Update(new SqlMemoryBrowserModel().Footer(0));
    }

    public event EventHandler? LoadMoreRequested;

    public Button Button { get; }

    public SqlMemoryFooterKind Kind => _kind;

    public string Summary => _summary.Text;

    public void Update(SqlMemoryFooter footer)
    {
        if (footer == null) throw new ArgumentNullException(nameof(footer));
        var previous = _kind;
        _kind = footer.Kind;
        Visibility = footer.Kind == SqlMemoryFooterKind.Hidden ? Visibility.Collapsed : Visibility.Visible;
        _summary.Text = footer.Summary;
        _capsule.ToolTip = footer.Summary;
        _hint.Text = footer.Hint ?? "";
        _hint.Visibility = footer.Hint == null ? Visibility.Collapsed : Visibility.Visible;

        var loading = footer.Kind == SqlMemoryFooterKind.Loading;
        Button.Visibility = footer.ActionLabel == null ? Visibility.Collapsed : Visibility.Visible;
        Button.IsEnabled = footer.CanAct;
        _label.Text = footer.ActionLabel ?? "";
        AutomationProperties.SetName(Button, footer.ActionLabel ?? "");
        Button.ToolTip = footer.Kind == SqlMemoryFooterKind.ContinueSearch ? footer.Hint : null;
        _glyph.Data = loading ? ArcGeometry : DownGeometry;
        _glyph.RenderTransform = loading ? _spin : _nudge;
        if (loading) _nudge.BeginAnimation(TranslateTransform.YProperty, null);
        UpdateSpin();

        // 同一種狀態只更新文字（例如刪列後的筆數），不重播；可見狀態之間切換才淡入。
        if (footer.Kind != previous && footer.Kind != SqlMemoryFooterKind.Hidden && footer.Kind != SqlMemoryFooterKind.Loading &&
            previous != SqlMemoryFooterKind.Loading)
            Reveal();
    }

    private void Reveal()
    {
        if (!SqlAssistChrome.MotionEnabled) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        _reveal.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void Nudge(double offset)
    {
        if (_kind == SqlMemoryFooterKind.Loading || !Button.IsEnabled) offset = 0;
        _nudge.BeginAnimation(TranslateTransform.YProperty, SqlAssistChrome.MotionEnabled
            ? new DoubleAnimation(offset, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
            : null);
        if (!SqlAssistChrome.MotionEnabled) _nudge.Y = 0;
    }

    private void UpdateSpin()
    {
        // 與表面載入圖示同一規則：動畫關閉時保留靜態弧線；看不見就停轉，不讓背景工具窗持續算繪。
        _spin.BeginAnimation(RotateTransform.AngleProperty,
            _kind == SqlMemoryFooterKind.Loading && IsVisible && SqlAssistChrome.MotionEnabled
                ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);
    }

    private static Geometry Frozen(Geometry geometry) { geometry.Freeze(); return geometry; }
}
