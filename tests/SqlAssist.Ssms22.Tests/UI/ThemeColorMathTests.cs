using System.Windows.Media;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class ThemeColorMathTests
{
    [Fact]
    public void BlackAndWhiteHaveMaximumContrast()
    {
        Assert.Equal(21, ThemeColorMath.Contrast(Colors.Black, Colors.White), 8);
        Assert.Equal(1, ThemeColorMath.Contrast(Colors.Black, Colors.Black), 8);
    }

    [Fact]
    public void DimTextFallsBackWhenItCannotBeRead()
    {
        Assert.Equal(Colors.Black, ThemeColorMath.EnsureContrast(Colors.LightGray, Colors.White, Colors.Black));
        Assert.Equal(Colors.White, ThemeColorMath.EnsureContrast(Colors.DarkSlateGray, Colors.Black, Colors.White));
        Assert.Equal(Colors.DarkBlue, ThemeColorMath.EnsureContrast(Colors.DarkBlue, Colors.White, Colors.Black));
    }

    [Fact]
    public void TransparentColorsAreCompositedBeforeCheckingContrast()
    {
        Assert.Equal(1, ThemeColorMath.Contrast(Color.FromArgb(0, 0, 0, 0), Colors.White), 8);
    }
}
