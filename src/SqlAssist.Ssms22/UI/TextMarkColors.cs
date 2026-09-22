using System;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 文字標記的底色與字色：把<b>一小段</b>文字整個染起來，要它從周圍跳出來的那一類。
/// 搜尋命中的兩級與區塊端點的高亮都走這裡。
/// </summary>
/// <remarks>
/// <b>鋪滿整行或整個區間的分類色不走這一層</b>（差異比對的加／刪、區塊範圍）。那一類要的是
/// 融入——底色淡、原本的文字顏色照舊讀得出來；這一類要的是跳出來——底色實、字色重算。
/// 兩類混成一份的下場是差異檢視變成一整片深色塊，而命中仍然淡到看不見。面積決定屬於哪一類。
///
/// 三條規則，每一條都來自實機上看得到的症狀：
///
/// <b>底色一律落在同一側</b>（深）。主題強調色的明度會<b>跟著佈景翻轉</b>——淺色佈景的
/// 強調色是深紫，深色佈景是亮紫。直接拿它當底色，配對的字色就跟著翻轉，於是同一組的兩級
/// 分居明度軸兩側：深色佈景上「目前那一處」變成亮底深字，而它旁邊較弱的那一級卻是暗底白字，
/// 看起來比它還強。
///
/// <b>字色走這一側能到的最純的那一端</b>，4.5 只是下限不是目標。一達到 4.5 就停的那一版停在
/// 中灰，而中灰配飽和彩底是所有組合裡最難看的一個。
///
/// <b>同一組的分級只動飽和與強度，不動明度側</b>。弱的那一級往灰退並壓暗一點，色相留著；
/// 退到另一側就是上面那個翻轉問題。
/// </remarks>
internal static class TextMarkColors
{
    /// <summary>底色與它所在表面至少要有的對比；低於它就等於沒有標記。</summary>
    /// <remarks>
    /// 比一般的 3:1 低，因為深色佈景上這兩件事會互相擠：表面本身就在深的那一側，而底色又不能
    /// 亮過 <see cref="MaximumLuminance"/>，中間只剩這麼窄的一段。補在色度與字重上。
    /// </remarks>
    public const double MinimumSeparation = 1.45;

    /// <summary>底色的亮度上限；再亮下去淺色字就過不了 4.5。</summary>
    public const double MaximumLuminance = 0.18;

    /// <summary>主要那一級：飽和，貼著亮度上限，讓它在整片內容裡最先被看到。</summary>
    public static MarkStrength Strong { get; } = new(MaximumLuminance, 1.0);

    /// <summary>次要那一級：同色相、更灰也更暗，仍在同一側。</summary>
    public static MarkStrength Weak { get; } = new(0.075, 0.4);

    /// <summary>一級標記的強度：落在哪一條亮度帶、色度留多少。</summary>
    public readonly struct MarkStrength
    {
        public MarkStrength(double luminance, double chroma)
        {
            Luminance = luminance;
            Chroma = chroma;
        }

        /// <summary>目標相對亮度。</summary>
        public double Luminance { get; }

        /// <summary>保留原色多少色度；1 是原樣，0 是同亮度的灰。</summary>
        public double Chroma { get; }
    }

    /// <summary>同一組的兩級之間至少要有的對比；色度與字重在這之上再補。</summary>
    public const double LevelSeparation = 1.3;

    /// <summary>
    /// 同一組標記的兩級，彼此與表面都分得開。
    /// </summary>
    /// <remarks>
    /// 表面剛好落在兩級中間時（中灰佈景），兩級各自推開表面之後會被擠到一起；把弱的那一級
    /// 再往暗推，同一個動作同時遠離強的那一級與表面。不推的那一版兩塊底色看起來一模一樣，
    /// 而「我在第幾處」就只剩字重在說。
    /// </remarks>
    public static (Color Strong, Color Weak) Pair(Color seed, Color surface)
    {
        var strong = Fill(seed, surface, Strong);
        var weak = Fill(seed, surface, Weak);

        for (var step = 0; step < 8 && ThemeColorMath.Contrast(strong, weak) < LevelSeparation; step++)
        {
            var next = ThemeColorMath.Luminance(weak) * 0.6;
            if (next < 0.005) break;
            weak = WithLuminance(weak, next);
        }

        return (strong, weak);
    }

    /// <summary>把種子色調成一塊標記底：同色相、指定的強度、與表面分得開。</summary>
    public static Color Fill(Color seed, Color surface, MarkStrength strength)
    {
        var fill = WithLuminance(Desaturate(seed, strength.Chroma), strength.Luminance);

        // 落在與表面同一條亮度帶時要推開，而方向是**遠離表面**：表面比這一塊暗就往淺推，反之
        // 往深推。往固定方向推的那一版，在深色佈景上會先穿過表面的亮度再往黑走，推完是一塊
        // 幾乎全黑、色相也沒了的方塊。往淺推守住 MaximumLuminance——字讀不到是這一層唯一
        // 不能讓的事，寧可對比停在門檻上由色度與字重補。
        var surfaceLuminance = ThemeColorMath.Luminance(surface);

        for (var step = 0; step < 8 && ThemeColorMath.Contrast(fill, surface) < MinimumSeparation; step++)
        {
            var current = ThemeColorMath.Luminance(fill);
            var next = surfaceLuminance < current
                ? Math.Min(current * 1.6, MaximumLuminance)
                : current * 0.6;
            if (Math.Abs(next - current) < 0.0001 || next < 0.005) break;
            fill = WithLuminance(fill, next);
        }

        return fill;
    }

    /// <summary>標記底上的字色：優先用這個表面原本的前景，讀不到就走到最淺的那一端。</summary>
    public static Color Ink(Color fill, Color preferred) =>
        ThemeColorMath.Contrast(preferred, fill) >= 4.5 ? preferred : Colors.White;

    /// <summary>往自己的中間灰靠，保留色相只收色度。</summary>
    private static Color Desaturate(Color color, double chroma)
    {
        if (chroma >= 1) return color;

        var middle = (Math.Max(color.R, Math.Max(color.G, color.B))
            + Math.Min(color.R, Math.Min(color.G, color.B))) / 2.0;
        return Color.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B));

        byte Mix(byte channel) => (byte)Math.Round(middle + ((channel - middle) * chroma));
    }

    /// <summary>往黑或往白混到目標亮度；二分只在配色變更時跑，不進捲動或游標路徑。</summary>
    private static Color WithLuminance(Color color, double target)
    {
        var toward = ThemeColorMath.Luminance(color) > target ? Colors.Black : Colors.White;
        var low = 0.0;
        var high = 1.0;
        var result = color;

        for (var step = 0; step < 12; step++)
        {
            var mid = (low + high) / 2;
            result = ThemeColorMath.Composite(
                Color.FromArgb((byte)Math.Round(mid * 255), toward.R, toward.G, toward.B), color);
            if (ThemeColorMath.Luminance(result) > target == (toward == Colors.Black)) low = mid;
            else high = mid;
        }

        return result;
    }
}
