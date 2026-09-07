using System;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

internal static class ThemeColorMath
{
    /// <summary>由主題強調色旋轉色相，供種類色帶共用；不在功能層硬寫 RGB 色票。</summary>
    public static Color RotateHue(Color color, double degrees)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var delta = maximum - minimum;
        var hue = delta == 0 ? 0 : maximum == r ? 60 * ((g - b) / delta % 6) :
            maximum == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        hue = ((hue + degrees) % 360 + 360) % 360;
        var saturation = Math.Max(0.55, maximum == 0 ? 0 : delta / maximum);
        var value = Math.Max(0.4, maximum);
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        (r, g, b) = hue < 60 ? (c, x, 0.0) : hue < 120 ? (x, c, 0.0) :
            hue < 180 ? (0.0, c, x) : hue < 240 ? (0.0, x, c) : hue < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static Color EnsureContrast(Color candidate, Color background, Color fallback)
    {
        return Contrast(candidate, background) >= 4.5 ? candidate : fallback;
    }

    public static double Contrast(Color foreground, Color background)
    {
        var first = Luminance(Composite(foreground, background));
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    public static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Luminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte value)
    {
        var channel = value / 255.0;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
