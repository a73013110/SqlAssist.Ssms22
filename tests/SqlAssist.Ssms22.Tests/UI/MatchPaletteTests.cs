using System.Windows.Media;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public class MatchPaletteTests
{
    private static readonly (Color Background, Color Foreground) Selection = (Colors.Yellow, Colors.Black);

    // 工具窗的清單底色與指令碼借來的編輯器底色都要走得過；兩者在深色主題下不同深淺，
    // 而命中高亮的基準正是各表面自己給的那一個。最後三組是刻意挑的極端：中灰佈景、
    // 純黑上的飽和色、幾乎沒有色度的強調色。
    [Theory]
    [InlineData(0x5649B0, 0xFFFFFF, 0x000000)]
    [InlineData(0x693D0F, 0xFFFDFC, 0x000000)]
    [InlineData(0x1E394A, 0xFCFDFF, 0x000000)]
    [InlineData(0xD3C1EC, 0x323134, 0xFFFFFF)]
    [InlineData(0x8FDB9F, 0x313432, 0xFFFFFF)]
    [InlineData(0x9184EE, 0x2C2C2C, 0xFFFFFF)]
    [InlineData(0x9184EE, 0x1E1E1E, 0xD4D4D4)]
    [InlineData(0x5649B0, 0xF5F5F5, 0x1F1F1F)]
    [InlineData(0x8FDB9F, 0x808080, 0xFFFFFF)]
    [InlineData(0xFF0000, 0x000000, 0xFFFFFF)]
    [InlineData(0x101010, 0xFFFFFF, 0x000000)]
    public void 兩級落在同一側標得出來也分得開而且字色讀得到(int accent, int surface, int foreground)
    {
        var palette = MatchPalette.Create(Rgb(accent), Rgb(surface), Rgb(foreground), highContrast: false, Selection);

        // 同側：兩級都落在深的那一邊。分居兩側的那一版在深色佈景上會讓「目前那一處」變成
        // 亮底深字，而它旁邊較弱的那一級卻是暗底淺字，看起來比它還強。
        Assert.True(ThemeColorMath.Luminance(palette.Background) <= TextMarkColors.MaximumLuminance + 0.01);
        Assert.True(ThemeColorMath.Luminance(palette.CurrentBackground) <= TextMarkColors.MaximumLuminance + 0.01);

        // 標得出來：兩級都要與所在表面分得開，否則等於沒有標記。
        Assert.True(ThemeColorMath.Contrast(palette.Background, Rgb(surface)) >= TextMarkColors.MinimumSeparation);
        Assert.True(ThemeColorMath.Contrast(palette.CurrentBackground, Rgb(surface)) >= TextMarkColors.MinimumSeparation);

        // 兩級之間：亮度上就看得出深淺，色度差與字重另外再補一層。
        Assert.True(ThemeColorMath.Contrast(palette.CurrentBackground, palette.Background) >= 1.25);

        // 字色走這一側能到的最純的那一端，4.5 只是下限；停在剛好及格的那一版是中灰。
        Assert.True(ThemeColorMath.Contrast(palette.Foreground, palette.Background) >= 4.5);
        Assert.True(ThemeColorMath.Contrast(palette.CurrentForeground, palette.CurrentBackground) >= 4.5);
    }

    [Fact]
    public void 高對比改用系統選取色仍然分得出兩級()
    {
        var surface = Colors.Black;
        var foreground = Colors.White;
        var palette = MatchPalette.Create(foreground, surface, foreground, highContrast: true, Selection);

        // 高對比沒有中間色可調，兩級差的是色相不是亮度；對比比值在那裡判不出來，只要求不同。
        Assert.NotEqual(palette.Background, palette.CurrentBackground);
        Assert.True(ThemeColorMath.Contrast(palette.Foreground, palette.Background) >= 4.5);
        Assert.True(ThemeColorMath.Contrast(palette.CurrentForeground, palette.CurrentBackground) >= 4.5);
    }

    private static Color Rgb(int value) =>
        Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
