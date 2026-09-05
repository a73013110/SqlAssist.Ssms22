using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>由殼層提供的實際色彩推導共用層級；同為淺色或深色的主題仍保留各自色系。</summary>
internal static class ThemePalette
{
    public static IReadOnlyDictionary<ThemeBrush, Color> Create(
        (Color Background, Color Foreground) window,
        (Color Background, Color Foreground) list,
        Color dim, Color border, Color accent, bool highContrast,
        (Color Background, Color Foreground) selection)
    {
        // Fluent 文字可帶透明度；先在所屬表面合成，避免選取底色讓文字再變淡一次。
        var foreground = ThemeColorMath.Composite(list.Foreground, list.Background);
        var windowForeground = ThemeColorMath.Composite(window.Foreground, window.Background);
        var background = list.Background;
        var badge = Overlay(foreground, 0.07);

        bool Readable(Color text, Color overlay) =>
            ThemeColorMath.Contrast(text, ThemeColorMath.Composite(overlay, background)) >= 4.5 &&
            ThemeColorMath.Contrast(text, ThemeColorMath.Composite(overlay, window.Background)) >= 4.5;

        Color Tint(double opacity)
        {
            // 共用按鈕會放在視窗與內容兩種底色上；不合對比時逐步減淡，而非丟掉主題色相。
            var tint = Overlay(accent, opacity);
            while (tint.A > 0 && !Readable(foreground, tint))
            {
                tint = Color.FromArgb((byte)(tint.A / 2), tint.R, tint.G, tint.B);
            }

            return tint;
        }

        return new Dictionary<ThemeBrush, Color>
        {
            [ThemeBrush.ListBackground] = background,
            [ThemeBrush.ListForeground] = foreground,
            [ThemeBrush.WindowBackground] = window.Background,
            [ThemeBrush.WindowForeground] = windowForeground,
            [ThemeBrush.DimForeground] = highContrast || !Readable(dim, Colors.Transparent) ? foreground : dim,
            [ThemeBrush.Border] = highContrast ? foreground : border,
            [ThemeBrush.Hairline] = highContrast ? foreground : Overlay(foreground, 0.10),
            [ThemeBrush.RowHover] = highContrast ? selection.Background : Tint(0.05),
            [ThemeBrush.RowSelected] = highContrast ? selection.Background : Tint(0.12),
            [ThemeBrush.SelectedForeground] = highContrast ? selection.Foreground : foreground,
            [ThemeBrush.RowPressed] = highContrast ? selection.Background : Tint(0.18),
            [ThemeBrush.RowAlternate] = highContrast ? background : Overlay(foreground, 0.045),
            [ThemeBrush.SegmentTrack] = highContrast ? background : Overlay(foreground, 0.06),
            [ThemeBrush.BadgeBackground] = highContrast ? background : badge,
            [ThemeBrush.AccentBackground] = highContrast ? background : Tint(0.12),
            [ThemeBrush.AccentBorder] = highContrast ? foreground : accent
        };
    }

    private static Color Overlay(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B);
}
