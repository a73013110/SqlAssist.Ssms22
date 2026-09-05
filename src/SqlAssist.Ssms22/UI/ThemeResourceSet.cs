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
    AccentBorder
}

/// <summary>同一份動態資源供所有視窗與獨立 Popup 使用，不保存任何控制項參考。</summary>
internal sealed class ThemeResourceSet
{
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
