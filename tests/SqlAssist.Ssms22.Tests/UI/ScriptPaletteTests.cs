using System.Linq;
using System.Windows.Media;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class ScriptPaletteTests
{
    // SSMS 深色編輯器的一組分類色；工具窗底色則取彩色深色主題（月光那一類）較亮的內容表面。
    private static readonly Color EditorBackground = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly Color EditorForeground = Color.FromRgb(0xDC, 0xDC, 0xDC);
    private static readonly Color ShellBackground = Color.FromRgb(0x2A, 0x28, 0x3A);
    private static readonly Color ShellForeground = Color.FromRgb(0xEA, 0xE8, 0xF2);

    [Fact]
    public void EditorSurfaceIsTakenAsAPair()
    {
        Assert.Equal(
            (EditorBackground, EditorForeground),
            ScriptPalette.Surface((EditorBackground, EditorForeground), (ShellBackground, ShellForeground)));
    }

    [Fact]
    public void ShellSurfaceIsKeptWhenTheEditorPairIsMissingOrUnreadable()
    {
        var shell = (ShellBackground, ShellForeground);
        Assert.Equal(shell, ScriptPalette.Surface(null, shell));
        // 半途的外觀可能給出讀不到自己的一對；那時整組退回，不混用兩邊。
        Assert.Equal(shell, ScriptPalette.Surface((EditorBackground, Color.FromRgb(0x2B, 0x2B, 0x2B)), shell));
    }

    [Fact]
    public void ReadableClassificationColoursAreLeftAlone()
    {
        var keyword = Color.FromRgb(0x56, 0x9C, 0xD6);
        Assert.Equal(keyword, ScriptPalette.Classification(keyword, EditorForeground, EditorBackground));
    }

    [Fact]
    public void MissingClassificationColourFallsBackToTheForeground()
    {
        Assert.Equal(EditorForeground, ScriptPalette.Classification(null, EditorForeground, EditorBackground));
    }

    /// <summary>
    /// 「比對佈景主題」＋彩色深色主題、且一個查詢視窗都沒開時的回歸：分類色配上工具窗底色
    /// 對比可能不足，但四種著色必須仍然彼此分得開，而不是全部塌成前景色。
    /// </summary>
    [Fact]
    public void ClassificationsStayDistinctOnASurfaceTheyWereNotTunedFor()
    {
        // 淺色配色搬到深色表面是最壞的情況：三種顏色對著深底全部低於 4.5:1。
        var candidates = new[]
        {
            Color.FromRgb(0x00, 0x00, 0xFF), // keyword
            Color.FromRgb(0x00, 0x80, 0x00), // comment
            Color.FromRgb(0xA3, 0x15, 0x15), // string
            Color.FromRgb(0x09, 0x88, 0x5A)  // number
        };

        var resolved = candidates
            .Select(color => ScriptPalette.Classification(color, ShellForeground, ShellBackground))
            .ToArray();

        foreach (var color in resolved)
        {
            Assert.True(ThemeColorMath.Contrast(color, ShellBackground) >= 4.5);
            Assert.NotEqual(ShellForeground, color);
        }

        Assert.Equal(resolved.Length, resolved.Distinct().Count());
    }

    [Fact]
    public void AdjustedColoursKeepTheirHue()
    {
        var adjusted = ScriptPalette.Classification(
            Color.FromRgb(0x00, 0x00, 0xFF), ShellForeground, ShellBackground);
        Assert.True(adjusted.B > adjusted.R);
        Assert.True(adjusted.B > adjusted.G);
    }
}
