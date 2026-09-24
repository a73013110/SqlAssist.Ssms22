using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知島上的 12 DIP 狀態圖示：膠囊、清單抬頭、各列與附條共用。
/// </summary>
/// <remarks>
/// 幾何、配色、完成的描繪、失敗的短震與進度圈都只有這一份。各處自己畫的版本，膠囊與列的
/// 勾號一個彈出、一個不動，「其他」狀態一處是叉號、一處是時鐘。
///
/// 要不要播回饋由呼叫端決定：列是狀態變了才播，膠囊只在「執行中 → 成功」那一刻播，
/// 失敗的短震則跟著狀態機的 <c>ShakeCount</c>。這裡只保證同一次呼叫不重播。
/// </remarks>
internal sealed class NotificationStatusIcon : Decorator
{
    public const double Size = 12;

    private static readonly Geometry Running = SqlAssistChrome.NotificationGeometry("M8,1 A7,7 0 1 1 1,8");
    private static readonly Geometry Check = SqlAssistChrome.NotificationGeometry("M3,8 L6.5,11.5 L13,4.5");
    private static readonly Geometry Alert = SqlAssistChrome.NotificationGeometry("M8,1 L15,14 L1,14 Z M8,5 L8,9 M8,11 L8,12");
    private static readonly Geometry Cross = SqlAssistChrome.NotificationGeometry("M3,3 L13,13 M13,3 L3,13");
    private static readonly Geometry Clock = SqlAssistChrome.NotificationGeometry("M8,1 A7,7 0 1 1 7.99,1 M8,4 L8,8 L11,10");

    /// <summary>勾號的筆畫長度（DIP，已縮到 12 DIP 畫布）；描繪以它換算虛線的長度。</summary>
    internal static readonly double CheckStrokeLength = StrokeLength(Check);

    private const double Thickness = 1.4;

    private readonly Path _path;
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _shake = new();
    private bool _applied;
    private int _generation;

    public NotificationStatusIcon()
    {
        Width = Size; Height = Size;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        var transforms = new TransformGroup();
        transforms.Children.Add(_spin); transforms.Children.Add(_scale); transforms.Children.Add(_shake);
        _path = new Path
        {
            Width = Size, Height = Size, Stretch = Stretch.None, StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = transforms,
        };
        Child = _path;
        Apply(NotificationVisualStatus.Pending);
    }

    public NotificationVisualStatus Status { get; private set; } = NotificationVisualStatus.Pending;

    /// <summary>進度圈正在轉。</summary>
    public bool IsSpinning { get; private set; }

    /// <summary>描繪、微彈或短震還在播。</summary>
    internal bool IsPlayingFeedback =>
        _path.HasAnimatedProperties || _scale.HasAnimatedProperties || _shake.HasAnimatedProperties;

    internal Path Glyph => _path;

    /// <summary>換狀態；<paramref name="feedback"/> 為 true 時播這一次轉換的回饋（成功描勾、失敗短震）。</summary>
    public void SetStatus(NotificationVisualStatus status, bool feedback)
    {
        if (!_applied || status != Status) Apply(status);
        if (!feedback) return;
        if (status == NotificationVisualStatus.Completed) PlayCheck();
        else if (status == NotificationVisualStatus.Failed) Shake();
    }

    /// <summary>進度圈；只有執行中的幾何轉起來才看得出是進度，呼叫端保證同一時間只有一個在轉。</summary>
    public void Spin(bool spin)
    {
        if (IsSpinning == spin) return;
        IsSpinning = spin;
        _spin.BeginAnimation(RotateTransform.AngleProperty, spin ? NotificationMotion.Spinner() : null);
    }

    public void Shake() => NotificationMotion.PlayShake(_shake);

    /// <summary>停掉進度圈與回饋並交還基底值。</summary>
    public void StopMotion()
    {
        Spin(false);
        ClearDraw();
        NotificationMotion.StopResult(_scale, _shake);
    }

    private void Apply(NotificationVisualStatus status)
    {
        _applied = true;
        Status = status;
        ClearDraw();
        _path.Data = status switch
        {
            NotificationVisualStatus.Running => Running,
            NotificationVisualStatus.Completed => Check,
            NotificationVisualStatus.Failed => Alert,
            NotificationVisualStatus.Canceled => Cross,
            _ => Clock,
        };
        switch (status)
        {
            case NotificationVisualStatus.Running:
                _path.SetResourceReference(Shape.StrokeProperty, ThemeResourceSet.NotificationSpinnerKey);
                break;
            case NotificationVisualStatus.Completed:
                _path.WithTheme(Shape.StrokeProperty, ThemeBrush.NotificationSuccess);
                break;
            case NotificationVisualStatus.Failed:
                _path.WithTheme(Shape.StrokeProperty, ThemeBrush.NotificationFailure);
                break;
            default:
                _path.WithTheme(Shape.StrokeProperty, ThemeBrush.DimForeground);
                break;
        }
    }

    /// <summary>
    /// 勾號從左到右描出來，描完微彈一下。
    /// </summary>
    /// <remarks>
    /// 虛線的單位是線寬的倍數。空白段多留兩倍線寬、起點多退一倍：剛好等長的版本在第一格就會在
    /// 起點露出半個圓頭。描完拿掉虛線，之後換主題或 DPI 重畫時才不會殘留一段空白。
    /// </remarks>
    private void PlayCheck()
    {
        var generation = ++_generation;
        var dash = CheckStrokeLength / Thickness;
        _path.StrokeDashArray = new DoubleCollection { dash, dash + 2 };
        var draw = NotificationMotion.Ease(dash + 1, 0, NotificationMotion.CheckDraw);
        draw.FillBehavior = FillBehavior.Stop;
        draw.Completed += (_, _) =>
        {
            if (generation != _generation) return;
            _path.StrokeDashArray = null;
            NotificationMotion.PlaySettle(_scale);
        };
        _path.BeginAnimation(Shape.StrokeDashOffsetProperty, draw);
    }

    private void ClearDraw()
    {
        _generation++;
        _path.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        _path.StrokeDashArray = null;
    }

    private static double StrokeLength(Geometry geometry)
    {
        var length = 0d;
        foreach (var figure in geometry.GetFlattenedPathGeometry().Figures)
        {
            var last = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                var points = segment switch
                {
                    LineSegment line => new[] { line.Point },
                    PolyLineSegment poly => System.Linq.Enumerable.ToArray(poly.Points),
                    _ => Array.Empty<Point>(),
                };
                foreach (var point in points) { length += (point - last).Length; last = point; }
            }
        }

        return length;
    }
}
