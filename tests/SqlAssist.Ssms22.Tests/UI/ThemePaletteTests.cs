using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class ThemePaletteTests
{
    [Theory]
    [InlineData("mango", 0x101010)]
    [InlineData("cool-breeze", 0x303F4F)]
    [InlineData("plum", 0xFFFFFF)]
    [InlineData("forest", 0xDDEEFF)]
    public void 自訂編輯器底色與殼層不同時仍可辨認色帶(string mode, int backgroundRgb)
    {
        var background = Color.FromRgb((byte)(backgroundRgb >> 16), (byte)(backgroundRgb >> 8), (byte)backgroundRgb);
        var palette = ColorsFor(mode);
        var roles = new[] { ThemeBrush.Block, ThemeBrush.BlockTry, ThemeBrush.BlockCatch,
            ThemeBrush.BlockCase, ThemeBrush.BlockParenthesis, ThemeBrush.BlockBracket };
        var colors = roles.Select(role => ThemeColorMath.EnsureGraphicContrast(palette[role], background)).ToArray();
        Assert.Single(colors.Distinct());
        foreach (var color in colors)
        {
            Assert.True(ThemeColorMath.Contrast(color, background) >= 3);
            Assert.Equal(color, ThemeColorMath.EnsureGraphicContrast(color, background));
        }
    }

    [Fact]
    public void 色帶與底色相同也能恢復對比()
    {
        Assert.True(ThemeColorMath.Contrast(ThemeColorMath.EnsureGraphicContrast(Colors.Black, Colors.Black), Colors.Black) >= 3);
        Assert.True(ThemeColorMath.Contrast(ThemeColorMath.EnsureGraphicContrast(Colors.White, Colors.White), Colors.White) >= 3);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("mango")]
    [InlineData("cool-breeze")]
    [InlineData("dark")]
    [InlineData("plum")]
    [InlineData("forest")]
    public void 區塊種類共用基準色且色帶維持圖形對比(string mode)
    {
        var colors = ColorsFor(mode);
        var roles = new[] { ThemeBrush.Block, ThemeBrush.BlockTry, ThemeBrush.BlockCatch,
            ThemeBrush.BlockCase, ThemeBrush.BlockParenthesis, ThemeBrush.BlockBracket };
        Assert.Single(roles.Select(role => colors[role]).Distinct());
        foreach (var role in roles)
            Assert.True(ThemeColorMath.Contrast(colors[role], colors[ThemeBrush.ListBackground]) >= 3);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("mango")]
    [InlineData("cool-breeze")]
    [InlineData("dark")]
    [InlineData("plum")]
    [InlineData("forest")]
    [InlineData("high-contrast")]
    public void EveryRoleIsPublishedAndTextRemainsReadable(string mode)
    {
        var colors = ColorsFor(mode);
        Assert.Equal(Enum.GetValues(typeof(ThemeBrush)).Length, colors.Count);
        foreach (var background in new[] { colors[ThemeBrush.ListBackground], colors[ThemeBrush.WindowBackground] })
        {
            Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.DimForeground], background) >= 4.5);
            Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.ListForeground], background) >= 4.5);
            foreach (var role in new[] { ThemeBrush.RowHover, ThemeBrush.RowSelected, ThemeBrush.RowPressed })
            {
                Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.SelectedForeground],
                    ThemeColorMath.Composite(colors[role], background)) >= 4.5);
            }
        }
    }

    [Theory]
    [InlineData("light")]
    [InlineData("mango")]
    [InlineData("cool-breeze")]
    [InlineData("dark")]
    [InlineData("plum")]
    [InlineData("forest")]
    [InlineData("high-contrast")]
    public void 命中兩級分得開且各自配得出讀得到的前景(string mode)
    {
        var colors = ColorsFor(mode);
        var background = colors[ThemeBrush.ListBackground];

        var match = ThemeColorMath.Composite(colors[ThemeBrush.MatchBackground], background);
        var current = ThemeColorMath.Composite(colors[ThemeBrush.MatchCurrentBackground], background);

        // 兩級都要配得出讀得到的字色；只換底色的那一版在深色主題上字會沉進去。
        Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.MatchForeground], match) >= 4.5);
        Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.MatchCurrentForeground], current) >= 4.5);

        // 門檻與推導的規範值在 MatchPaletteTests；這裡只驗工具窗這一份確實照它填出來。
        Assert.True(ThemeColorMath.Contrast(current, background) >= TextMarkColors.MinimumSeparation);
        Assert.True(ThemeColorMath.Contrast(match, background) >= TextMarkColors.MinimumSeparation);

        // 兩級必須是兩個看得出差別的東西。非高對比的差別是同一個色相的半透明與實色，讀的是
        // 亮度；高對比換成兩個系統色，白與黃的亮度本來就接近，分得出來靠的是色相——對比比值
        // 在那裡判不出來，所以只要求兩者不同。
        Assert.NotEqual(match, current);
        if (mode != "high-contrast") Assert.True(ThemeColorMath.Contrast(current, match) >= 1.25);

        // 命中不借強調底：那一份同時是開關「開著」與核取方塊打勾的底。
        Assert.NotEqual(colors[ThemeBrush.AccentBackground], colors[ThemeBrush.MatchBackground]);
        Assert.NotEqual(colors[ThemeBrush.RowSelected], colors[ThemeBrush.MatchBackground]);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("mango")]
    [InlineData("cool-breeze")]
    [InlineData("dark")]
    [InlineData("plum")]
    [InlineData("forest")]
    public void 語意色調在兩種表面都保留可讀文字且與中性回饋分得開(string mode)
    {
        var colors = ColorsFor(mode);
        foreach (var (hover, pressed, text) in new[]
                 {
                     (ThemeBrush.DangerBackground, ThemeBrush.DangerPressed, ThemeBrush.DangerForeground),
                     (ThemeBrush.FavoriteBackground, ThemeBrush.FavoritePressed, ThemeBrush.FavoriteForeground)
                 })
        {
            // 語意色只在停駐時出現，但仍要在內容與視窗兩種底色上讀得到配對文字。
            foreach (var background in new[] { colors[ThemeBrush.ListBackground], colors[ThemeBrush.WindowBackground] })
                foreach (var role in new[] { hover, pressed })
                    Assert.True(ThemeColorMath.Contrast(colors[text], ThemeColorMath.Composite(colors[role], background)) >= 4.5);
            Assert.True(colors[pressed].A > colors[hover].A);
            Assert.NotEqual(colors[ThemeBrush.RowSelected], colors[hover]);
        }

        Assert.NotEqual(colors[ThemeBrush.DangerBackground], colors[ThemeBrush.FavoriteBackground]);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("mango")]
    [InlineData("cool-breeze")]
    [InlineData("dark")]
    [InlineData("plum")]
    [InlineData("forest")]
    public void 差異的新增與刪除底色讓SQL與標記都可讀且彼此分得開(string mode)
    {
        var colors = ColorsFor(mode);
        foreach (var (tint, text) in new[]
                 {
                     (ThemeBrush.DiffAddedBackground, ThemeBrush.DiffAddedForeground),
                     (ThemeBrush.DiffRemovedBackground, ThemeBrush.DiffRemovedForeground)
                 })
        {
            Assert.True(colors[tint].A > 0);
            foreach (var surface in new[] { colors[ThemeBrush.ListBackground], colors[ThemeBrush.WindowBackground] })
            {
                var line = ThemeColorMath.Composite(colors[tint], surface);
                // SQL 原文直接疊在差異底色上；標記與行號用同色相的配對前景。
                Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.ListForeground], line) >= 4.5);
                Assert.True(ThemeColorMath.Contrast(colors[text], line) >= 4.5);
            }
        }

        Assert.NotEqual(colors[ThemeBrush.DiffAddedBackground], colors[ThemeBrush.DiffRemovedBackground]);
        Assert.NotEqual(colors[ThemeBrush.DiffAddedForeground], colors[ThemeBrush.DiffRemovedForeground]);
    }

    [Fact]
    public void 高對比的差異不上底色只靠標記辨識()
    {
        var colors = ColorsFor("high-contrast");
        Assert.Equal(colors[ThemeBrush.ListBackground], colors[ThemeBrush.DiffAddedBackground]);
        Assert.Equal(colors[ThemeBrush.ListBackground], colors[ThemeBrush.DiffRemovedBackground]);
        Assert.Equal(colors[ThemeBrush.ListForeground], colors[ThemeBrush.DiffAddedForeground]);
        Assert.Equal(colors[ThemeBrush.ListForeground], colors[ThemeBrush.DiffRemovedForeground]);
    }

    [Fact]
    public void 高對比的語意色調回到系統選取色()
    {
        var colors = ColorsFor("high-contrast");
        foreach (var role in new[] { ThemeBrush.DangerBackground, ThemeBrush.DangerPressed,
                     ThemeBrush.FavoriteBackground, ThemeBrush.FavoritePressed })
        {
            Assert.Equal(colors[ThemeBrush.RowSelected], colors[role]);
        }

        foreach (var role in new[] { ThemeBrush.DangerForeground, ThemeBrush.FavoriteForeground })
            Assert.Equal(colors[ThemeBrush.SelectedForeground], colors[role]);
    }

    [Theory]
    [InlineData("mango", "cool-breeze")]
    [InlineData("plum", "forest")]
    public void SwitchingHueWithinTheSameBrightnessChangesSurfacesAndAccents(string first, string second)
    {
        var before = ColorsFor(first);
        var after = ColorsFor(second);
        foreach (var role in new[] { ThemeBrush.WindowBackground, ThemeBrush.ListBackground,
                     ThemeBrush.AccentBorder, ThemeBrush.AccentBackground, ThemeBrush.RowSelected })
        {
            Assert.NotEqual(before[role], after[role]);
        }

        Assert.Equal(before[ThemeBrush.ListForeground].A, after[ThemeBrush.ListForeground].A);
    }

    [Fact]
    public void ShellSurfacesAreNotReplacedWithNeutralOrTitleBarColors()
    {
        var colors = ColorsFor("mango");
        Assert.Equal(Rgb(0xFDFAF6), colors[ThemeBrush.WindowBackground]);
        Assert.Equal(Rgb(0xFFFDFC), colors[ThemeBrush.ListBackground]);
        Assert.Equal(Rgb(0x693D0F), colors[ThemeBrush.AccentBorder]);
        var tint = colors[ThemeBrush.AccentBackground];
        Assert.Equal(Color.FromArgb(31, 0x69, 0x3D, 0x0F), tint);
    }

    [Fact]
    public void LowContrastAccentIsFadedWithoutLosingItsHue()
    {
        var surface = (Colors.White, Rgb(0x646464));
        var colors = ThemePalette.Create(surface, surface, Colors.Black, Colors.Black,
            Rgb(0x301020), false, (Colors.Yellow, Colors.Black));
        var pressed = colors[ThemeBrush.RowPressed];
        Assert.True(pressed.A < 46);
        Assert.Equal((byte)0x30, pressed.R);
        Assert.Equal((byte)0x10, pressed.G);
        Assert.Equal((byte)0x20, pressed.B);
        Assert.True(ThemeColorMath.Contrast(surface.Item2,
            ThemeColorMath.Composite(pressed, surface.Item1)) >= 4.5);
    }

    [Fact]
    public void FluentAlphaIsCompositedForTextAndPreservedForAccent()
    {
        var text = Color.FromArgb(228, 0, 0, 0);
        var surface = (Colors.White, text);
        var colors = ThemePalette.Create(surface, surface, Colors.Black, Colors.Black,
            Color.FromArgb(128, 0x69, 0x3D, 0x0F), false, (Colors.Yellow, Colors.Black));
        Assert.Equal(ThemeColorMath.Composite(text, Colors.White), colors[ThemeBrush.ListForeground]);
        Assert.Equal((byte)15, colors[ThemeBrush.AccentBackground].A);
    }

    [Fact]
    public void DimTextMustBeReadableOnBothWindowAndContentSurfaces()
    {
        var colors = ThemePalette.Create((Rgb(0xBBBBBB), Colors.Black), (Colors.White, Colors.Black),
            Rgb(0x767676), Colors.Black, Colors.DarkBlue, false, (Colors.Yellow, Colors.Black));
        Assert.Equal(Colors.Black, colors[ThemeBrush.DimForeground]);
    }

    [Fact]
    public void HighContrastKeepsFullSelectionPairAndSolidSurfaces()
    {
        var colors = ColorsFor("high-contrast");
        Assert.Equal(Colors.Yellow, colors[ThemeBrush.RowSelected]);
        Assert.Equal(Colors.Yellow, colors[ThemeBrush.RowHover]);
        Assert.Equal(Colors.Yellow, colors[ThemeBrush.RowPressed]);
        Assert.Equal(Colors.Black, colors[ThemeBrush.SelectedForeground]);
        Assert.Equal(Colors.Black, colors[ThemeBrush.AccentBackground]);
        Assert.Equal(Colors.Black, colors[ThemeBrush.RowAlternate]);
        Assert.Equal(colors[ThemeBrush.ListForeground], colors[ThemeBrush.ScrollThumb]);
        Assert.True(ColorsFor("dark")[ThemeBrush.ScrollThumb].A < ColorsFor("dark")[ThemeBrush.DimForeground].A);
        Assert.Equal((byte)0, colors[ThemeBrush.BlockRange].A);
        foreach (var color in colors.Where(pair => pair.Key != ThemeBrush.BlockRange).Select(pair => pair.Value))
        {
            Assert.Equal((byte)255, color.A);
        }
    }

    internal static IReadOnlyDictionary<ThemeBrush, Color> ColorsFor(string mode)
    {
        // 彩色表面與強調色取自 SSMS 22 的公開 Shell tokens；不依賴本機主題或正在執行的 SSMS。
        var (window, list, accent, dark) = mode switch
        {
            "mango" => (0xFDFAF6, 0xFFFDFC, 0x693D0F, false),
            "cool-breeze" => (0xF7FAFC, 0xFCFDFF, 0x1E394A, false),
            "plum" => (0x27242B, 0x323134, 0xD3C1EC, true),
            "forest" => (0x242B26, 0x313432, 0x8FDB9F, true),
            "dark" => (0x282828, 0x2C2C2C, 0x9184EE, true),
            "high-contrast" => (0x000000, 0x000000, 0xFFFFFF, true),
            _ => (0xF9F9F9, 0xFFFFFF, 0x5649B0, false)
        };
        var primary = dark ? Colors.White : Color.FromArgb(228, 0, 0, 0);
        var secondary = dark ? Color.FromArgb(204, 255, 255, 255) : Color.FromArgb(178, 0, 0, 0);
        return ThemePalette.Create((Rgb(window), primary), (Rgb(list), primary),
            secondary, primary, Rgb(accent), mode == "high-contrast", (Colors.Yellow, Colors.Black));
    }

    private static Color Rgb(int value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
