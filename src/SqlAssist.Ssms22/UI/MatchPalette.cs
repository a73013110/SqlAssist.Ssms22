using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 搜尋命中的整組顏色：一般命中與「目前停在的那一處」各一對底色與字色。
/// </summary>
/// <remarks>
/// 顯示命中的表面各自給自己的底色當基準（工具窗是清單底色，指令碼借的是 SSMS 編輯器底色，
/// 兩者在深色主題下不一定同深淺），推導只有這一份。各寫一份的下場是同一個搜尋字在同一個
/// 視窗的兩塊看起來不是同一件事。
///
/// <b>兩級都蓋掉底下的著色。</b>把一般命中做成「淡到讓語法著色仍讀得出來」的那一版，等於要求
/// 底色與最淡的那個分類色維持 4.5:1，而校正只能把底色往表面推——推完的結果就是一層幾乎看不見
/// 的薄色，使用者得瞇著眼找命中在哪裡。留住著色與一眼看得出來互斥，而「有沒有標出來」才是
/// 使用者要從這裡讀到的事；分類色在命中那幾個字上讀不到並不影響判讀，它們的周圍仍然有色。
///
/// 兩級的差別不靠「深淺差一階」——那在 150% DPI 的深色主題上分不出來，而分不出來等於沒有
/// 「目前」這個概念。這裡改成由 <see cref="Separation"/> 保證的對比差距，往表面退到差距夠了
/// 才停；呈現端再加一級字重，狀態就不是只靠顏色表達。
/// </remarks>
internal readonly struct MatchPalette
{
    /// <summary>兩級之間、以及一般命中與表面之間都要維持的最小對比。</summary>
    /// <remarks>
    /// 目前那一處對表面是 4.5，中間才塞得下兩段 <see cref="Separation"/>；把它放大到 2.5 以上
    /// 的那一版在某些主題上找不到落點，一般命中會退回與目前那一處同色。
    /// </remarks>
    private const double Separation = 1.8;

    /// <summary>退向表面的搜尋步數；只在配色變更時跑，不進捲動或游標路徑。</summary>
    private const int Steps = 20;

    private MatchPalette(Color background, Color foreground, Color currentBackground, Color currentForeground)
    {
        Background = background;
        Foreground = foreground;
        CurrentBackground = currentBackground;
        CurrentForeground = currentForeground;
    }

    /// <summary>一般命中的底色。</summary>
    public Color Background { get; }

    /// <summary>一般命中的字色。</summary>
    public Color Foreground { get; }

    /// <summary>目前停在那一處的底色；整片指令碼裡要一眼跳出來。</summary>
    public Color CurrentBackground { get; }

    /// <summary>目前停在那一處的字色。</summary>
    public Color CurrentForeground { get; }

    /// <param name="accent">主題強調色。</param>
    /// <param name="surface">這個表面自己的底色。</param>
    /// <param name="foreground">這個表面自己的前景色；字色由它往黑或白推到對比過關。</param>
    /// <param name="selection">高對比下的系統選取色與配對文字。</param>
    public static MatchPalette Create(
        Color accent, Color surface, Color foreground, bool highContrast,
        (Color Background, Color Foreground) selection)
    {
        // 高對比沒有半透明也沒有中間色可用：一般命中用反白，目前那一處用系統選取色。
        // 兩者都是實色、都合規，而且差的是色相不是亮度——亮度在那裡本來就只有兩級。
        if (highContrast) return new MatchPalette(foreground, surface, selection.Background, selection.Foreground);

        // 目前那一處：飽和強調色，對表面至少 4.5:1。校正只降飽和不換色相，
        // 強調色在某些佈景上本來就接近表面底色，不校正的那一版整塊看不見。
        var current = ThemeColorMath.EnsureTextContrast(Color.FromRgb(accent.R, accent.G, accent.B), surface);
        var match = Retreat(current, surface);
        return new MatchPalette(
            match,
            ThemeColorMath.EnsureTextContrast(foreground, match),
            current,
            ThemeColorMath.EnsureTextContrast(foreground, current));
    }

    /// <summary>從目前那一處的底色往表面退，退到兩級分得開、而它自己又還看得出邊界為止。</summary>
    private static Color Retreat(Color current, Color surface)
    {
        var match = current;

        for (var step = 1; step <= Steps; step++)
        {
            var candidate = ThemeColorMath.Composite(
                Color.FromArgb((byte)(255 * step / Steps), surface.R, surface.G, surface.B), current);
            // 退過頭就停在上一個：退到與表面分不開等於這一處根本沒有標記。
            if (ThemeColorMath.Contrast(candidate, surface) < Separation) break;
            match = candidate;
            if (ThemeColorMath.Contrast(current, candidate) >= Separation) break;
        }

        return match;
    }
}
