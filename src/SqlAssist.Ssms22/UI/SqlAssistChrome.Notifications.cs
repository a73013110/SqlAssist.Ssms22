using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

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
        // 保留 CreateButton 的動態前景與樣板；直接換 Style 會讓通知圖示在深色主題退回黑色。
        style.BasedOn = button.Style;
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Arrow));
        style.Triggers.Add(disabled);
        button.Style = style;
    }

    // 錨在宿主內容區的右上：編輯器的 adornment 座標與視窗內容區都天然避開文件分頁、
    // 工具列與主視窗控制鈕。
    internal static Point NotificationAnchor(Size viewport, Size panel)
    {
        var marginX = Math.Min(4, Math.Max(0, (viewport.Width - panel.Width) / 2));
        var marginY = Math.Min(8, Math.Max(0, (viewport.Height - panel.Height) / 2));
        return new Point(Math.Max(0, viewport.Width - panel.Width - marginX), marginY);
    }

    // 固定 16 單位畫布縮至 12 DIP，不能依各形狀的 Bounds 拉伸，否則勾號會偏心。
    internal static Geometry NotificationGeometry(string data)
    {
        var geometry = Geometry.Parse(data).Clone();
        geometry.Transform = new ScaleTransform(0.75, 0.75);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 通知表面的材質：玻璃底、邊緣與點陣快取的單層柔影；高對比退回實色並拿掉柔影。
    /// </summary>
    /// <remarks>
    /// 柔影掛在只有底色的那一層，不掛在內容上：掛在內容上時文字也會帶著一圈模糊。
    /// 依 DPI 給快取倍率，150%／200% 才不會糊；捲動或變形之外的影格只是搬一張圖。
    /// </remarks>
    internal static void ApplyNotificationMaterial(Border surface, UIElement? sheen, bool glass)
    {
        if (sheen is not null) sheen.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        if (glass)
        {
            surface.SetResourceReference(Border.BackgroundProperty, ThemeResourceSet.NotificationGlassKey);
            surface.SetResourceReference(Border.BorderBrushProperty, ThemeResourceSet.NotificationRimKey);
            surface.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.14 };
        }
        else
        {
            surface.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
            surface.WithTheme(Border.BorderBrushProperty, ThemeBrush.Border);
            surface.Effect = null;
        }

        UpdateNotificationShadowCache(surface);
    }

    internal static void UpdateNotificationShadowCache(UIElement surface) =>
        surface.CacheMode = surface.Effect is null ? null
            : new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(surface).DpiScaleX, SnapsToDevicePixels = true };

    /// <summary>通知島旁邊的衛星：直徑 32 DIP 的圓鈕，停駐與鍵盤焦點只換邊框與底色。</summary>
    internal static Button CreateNotificationSatellite(string name)
    {
        var circle = new FrameworkElementFactory(typeof(Border)) { Name = "bg" };
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        circle.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        circle.SetBinding(Border.BackgroundProperty, TemplatedParent(nameof(Control.Background)));
        circle.SetBinding(Border.BorderBrushProperty, TemplatedParent(nameof(Control.BorderBrush)));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        circle.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = circle };
        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "bg");
        AddTrigger(template, UIElement.IsKeyboardFocusedProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "bg");
        AddTrigger(template, ButtonBase.IsPressedProperty, Border.BackgroundProperty, ThemeBrush.RowPressed, "bg");
        var button = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0), Template = template, ToolTip = name,
            FocusVisualStyle = null
        };
        ApplyNotificationCursor(button);
        AutomationProperties.SetName(button, name);
        return button;
    }
}
