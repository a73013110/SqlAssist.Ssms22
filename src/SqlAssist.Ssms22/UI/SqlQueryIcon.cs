using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

/// <summary>語意圖示插槽；宿主提供原生影像，純 WPF 與不可用時保留向量備援。</summary>
internal sealed class SqlQueryIcon : Decorator
{
    private static readonly Style IconStyle = CreateIconStyle();
    internal static Func<string, FrameworkElement?>? NativeImageFactory { get; set; }

    public static readonly DependencyProperty IconNameProperty = DependencyProperty.Register(
        nameof(IconName), typeof(string), typeof(SqlQueryIcon),
        new PropertyMetadata("", (sender, _) => ((SqlQueryIcon)sender).UpdateImage()));

    public string IconName
    {
        get => (string)GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public SqlQueryIcon()
    {
        Width = Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        // 預設色放 Style，不能以 local value 蓋掉資料樣板的選取前景繫結。
        Style = IconStyle;
    }

    private static Style CreateIconStyle()
    {
        var style = new Style(typeof(SqlQueryIcon));
        style.Setters.Add(ThemeResourceSet.Setter(TextElement.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
    }

    private void UpdateImage()
    {
        if (NativeImageFactory?.Invoke(IconName) is { } image) { Child = image; return; }
        var path = new Path
        {
            Data = SqlAssistChrome.QueryIcon(IconName), Width = 14, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.None, StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        path.SetBinding(Shape.StrokeProperty, new Binding { Source = this, Path = new PropertyPath(TextElement.ForegroundProperty) });
        Child = path;
    }
}
