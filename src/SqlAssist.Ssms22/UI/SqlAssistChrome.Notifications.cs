using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

/// <summary>提示錨在內容區的哪一角；<see cref="NotificationPosition.BottomRight"/> 尚未有使用者設定。</summary>
internal enum NotificationPosition { TopRight, BottomRight }

internal static partial class SqlAssistChrome
{
    /// <summary>卡片本身的控制項仍在這一份建立；呼叫端從卡片的屬性取抬頭與明細，不另外接線。</summary>
    public static NotificationCard CreateNotificationCard() => new();

    public static void SetNotificationSummary(Button summary, string text)
    {
        if (summary.Content is TextBlock heading) heading.Text = text;
        summary.ToolTip = text + "\n展開或收合通知明細";
        AutomationProperties.SetName(summary, text);
    }

    internal static Button CreateNotificationButton(string name, string geometry)
    {
        var button = CreateButton(name, DefaultMetrics);
        button.Width = 16; button.Height = 16; button.MinWidth = 0;
        ApplyNotificationCursor(button);
        button.Padding = new Thickness(2); button.Margin = new Thickness(0);
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
        button.Content = new Path
        {
            Data = Geometry.Parse(geometry), Width = 10, Height = 10,
            Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        }.WithTheme(Shape.StrokeProperty, ThemeBrush.ListForeground);
        return button;
    }

    internal static void ApplyNotificationCursor(Button button)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Arrow));
        style.Triggers.Add(disabled);
        button.Style = style;
    }

    // adornment 座標屬於編輯器內容區，天然避開文件分頁、工具列與主視窗控制鈕。
    internal static Point NotificationAnchor(Size viewport, Size panel, NotificationPosition position)
    {
        var marginX = Math.Min(4, Math.Max(0, (viewport.Width - panel.Width) / 2));
        var marginY = Math.Min(8, Math.Max(0, (viewport.Height - panel.Height) / 2));
        return new Point(Math.Max(0, viewport.Width - panel.Width - marginX),
            position == NotificationPosition.TopRight ? marginY : Math.Max(0, viewport.Height - panel.Height - marginY));
    }

    // 明確覆寫時只略過 OS 動畫偏好，不影響高對比與元件總開關。
    internal static bool NotificationMotionEnabled(bool enabled, bool force, bool systemAnimation, bool highContrast) =>
        enabled && !highContrast && (force || systemAnimation);

    internal static DoubleAnimation NotificationAnimation(double from, double to, int milliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    // 固定 16 單位畫布縮至 12 DIP，不能依各形狀的 Bounds 拉伸，否則勾號會偏心。
    internal static Geometry NotificationGeometry(string data)
    {
        var geometry = Geometry.Parse(data).Clone();
        geometry.Transform = new ScaleTransform(0.75, 0.75);
        geometry.Freeze();
        return geometry;
    }

    internal static void AnimateNotificationResult(ScaleTransform scale, TranslateTransform shake, NotificationVisualStatus state)
    {
        if (state == NotificationVisualStatus.Completed)
        {
            var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.65, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        else if (state == NotificationVisualStatus.Failed)
        {
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            var values = new[] { 0d, -2, 2, -1, 1, 0 };
            for (var i = 0; i < values.Length; i++)
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 50))));
            shake.BeginAnimation(TranslateTransform.XProperty, animation);
        }
    }
}
