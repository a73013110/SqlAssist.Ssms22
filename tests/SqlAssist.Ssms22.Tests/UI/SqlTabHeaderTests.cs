using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlTabHeaderTests
{
    [Fact]
    public void 總數是淡色數字而命中數借命中色票()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));
            var header = new SqlTabHeader("欄位");
            header.Resources.MergedDictionaries.Add(palette.Resources);
            var chip = (Border)header.Children[1];
            var count = (TextBlock)chip.Child;

            Assert.Equal(Visibility.Collapsed, chip.Visibility);

            header.ShowTotal(23);
            Assert.Equal(Visibility.Visible, chip.Visibility);
            Assert.Equal("23", count.Text);
            Assert.Null(chip.Background);
            Assert.Same(palette.Resources[ThemeBrush.DimForeground], count.Foreground);
            Assert.Equal("欄位，23 項", AutomationProperties.GetName(header));

            header.ShowHits(3);
            Assert.True(header.IsHitCount);
            Assert.Same(palette.Resources[ThemeBrush.MatchBackground], chip.Background);
            Assert.Same(palette.Resources[ThemeBrush.MatchForeground], count.Foreground);
            Assert.Equal("欄位，3 個符合", AutomationProperties.GetName(header));

            // 零也照寫，但不是一顆亮起來的膠囊：那一頁沒有東西可看。
            header.ShowHits(0);
            Assert.Equal("0", count.Text);
            Assert.Null(chip.Background);

            header.ShowTotal(null);
            Assert.Equal(Visibility.Collapsed, chip.Visibility);
            Assert.Equal("欄位", AutomationProperties.GetName(header));
        });
    }
}
