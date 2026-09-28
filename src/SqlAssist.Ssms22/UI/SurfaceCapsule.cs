using System;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 浮層的膠囊：通知島收合時與浮動預覽等內容時是同一顆。
/// </summary>
/// <remarks>
/// 兩個浮層會同時出現在使用者眼前，高度、圓角、左右留白與「圖示 → 文字」的間距只要差一點，
/// 看起來就是兩種東西。寬度照文字量，但有上下限：太窄的膠囊讀起來像按鈕，太寬的擋住 SQL。
/// </remarks>
internal static class SurfaceCapsule
{
    public const double Height = 32;
    public const double Radius = Height / 2;
    public const double MinWidth = 160;
    public const double MaxWidth = 320;

    /// <summary>出現時從這麼大的圓點長出來，消失時縮回它再淡掉。</summary>
    public const double DotSize = 12;

    /// <summary>左距、圖示與文字之間、右距。</summary>
    private const double Padding = 12;
    private const double Gap = 8;
    private const double End = 14;

    /// <summary>膠囊文字的字級；不跟預覽字級走，那是內容的可讀性，膠囊是外框。</summary>
    public const double TextSize = 12;

    /// <summary>放得下這段文字的膠囊寬度。</summary>
    public static double Width(string text)
    {
        var probe = new TextBlock { Text = text, FontFamily = SqlAssistChrome.InterfaceFont, FontSize = TextSize };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chrome = Padding + SurfaceStatusIcon.Size + Gap + End;
        return Math.Ceiling(Math.Max(MinWidth, Math.Min(MaxWidth, chrome + probe.DesiredSize.Width)));
    }

    /// <summary>圖示一欄、文字一欄；留白照膠囊的規格，呼叫端只給兩個元素。</summary>
    public static Grid CreateLayout(SurfaceStatusIcon icon, FrameworkElement text)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Padding + SurfaceStatusIcon.Size + Gap) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        icon.Margin = new Thickness(Padding, 0, 0, 0);
        layout.Children.Add(icon);
        text.Margin = new Thickness(0, 0, End, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);
        return layout;
    }
}
