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

        var dimForeground = highContrast || !Readable(dim, Colors.Transparent) ? foreground : dim;
        // 命中自成一組色票，不借 AccentBackground：那一份同時是開關「開著」與核取方塊打勾的底，
        // 共用的話一邊為了對比調整、另一邊跟著變；而且它是 Tint(0.12)，對比不足時還會把 alpha
        // 逐次折半，退到幾乎看不見——「有沒有標出來」正是使用者唯一要從命中讀到的事。
        // 高對比沒有半透明可用，兩級改用「反白」與「系統選取色」兩種實色，一樣分得出來。
        var match = highContrast ? foreground : ThemeColorMath.MatchBackground(accent, dimForeground, background);
        var matchCurrent = highContrast
            ? selection.Background
            : ThemeColorMath.CurrentMatchBackground(accent, background);
        var colors = new Dictionary<ThemeBrush, Color>
        {
            [ThemeBrush.ListBackground] = background,
            [ThemeBrush.ListForeground] = foreground,
            [ThemeBrush.WindowBackground] = window.Background,
            [ThemeBrush.WindowForeground] = windowForeground,
            [ThemeBrush.DimForeground] = dimForeground,
            [ThemeBrush.Border] = highContrast ? foreground : border,
            [ThemeBrush.Hairline] = highContrast ? foreground : Overlay(foreground, 0.10),
            [ThemeBrush.RowHover] = highContrast ? selection.Background : Tint(0.05),
            [ThemeBrush.RowSelected] = highContrast ? selection.Background : Tint(0.12),
            [ThemeBrush.SelectedForeground] = highContrast ? selection.Foreground : foreground,
            [ThemeBrush.RowPressed] = highContrast ? selection.Background : Tint(0.18),
            [ThemeBrush.RowAlternate] = highContrast ? background : Overlay(foreground, 0.045),
            [ThemeBrush.SegmentTrack] = highContrast ? background : Overlay(foreground, 0.06),
            // 覆蓋式捲軸平時退到淡色；透明度放在色彩裡，高對比才能回到實色而不必另寫觸發器。
            [ThemeBrush.ScrollThumb] = highContrast ? foreground : Overlay(dimForeground, 0.65),
            [ThemeBrush.BadgeBackground] = highContrast ? background : badge,
            [ThemeBrush.AccentBackground] = highContrast ? background : Tint(0.12),
            [ThemeBrush.AccentBorder] = highContrast ? foreground : accent,
            [ThemeBrush.MatchBackground] = match,
            // 清單列沒有語法著色可留，所以這一級也配一個前景；只換底色而讓字色留在原地的那一版，
            // 底色一深就讀不到，而字重撐不住——SemiBold 在介面字型上差得太少。
            [ThemeBrush.MatchForeground] = highContrast ? background : ThemeColorMath.EnsureTextContrast(foreground, match),
            [ThemeBrush.MatchCurrentBackground] = matchCurrent,
            [ThemeBrush.MatchCurrentForeground] = highContrast
                ? selection.Foreground
                : ThemeColorMath.EnsureTextContrast(foreground, matchCurrent),
            // 狀態只染圖形；文字仍沿用可讀的主題前景，高對比則由形狀辨識。
            [ThemeBrush.NotificationSuccess] = highContrast ? foreground : ThemeColorMath.EnsureGraphicContrast(Added, background),
            [ThemeBrush.NotificationFailure] = highContrast ? foreground : ThemeColorMath.EnsureGraphicContrast(Danger, background),
            [ThemeBrush.NotificationRunning] = highContrast ? foreground : ThemeColorMath.EnsureGraphicContrast(accent, background),
            [ThemeBrush.NotificationRunningEnd] = highContrast ? foreground : ThemeColorMath.EnsureGraphicContrast(
                ThemeColorMath.Composite(Overlay(foreground, 0.25), accent), background),
            // 量表只是圖形，分級另有文字與百分比；高對比全用前景色，靠長度與文字辨識。
            [ThemeBrush.MeterNormal] = highContrast ? foreground : ThemeColorMath.EnsureGraphicContrast(accent, background),
            [ThemeBrush.MeterWarning] = highContrast ? foreground : ThemeColorMath.EnsureGraphicContrast(Favorite, background),
            [ThemeBrush.MeterCritical] = highContrast ? foreground : ThemeColorMath.EnsureGraphicContrast(Danger, background)
        };
        // 語意色只在停駐與按下時出現；靜止仍是中性，整排按鈕才不會變成一串彩色標籤。
        Tone(ThemeBrush.DangerBackground, ThemeBrush.DangerPressed, ThemeBrush.DangerForeground, Danger);
        Tone(ThemeBrush.FavoriteBackground, ThemeBrush.FavoritePressed, ThemeBrush.FavoriteForeground, Favorite);
        DiffTone(ThemeBrush.DiffAddedBackground, ThemeBrush.DiffAddedForeground, Added);
        DiffTone(ThemeBrush.DiffRemovedBackground, ThemeBrush.DiffRemovedForeground, Danger);

        // 差異列的底色鋪滿整行，SQL 文字直接疊在上面：一般前景必須讀得到，不夠就減淡而不換色相。
        // 標記與行號另用同色相的深／淺色，並在兩種表面上都過 4.5:1。高對比不上色，只靠 +／- 標記辨識。
        void DiffTone(ThemeBrush tint, ThemeBrush text, Color seed)
        {
            if (highContrast)
            {
                colors[tint] = background;
                colors[text] = foreground;
                return;
            }

            var overlay = Overlay(seed, 0.16);
            while (overlay.A > 0 && !Readable(foreground, overlay))
                overlay = Color.FromArgb((byte)(overlay.A / 2), overlay.R, overlay.G, overlay.B);
            colors[tint] = overlay;
            var paired = seed;
            foreach (var surface in new[] { background, window.Background })
                paired = ThemeColorMath.EnsureTextContrast(paired, ThemeColorMath.Composite(overlay, surface));
            colors[text] = paired;
        }

        void Tone(ThemeBrush hover, ThemeBrush pressed, ThemeBrush text, Color seed)
        {
            if (highContrast)
            {
                // 高對比沿用系統選取色與配對文字；半透明色票在那裡既不合規也分不出語意。
                colors[hover] = colors[pressed] = selection.Background;
                colors[text] = selection.Foreground;
                return;
            }

            var tint = Overlay(seed, 0.16);
            var down = Overlay(seed, 0.26);
            colors[hover] = tint;
            colors[pressed] = down;
            // 同一顆按鈕會落在內容與視窗兩種底色上，按下時色調又更濃；四種組合都要讓文字過 4.5:1。
            // 同一族底色的校正方向一致，逐一收緊只會愈來愈保守，不會把前一個表面推回不合格。
            var paired = seed;
            foreach (var surface in new[] { background, window.Background })
                foreach (var overlay in new[] { tint, down })
                    paired = ThemeColorMath.EnsureTextContrast(paired, ThemeColorMath.Composite(overlay, surface));
            colors[text] = paired;
        }

        foreach (var pair in BlockPalette.Create(background, foreground, accent, null, highContrast))
            colors[pair.Key] = pair.Value;
        return colors;
    }

    /// <summary>破壞性操作的種子色；與通知的失敗色同源，兩處不各自維護一份紅。</summary>
    private static readonly Color Danger = Color.FromRgb(225, 77, 95);

    /// <summary>新增行的種子色；與通知的成功色同源。</summary>
    private static readonly Color Added = Color.FromRgb(38, 166, 112);

    /// <summary>收藏的種子色；星號圖示的暖金黃，不借用主題強調色以免與焦點混淆。</summary>
    private static readonly Color Favorite = Color.FromRgb(220, 160, 20);

    private static Color Overlay(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B);
}
