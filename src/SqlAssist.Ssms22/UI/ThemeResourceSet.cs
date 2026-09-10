using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>語意鍵不含深淺判斷；控制項只描述用途，配色集中在主題來源。</summary>
internal enum ThemeBrush
{
    ListBackground,
    ListForeground,
    DimForeground,
    WindowBackground,
    WindowForeground,
    Border,
    Hairline,
    RowHover,
    RowSelected,
    SelectedForeground,
    RowPressed,
    RowAlternate,
    SegmentTrack,
    BadgeBackground,
    AccentBackground,
    AccentBorder,
    NotificationSuccess,
    NotificationFailure,
    NotificationRunning,
    NotificationRunningEnd,
    Block,
    BlockTry,
    BlockCatch,
    BlockCase,
    BlockParenthesis,
    BlockBracket,
    BlockRange,
    BlockHintBackground,
    BlockHintForeground,
    BlockHintHoverForeground,
    BlockKeywordForeground,
    BlockKeywordBackground,
    BlockSymbolForeground,
    BlockSymbolBackground
}

/// <summary>同一份動態資源供所有視窗與獨立 Popup 使用，不保存任何控制項參考。</summary>
internal sealed class ThemeResourceSet
{
    // XAML 的 x:Static 需要公開欄位；型別本身仍限於組件內部。
    public const string NotificationSpinnerKey = "SqlAssist.NotificationSpinner";
    internal const string NotificationGlassKey = "SqlAssist.NotificationGlass";
    public const string NotificationDimKey = "SqlAssist.NotificationDim";
    public const string NotificationRimKey = "SqlAssist.NotificationRim";
    public const string NotificationSheenKey = "SqlAssist.NotificationSheen";
    private static readonly (ResourceKey Brush, ResourceKey? Color, ThemeBrush Role)[] SystemAliases =
    {
        (SystemColors.WindowBrushKey, SystemColors.WindowColorKey, ThemeBrush.ListBackground),
        (SystemColors.WindowTextBrushKey, SystemColors.WindowTextColorKey, ThemeBrush.ListForeground),
        (SystemColors.ControlBrushKey, SystemColors.ControlColorKey, ThemeBrush.ListBackground),
        (SystemColors.ControlTextBrushKey, SystemColors.ControlTextColorKey, ThemeBrush.ListForeground),
        (SystemColors.ControlLightBrushKey, SystemColors.ControlLightColorKey, ThemeBrush.ListBackground),
        (SystemColors.ControlLightLightBrushKey, SystemColors.ControlLightLightColorKey, ThemeBrush.ListBackground),
        (SystemColors.ControlDarkBrushKey, SystemColors.ControlDarkColorKey, ThemeBrush.Border),
        (SystemColors.ControlDarkDarkBrushKey, SystemColors.ControlDarkDarkColorKey, ThemeBrush.Border),
        (SystemColors.GrayTextBrushKey, SystemColors.GrayTextColorKey, ThemeBrush.DimForeground),
        (SystemColors.MenuBrushKey, SystemColors.MenuColorKey, ThemeBrush.ListBackground),
        (SystemColors.MenuTextBrushKey, SystemColors.MenuTextColorKey, ThemeBrush.ListForeground),
        (SystemColors.ScrollBarBrushKey, SystemColors.ScrollBarColorKey, ThemeBrush.ListBackground),
        (SystemColors.HighlightBrushKey, SystemColors.HighlightColorKey, ThemeBrush.RowSelected),
        (SystemColors.HighlightTextBrushKey, SystemColors.HighlightTextColorKey, ThemeBrush.SelectedForeground),
        (SystemColors.InactiveSelectionHighlightBrushKey, null, ThemeBrush.RowSelected),
        (SystemColors.InactiveSelectionHighlightTextBrushKey, null, ThemeBrush.SelectedForeground)
    };

    public ResourceDictionary Resources { get; } = new();

    public void Update(IReadOnlyDictionary<ThemeBrush, Color> colors)
    {
        foreach (var pair in colors)
        {
            // 不變的筆刷保留身分，避免重複通知 WPF；凍結的是值，不是動態資源參考。
            if (Resources[pair.Key] is SolidColorBrush existing && existing.Color == pair.Value)
            {
                continue;
            }

            var brush = new SolidColorBrush(pair.Value);
            brush.Freeze();
            Resources[pair.Key] = brush;
        }

        if (colors.TryGetValue(ThemeBrush.NotificationRunning, out var running) && colors.TryGetValue(ThemeBrush.NotificationRunningEnd, out var end) &&
            (Resources[NotificationSpinnerKey] is not LinearGradientBrush gradient || gradient.GradientStops[0].Color != running || gradient.GradientStops[1].Color != end))
        {
            var spinner = new LinearGradientBrush(running, end, 45);
            spinner.Freeze();
            Resources[NotificationSpinnerKey] = spinner;
        }

        // 材質保持主題色與高覆蓋率，文字不跟著透明；高對比由表面切回實色。
        if (colors.TryGetValue(ThemeBrush.ListBackground, out var surface))
        {
            var glassColor = Color.FromArgb(224, surface.R, surface.G, surface.B);
            var foreground = colors[ThemeBrush.ListForeground];
            var sheen = Color.FromArgb(8, foreground.R, foreground.G, foreground.B);
            Color LitSurface(Color backdrop) => ThemeColorMath.Composite(sheen, ThemeColorMath.Composite(glassColor, backdrop));
            // SQL 編輯器可能使用相反的自訂底色；最差黑／白底仍須保留文字對比。
            while (glassColor.A < 255 && (ThemeColorMath.Contrast(foreground, LitSurface(Colors.Black)) < 4.5 ||
                ThemeColorMath.Contrast(foreground, LitSurface(Colors.White)) < 4.5))
                glassColor.A++;
            if (Resources[NotificationGlassKey] is not SolidColorBrush glass || glass.Color != glassColor)
            {
                var brush = new SolidColorBrush(glassColor);
                brush.Freeze();
                Resources[NotificationGlassKey] = brush;
            }
            var dim = colors[ThemeBrush.DimForeground];
            var black = LitSurface(Colors.Black);
            var white = LitSurface(Colors.White);
            dim = ThemeColorMath.EnsureTextContrast(dim, ThemeColorMath.Contrast(dim, black) < ThemeColorMath.Contrast(dim, white) ? black : white);
            if (Resources[NotificationDimKey] is not SolidColorBrush previousDim || previousDim.Color != dim)
            {
                var brush = new SolidColorBrush(dim);
                brush.Freeze();
                Resources[NotificationDimKey] = brush;
            }

            UpdateNotificationGradient(NotificationRimKey, foreground, 82, 12);
            UpdateNotificationGradient(NotificationSheenKey, foreground, 8, 0);
        }

        // 原生樣板的角落填色、預設文字選取仍可能讀系統鍵；別名只作用在本擴充根節點，
        // 不修改 Application.Resources，更不改 SSMS 或 Windows 的全域配色。
        foreach (var alias in SystemAliases)
        {
            if (Resources[alias.Role] is SolidColorBrush brush && !ReferenceEquals(Resources[alias.Brush], brush))
            {
                Resources[alias.Brush] = brush;
                if (alias.Color is { } colorKey)
                {
                    Resources[colorKey] = brush.Color;
                }
            }
        }
    }

    private void UpdateNotificationGradient(string key, Color color, byte startAlpha, byte endAlpha)
    {
        var start = Color.FromArgb(startAlpha, color.R, color.G, color.B);
        var end = Color.FromArgb(endAlpha, color.R, color.G, color.B);
        if (Resources[key] is LinearGradientBrush previous && previous.GradientStops[0].Color == start && previous.GradientStops[1].Color == end) return;
        var gradient = new LinearGradientBrush(start, end, 45);
        gradient.Freeze();
        Resources[key] = gradient;
    }

    public Brush Get(ThemeBrush key) => (Brush)Resources[key];

    public static Setter Setter(DependencyProperty property, ThemeBrush key, string? targetName = null)
    {
        return new Setter(property, new DynamicResourceExtension(key), targetName);
    }
}

internal static class ThemeResourceBinding
{
    public static T WithTheme<T>(this T element, DependencyProperty property, ThemeBrush key)
        where T : DependencyObject
    {
        // Run 是 FrameworkContentElement，不能只照顧 FrameworkElement 而漏掉標題中的文字。
        if (element is FrameworkElement visual)
        {
            visual.SetResourceReference(property, key);
        }
        else if (element is FrameworkContentElement content)
        {
            content.SetResourceReference(property, key);
        }
        else
        {
            throw new ArgumentException("只有 WPF 元素可繫結主題資源。", nameof(element));
        }

        return element;
    }
}
