using System;
using System.Collections.Generic;
using System.Windows.Media;
using SqlAssist.Core.Settings;

namespace SqlAssist.Ssms22.UI;

/// <summary>區塊各表面共用單一色相；輸入取自目前檢視，不保存深淺主題旗標。</summary>
internal static class BlockPalette
{
    public static IReadOnlyDictionary<ThemeBrush, Color> Create(Color background, Color foreground,
        Color accent, string? preference, bool highContrast, SqlAssistSettings? settings = null)
    {
        if (!highContrast && SqlColorPreference.TryParseRgb(preference, out var rgb))
            accent = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var fallback = ThemeColorMath.Contrast(Colors.Black, background) >=
            ThemeColorMath.Contrast(Colors.White, background) ? Colors.Black : Colors.White;
        var text = ThemeColorMath.EnsureContrast(ThemeColorMath.Composite(foreground, background), background, fallback);
        var graphic = highContrast ? text : ThemeColorMath.EnsureGraphicContrast(accent, background);
        var range = highContrast ? Colors.Transparent : Tint(graphic, background, text, 0.12);
        var hint = highContrast ? background : ThemeColorMath.Composite(Tint(graphic, background, text, 0.06), background);
        var globalInk = ReadOptionalColor(settings?.BlockKeywordForeground);
        var keyword = Endpoint(globalInk, settings?.BlockKeywordBackground);
        // 細項只覆寫指定通道；留空或無效值繼承全域「原始基準值」，再依實際背景校正。
        var symbol = Endpoint(ReadOptionalColor(settings?.BlockSymbolForeground) ?? globalInk, settings?.BlockSymbolBackground,
            ReadColor(settings?.BlockKeywordBackground, accent));

        (Color Foreground, Color Background) Endpoint(Color? explicitInk, string? backgroundPreference, Color? defaultBackground = null)
        {
            // 端點面積小，採實色高亮而非區間淡底；分類標籤才能改字色，marker 前景其實是框線。
            if (highContrast) return (background, text);
            var fill = ThemeColorMath.EnsureGraphicContrast(ReadColor(backgroundPreference, defaultBackground ?? accent), background);
            if (explicitInk is { } requested)
                return (requested, ThemeColorMath.EnsureBackgroundForText(fill, requested, background));
            var ink = ThemeColorMath.EnsureTextContrast(foreground, fill);
            return (ink, fill);
        }
        return new Dictionary<ThemeBrush, Color>
        {
            [ThemeBrush.Block] = graphic,
            [ThemeBrush.BlockTry] = graphic,
            [ThemeBrush.BlockCatch] = graphic,
            [ThemeBrush.BlockCase] = graphic,
            [ThemeBrush.BlockParenthesis] = graphic,
            [ThemeBrush.BlockBracket] = graphic,
            [ThemeBrush.BlockRange] = range,
            [ThemeBrush.BlockHintBackground] = hint,
            [ThemeBrush.BlockHintForeground] = text,
            [ThemeBrush.BlockHintHoverForeground] = highContrast ? text : ThemeColorMath.EnsureTextContrast(graphic, hint),
            [ThemeBrush.BlockKeywordForeground] = keyword.Foreground,
            [ThemeBrush.BlockKeywordBackground] = keyword.Background,
            [ThemeBrush.BlockSymbolForeground] = symbol.Foreground,
            [ThemeBrush.BlockSymbolBackground] = symbol.Background
        };
    }

    private static Color ReadColor(string? preference, Color fallback) =>
        ReadOptionalColor(preference) ?? fallback;

    private static Color? ReadOptionalColor(string? preference) =>
        SqlColorPreference.TryParseRgb(preference, out var rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : null;

    private static Color Tint(Color accent, Color background, Color text, double opacity)
    {
        var alpha = (byte)Math.Round(255 * opacity);
        // 一般文字至少 4.5:1；淡底不要求圖形的 3:1，避免大區間壓過 SQL 與原生選取。
        while (alpha > 0 && ThemeColorMath.Contrast(text,
            ThemeColorMath.Composite(Color.FromArgb(alpha, accent.R, accent.G, accent.B), background)) < 4.5)
            alpha--;
        return Color.FromArgb(alpha, accent.R, accent.G, accent.B);
    }
}
