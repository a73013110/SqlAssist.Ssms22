using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 用量頁的量表：淡色軌道上一條依分級著色的填滿；可切換成不確定進度的滑動段。
/// </summary>
/// <remarks>
/// 長度變化是狀態回饋：從舊值滑到新值，讓使用者看得出清理後「少了多少」。
/// 只動 <see cref="ScaleTransform"/>，不改版面尺寸；動畫結束交還基底值，關掉動畫時直接落在終值。
/// 顏色只是輔助，旁邊一定有數值與分級文字。
/// </remarks>
internal sealed class SqlUsageMeter : Grid
{
    /// <summary>長度轉換的時間；狀態回饋的縮放上限是 400 ms，這裡留一點餘裕。</summary>
    public static readonly TimeSpan FillDuration = TimeSpan.FromMilliseconds(360);

    private static readonly TimeSpan SweepDuration = TimeSpan.FromMilliseconds(1100);

    private readonly Border _fill;
    private readonly Border _sweep;
    private readonly ScaleTransform _scale = new(0, 1);
    private readonly TranslateTransform _offset = new();
    private bool _indeterminate;

    public SqlUsageMeter(double height = 6)
    {
        Height = height;
        ClipToBounds = true;
        var radius = new CornerRadius(height / 2);
        var track = new Border { CornerRadius = radius };
        track.SetResourceReference(Border.BackgroundProperty, ThemeBrush.SegmentTrack);
        Children.Add(track);

        _fill = new Border { CornerRadius = radius, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _scale };
        _fill.SetResourceReference(Border.BackgroundProperty, ThemeBrush.MeterNormal);
        Children.Add(_fill);

        _sweep = new Border
        {
            CornerRadius = radius, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed,
            RenderTransform = _offset
        };
        _sweep.SetResourceReference(Border.BackgroundProperty, ThemeBrush.MeterNormal);
        Children.Add(_sweep);

        IsHitTestVisible = false;
        SizeChanged += (_, _) => UpdateSweep();
        IsVisibleChanged += (_, _) => UpdateSweep();
        Unloaded += (_, _) => _offset.BeginAnimation(TranslateTransform.XProperty, null);
    }

    /// <summary>目前填滿的比例（0～1）；超過上限時畫滿。</summary>
    public double Value => _scale.ScaleX;

    public SqlMemoryUsageSeverity Severity { get; private set; }

    /// <param name="ratio">null 表示沒有上限；軌道留空，畫面只靠數字。</param>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public void SetValue(double? ratio, SqlMemoryUsageSeverity severity, bool? motion = null)
    {
        IsIndeterminate = false;
        Severity = severity;
        _fill.SetResourceReference(Border.BackgroundProperty, Brush(severity));
        var target = ratio is { } value ? double.IsNaN(value) ? 0 : Math.Max(0, Math.Min(1, value)) : 0;
        var from = _scale.ScaleX;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.ScaleX = target;
        AutomationProperties.SetItemStatus(this, ratio is { } shown ? SqlMemoryUsageSummary.Percent(shown) : "不限");
        if (!(motion ?? SqlAssistChrome.MotionEnabled) || Math.Abs(from - target) < 0.001) return;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, target, FillDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    /// <summary>長時間操作的進度；比例未知，只表示「還在做」。動畫關閉時改為靜止的半段。</summary>
    public bool IsIndeterminate
    {
        get => _indeterminate;
        set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            _fill.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _sweep.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateSweep();
        }
    }

    public static ThemeBrush Brush(SqlMemoryUsageSeverity severity) => severity switch
    {
        SqlMemoryUsageSeverity.Critical => ThemeBrush.MeterCritical,
        SqlMemoryUsageSeverity.Warning => ThemeBrush.MeterWarning,
        _ => ThemeBrush.MeterNormal,
    };

    private void UpdateSweep()
    {
        var width = ActualWidth;
        _sweep.Width = Math.Max(0, width * 0.3);
        // 不可見時停轉：背景工具窗不持續算繪，與載入圖示同一條規則。
        if (!_indeterminate || !IsVisible || width <= 0 || !SqlAssistChrome.MotionEnabled)
        {
            _offset.BeginAnimation(TranslateTransform.XProperty, null);
            _offset.X = _indeterminate ? width * 0.35 : 0;
            return;
        }
        _offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-_sweep.Width, width, SweepDuration)
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            RepeatBehavior = RepeatBehavior.Forever
        });
    }
}
