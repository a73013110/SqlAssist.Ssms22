using System;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

internal static class ThemeColorMath
{
    /// <summary>圖形至少 3:1；向黑或白漸進調整，保留種類色相且不假設編輯器與殼層同色。</summary>
    public static Color EnsureGraphicContrast(Color candidate, Color background)
        => EnsureMinimumContrast(candidate, background, 3);

    public static Color EnsureTextContrast(Color candidate, Color background)
        => EnsureMinimumContrast(candidate, background, 4.5);

    /// <summary>固定使用者字色，優先同時滿足文字與外部底色對比；無法兼得時以文字可讀為先。</summary>
    public static Color EnsureBackgroundForText(Color candidate, Color foreground, Color editorBackground)
    {
        candidate = Composite(candidate, editorBackground);
        Color? readable = null;
        // 有界搜尋只發生在配色變更，不進入游標、捲動或 GetTags 路徑。
        for (var step = 0; step <= 20; step++)
        {
            for (var direction = 0; direction < 2; direction++)
            {
                var channel = direction == 0 ? (byte)0 : (byte)255;
                var adjusted = Composite(Color.FromArgb((byte)Math.Round(255 * step / 20.0), channel, channel, channel), candidate);
                if (Contrast(foreground, adjusted) < 4.5) continue;
                readable ??= adjusted;
                if (Contrast(adjusted, editorBackground) >= 3) return adjusted;
            }
        }
        // 某些指定字色無法同時滿足兩個表面；不可為了背景辨識度把自訂白字改成黑字。
        return readable ?? (Contrast(foreground, Colors.Black) >= Contrast(foreground, Colors.White) ? Colors.Black : Colors.White);
    }

    /// <summary>一般命中的底色覆蓋率；夠深到有邊界，又留得住底下的著色。</summary>
    /// <remarks>
    /// 更低的那一版在彩色深色主題上退成一層看不出邊界的薄色，而「有沒有標出來」正是使用者
    /// 唯一要從命中高亮讀到的事。
    /// </remarks>
    public const byte MatchCoverage = 0x8C;

    /// <summary>
    /// 一般命中的底色：半透明強調色鋪上去，再對<b>這個表面自己的</b>底色與最淡的前景校正。
    /// </summary>
    /// <remarks>
    /// 校正的是底色而不是字色，所以底下的語法著色整組留得住；傳進來的前景要挑最淡的那一個
    /// （指令碼是註解色），它在高亮上讀得到，其餘就都讀得到。
    ///
    /// 底色的基準由呼叫端給——工具窗是清單底色，指令碼借的是 SSMS 編輯器底色，兩者在深色
    /// 主題下不一定同深淺。拿錯基準的症狀是高亮整塊看不見，而使用者會以為命中根本沒有標出來。
    /// </remarks>
    public static Color MatchBackground(Color accent, Color foreground, Color background) =>
        EnsureBackgroundForText(Color.FromArgb(MatchCoverage, accent.R, accent.G, accent.B), foreground, background);

    /// <summary>目前停在那一處命中的底色：<b>實色</b>強調色。</summary>
    /// <remarks>
    /// 與一般命中的差別刻意做成「半透明 vs 實色」而不是兩種深淺的同一個顏色：深淺差一階的
    /// 那一版在 150% DPI 的深色主題上分不出來，而分不出來等於沒有「目前」這個概念。
    /// 實色會蓋掉底下的著色，所以字色要另外對著它重算。
    /// </remarks>
    public static Color CurrentMatchBackground(Color accent, Color background) =>
        EnsureGraphicContrast(Color.FromRgb(accent.R, accent.G, accent.B), background);

    private static Color EnsureMinimumContrast(Color candidate, Color background, double minimum)
    {
        candidate = Composite(candidate, background);
        var target = Contrast(Colors.Black, background) >= Contrast(Colors.White, background) ? Colors.Black : Colors.White;
        for (var step = 0; step <= 20; step++)
        {
            var color = Composite(Color.FromArgb((byte)Math.Round(255 * step / 20.0), target.R, target.G, target.B), candidate);
            if (Contrast(color, background) >= minimum) return color;
        }
        return target;
    }

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
