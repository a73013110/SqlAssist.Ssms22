using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace SqlAssist.Ssms22.UI;

/// <summary>唯一的 SSMS 主題接線；只在主題改變時解析、推導及發布筆刷。</summary>
internal static class VsThemeBrushes
{
    private static readonly ThemeResourceSet Palette = new();
    private static Dispatcher? _dispatcher;
    private static ThemeRefreshQueue? _refreshQueue;

    public static event EventHandler? Changed;

    public static Brush Get(ThemeBrush key)
    {
        Initialize();
        return Palette.Get(key);
    }

    public static void Apply(FrameworkElement root)
    {
        Initialize();
        if (!root.Resources.MergedDictionaries.Contains(Palette.Resources))
        {
            root.Resources.MergedDictionaries.Add(Palette.Resources);
        }
    }

    public static void Initialize()
    {
        if (_dispatcher is not null)
        {
            _dispatcher.VerifyAccess();
            return;
        }

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        Refresh();
        _refreshQueue = new ThemeRefreshQueue(dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新佈景主題筆刷", Refresh));
        VSColorTheme.ThemeChanged += OnThemeChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public static void Shutdown()
    {
        VSColorTheme.ThemeChanged -= OnThemeChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _refreshQueue?.Dispose();
        _refreshQueue = null;
        _dispatcher = null;
    }

    private static void OnThemeChanged(ThemeChangedEventArgs args) => QueueRefresh();

    private static void OnSystemParametersChanged(object sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast))
        {
            QueueRefresh();
        }
    }

    private static void QueueRefresh()
    {
        // 等殼層併入新資源後再讀；同一輪廣播只發布一次，不在每個查詢視窗重查。
        SqlAssistPlatformGuard.Probe("排程佈景主題更新", () => _refreshQueue?.Request());
    }

    private static void Refresh()
    {
        var highContrast = SystemParameters.HighContrast;
        var window = ResolvePair(
            EnvironmentColors.ToolWindowBackgroundBrushKey, EnvironmentColors.ToolWindowTextBrushKey,
            EnvironmentColors.ToolWindowBackgroundColorKey, EnvironmentColors.ToolWindowTextColorKey,
            SystemColors.WindowColor, SystemColors.WindowTextColor);
        var list = ResolvePair(
            EnvironmentColors.ToolTipBrushKey, EnvironmentColors.ToolTipTextBrushKey,
            EnvironmentColors.ToolTipColorKey, EnvironmentColors.ToolTipTextColorKey,
            window.Background, window.Foreground);

        // 高對比尊重系統的完整前景／背景組，不能把選取色降成 12% 透明。
        var foreground = highContrast ? SystemColors.WindowTextColor : list.Foreground;
        var background = highContrast ? SystemColors.WindowColor : list.Background;
        var dim = highContrast ? foreground : Resolve(
            EnvironmentColors.SystemGrayTextBrushKey, EnvironmentColors.SystemGrayTextColorKey, foreground);
        var border = highContrast ? foreground : Resolve(
            EnvironmentColors.ToolTipBorderBrushKey, EnvironmentColors.ToolTipBorderColorKey, foreground);
        var badge = Overlay(foreground, 0.07);
        var accent = Resolve(EnvironmentColors.AccentPaleBrushKey, EnvironmentColors.AccentPaleColorKey, background);
        var accentBorder = Resolve(EnvironmentColors.AccentBorderBrushKey, EnvironmentColors.AccentBorderColorKey, border);

        Palette.Update(new Dictionary<ThemeBrush, Color>
        {
            [ThemeBrush.ListBackground] = background,
            [ThemeBrush.ListForeground] = foreground,
            [ThemeBrush.DimForeground] = ThemeColorMath.EnsureContrast(dim, background, foreground),
            [ThemeBrush.WindowBackground] = highContrast ? background : window.Background,
            [ThemeBrush.WindowForeground] = highContrast ? foreground : window.Foreground,
            [ThemeBrush.Border] = border,
            [ThemeBrush.Hairline] = highContrast ? foreground : Overlay(foreground, 0.10),
            [ThemeBrush.RowHover] = highContrast ? SystemColors.HighlightColor : Overlay(foreground, 0.05),
            [ThemeBrush.RowSelected] = highContrast ? SystemColors.HighlightColor : Overlay(foreground, 0.12),
            [ThemeBrush.SelectedForeground] = highContrast ? SystemColors.HighlightTextColor : foreground,
            [ThemeBrush.RowPressed] = highContrast ? SystemColors.HighlightColor : Overlay(foreground, 0.18),
            [ThemeBrush.RowAlternate] = highContrast ? background : Overlay(foreground, 0.045),
            [ThemeBrush.SegmentTrack] = highContrast ? background : Overlay(foreground, 0.06),
            [ThemeBrush.BadgeBackground] = highContrast ? background : badge,
            // 自訂 Accent 若與一般文字衝突，只降級徽章底色，不修改使用者的 SSMS 設定。
            [ThemeBrush.AccentBackground] = highContrast ? background :
                ThemeColorMath.Contrast(foreground, ThemeColorMath.Composite(accent, background)) >= 4.5 ? accent : badge,
            [ThemeBrush.AccentBorder] = highContrast ? foreground : accentBorder
        });

        // 捲軸、選單與下拉 Popup 交給 SSMS 的完整樣式，不只覆寫控制項表面的底色。
        PublishStyle(typeof(ScrollBar), VsResourceKeys.ScrollBarStyleKey);
        PublishStyle(typeof(ComboBox), VsResourceKeys.ComboBoxStyleKey);
        PublishStyle(typeof(ComboBoxItem), VsResourceKeys.ComboBoxItemStyleKey);
        PublishStyle(typeof(ContextMenu), VsResourceKeys.ContextMenuStyleKey);
        PublishStyle(typeof(ToolTip), VsResourceKeys.LargeToolTipStyleKey);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void PublishStyle(Type controlType, object key)
    {
        SqlAssistPlatformGuard.Probe("解析 SSMS 控制項樣式", () =>
        {
            if (Application.Current?.TryFindResource(key) is Style style &&
                style.TargetType.IsAssignableFrom(controlType))
            {
                if (!ReferenceEquals(Palette.Resources[controlType], style))
                {
                    Palette.Resources[controlType] = style;
                }
            }
            else
            {
                // 主題移除某個樣式時不能保留上一個主題的快照；退回局部系統色別名。
                Palette.Resources.Remove(controlType);
            }
        });
    }

    private static Color Overlay(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);

    private static (Color Background, Color Foreground) ResolvePair(
        object backgroundKey, object foregroundKey, ThemeResourceKey backgroundColorKey,
        ThemeResourceKey foregroundColorKey, Color fallbackBackground, Color fallbackForeground)
    {
        var background = TryResolve(backgroundKey, backgroundColorKey);
        var foreground = TryResolve(foregroundKey, foregroundColorKey);
        // 缺一個鍵就整組退回；逐項退回 Windows 會把 SSMS 暗底與系統黑字湊在一起。
        return background.HasValue && foreground.HasValue
            ? (background.Value, foreground.Value)
            : (fallbackBackground, fallbackForeground);
    }

    private static Color Resolve(object key, ThemeResourceKey colorKey, Color fallback) =>
        TryResolve(key, colorKey) ?? fallback;

    private static Color? TryResolve(object key, ThemeResourceKey colorKey) => SqlAssistPlatformGuard.Probe<Color?>(
        "解析佈景主題顏色",
        () =>
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            {
                return brush.Color;
            }

            // WPF 字典尚未併入時仍先問殼層；Windows 系統色是最後且成對的備援。
            var color = VSColorTheme.GetThemedColor(colorKey);
            return color.IsEmpty ? (Color?)null : Color.FromArgb(color.A, color.R, color.G, color.B);
        },
        fallback: null);
}
