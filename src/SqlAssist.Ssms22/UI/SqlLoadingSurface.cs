using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>內容上的共用忙碌回饋，不另佔狀態列；不可見時停止動畫。</summary>
internal sealed class SqlLoadingSurface : Grid
{
    private readonly FrameworkElement _indicator;
    private readonly RotateTransform _rotation = new();
    public bool IsLoading
    {
        get => _indicator.Visibility == Visibility.Visible;
        set
        {
            if (IsLoading == value) return;
            _indicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed; UpdateAnimation();
        }
    }

    public SqlLoadingSurface(UIElement content)
    {
        Children.Add(content);
        _indicator = SqlAssistChrome.CreateLoadingIndicator(_rotation);
        Children.Add(_indicator);
        IsLoading = false;
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void UpdateAnimation()
    {
        // 動畫關閉時保留靜態忙碌圖示；不可見時停轉，不讓背景工具窗持續算繪。
        _rotation.BeginAnimation(RotateTransform.AngleProperty,
            IsLoading && IsVisible && SqlAssistChrome.MotionEnabled
                ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);
    }
}
