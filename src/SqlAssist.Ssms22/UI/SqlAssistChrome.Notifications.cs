using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlAssistChrome
{
    /// <summary>只有圖示的按鈕：名稱同時是提示與朗讀名稱。</summary>
    internal static void SetButtonName(Button button, string name)
    {
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
    }

    internal static Button CreateNotificationButton(string name, string geometry)
    {
        var button = CreateButton(name, DefaultMetrics);
        // 點擊區比 10 DIP 的筆畫大一圈；筆畫置中，右緣與列上的文字收在同一條線（NotificationLayout.TextEnd）。
        button.Width = NotificationLayout.CloseButton; button.Height = NotificationLayout.CloseButton; button.MinWidth = 0;
        button.Padding = new Thickness(0); button.Margin = new Thickness(0);
        SetButtonName(button, name);
        button.Content = new Path
        {
            Data = Geometry.Parse(geometry), Width = 10, Height = 10,
            Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        }.WithTheme(Shape.StrokeProperty, ThemeBrush.ListForeground);
        return button;
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
    /// 柔影與浮動預覽同一份（<see cref="SetSurfaceShadow"/>）；玻璃關掉時一起拿掉。
    /// </remarks>
    internal static void ApplyNotificationMaterial(Border surface, UIElement? sheen, bool glass)
    {
        if (sheen is not null) sheen.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        if (glass)
        {
            surface.SetResourceReference(Border.BackgroundProperty, ThemeResourceSet.NotificationGlassKey);
            surface.SetResourceReference(Border.BorderBrushProperty, ThemeResourceSet.NotificationRimKey);
        }
        else
        {
            surface.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
            surface.WithTheme(Border.BorderBrushProperty, ThemeBrush.Border);
        }

        SetSurfaceShadow(surface, glass);
    }

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
        SetClickCursor(strip);
    }
}
