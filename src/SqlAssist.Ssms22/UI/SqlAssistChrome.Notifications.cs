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
    internal static Button CreateNotificationButton(string name, string geometry)
    {
        var button = CreateButton(name, DefaultMetrics);
        // 點擊區比 10 DIP 的筆畫大一圈；筆畫置中，右緣與列上的文字收在同一條線（NotificationLayout.TextEnd）。
        button.Width = NotificationLayout.CloseButton; button.Height = NotificationLayout.CloseButton; button.MinWidth = 0;
        ApplyNotificationCursor(button);
        button.Padding = new Thickness(0); button.Margin = new Thickness(0);
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

    /// <summary>
    /// 通知島的附條：貼著卡片底邊、整條可按的一列，上緣一條髮絲線。
    /// </summary>
    /// <remarks>
    /// 停駐、按下與鍵盤焦點只換底色與內框，內框平時就預留 1 DIP，換狀態時字不位移。
    /// 底色不畫圓角：附條貼在島嶼底邊，下緣的圓角由島嶼的裁切決定，自己畫的話變形途中會跟裁切錯開。
    /// </remarks>
    internal static void ApplyNotificationStrip(Button strip)
    {
        var divider = new FrameworkElementFactory(typeof(Border));
        divider.SetValue(Border.BorderThicknessProperty, new Thickness(0, 1, 0, 0));
        divider.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        var surface = new FrameworkElementFactory(typeof(Border)) { Name = "bg" };
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        surface.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        surface.SetBinding(Border.PaddingProperty, TemplatedParent(nameof(Control.Padding)));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        divider.AppendChild(surface);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = divider };
        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BackgroundProperty, ThemeBrush.RowHover, "bg");
        AddTrigger(template, ButtonBase.IsPressedProperty, Border.BackgroundProperty, ThemeBrush.RowPressed, "bg");
        AddTrigger(template, UIElement.IsKeyboardFocusedProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "bg");
        strip.Template = template;
        strip.FocusVisualStyle = null;
        strip.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        strip.FontFamily = InterfaceFont;
        ApplyNotificationCursor(strip);
    }
}
