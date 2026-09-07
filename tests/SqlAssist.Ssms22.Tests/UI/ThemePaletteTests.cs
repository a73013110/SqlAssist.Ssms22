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
