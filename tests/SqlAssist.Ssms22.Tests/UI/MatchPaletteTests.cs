using System.Windows.Media;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public class MatchPaletteTests
{
    /// <summary>規範門檻：兩級之間、以及一般命中與表面之間都要守住的最小對比。</summary>
    private const double Separation = 1.8;

    private static readonly (Color Background, Color Foreground) Selection = (Colors.Yellow, Colors.Black);

    // 工具窗的清單底色與指令碼借來的編輯器底色都要走得過；兩者在深色主題下不同深淺，
    // 而命中高亮的基準正是各表面自己給的那一個。
    [Theory]
    [InlineData(0x5649B0, 0xFFFFFF, 0x000000)]
    [InlineData(0x693D0F, 0xFFFDFC, 0x000000)]
    [InlineData(0x1E394A, 0xFCFDFF, 0x000000)]
    [InlineData(0xD3C1EC, 0x323134, 0xFFFFFF)]
    [InlineData(0x8FDB9F, 0x313432, 0xFFFFFF)]
    [InlineData(0x9184EE, 0x2C2C2C, 0xFFFFFF)]
    [InlineData(0x9184EE, 0x1E1E1E, 0xD4D4D4)]
    [InlineData(0x5649B0, 0xF5F5F5, 0x1F1F1F)]
    public void 兩級都標得出來也分得開而且各自配得出讀得到的字色(int accent, int surface, int foreground)
    {
        var palette = MatchPalette.Create(Rgb(accent), Rgb(surface), Rgb(foreground), highContrast: false, Selection);

        // 目前那一處要從整片指令碼裡一眼跳出來，所以對表面守的是文字級的 4.5。
        Assert.True(ThemeColorMath.Contrast(palette.CurrentBackground, Rgb(surface)) >= 4.5);
        // 一般命中可以輕一點，但輕到與表面分不開就等於沒有標記——那正是「只有粗體」的那一版。
        Assert.True(ThemeColorMath.Contrast(palette.Background, Rgb(surface)) >= Separation);
        // 兩級必須看得出差別，否則按了「下一個」還是要自己找跳到哪裡。
        Assert.True(ThemeColorMath.Contrast(palette.CurrentBackground, palette.Background) >= Separation);

        Assert.True(ThemeColorMath.Contrast(palette.Foreground, palette.Background) >= 4.5);
        Assert.True(ThemeColorMath.Contrast(palette.CurrentForeground, palette.CurrentBackground) >= 4.5);
    }

    [Fact]
    public void 高對比改用系統選取色仍然分得出兩級()
    {
        var surface = Colors.Black;
        var foreground = Colors.White;
        var palette = MatchPalette.Create(foreground, surface, foreground, highContrast: true, Selection);

        // 高對比沒有中間色可用，兩級差的是色相不是亮度；對比比值在那裡判不出來，只要求不同。
        Assert.NotEqual(palette.Background, palette.CurrentBackground);
        Assert.True(ThemeColorMath.Contrast(palette.Foreground, palette.Background) >= 4.5);
        Assert.True(ThemeColorMath.Contrast(palette.CurrentForeground, palette.CurrentBackground) >= 4.5);
    }

    private static Color Rgb(int value) =>
        Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
