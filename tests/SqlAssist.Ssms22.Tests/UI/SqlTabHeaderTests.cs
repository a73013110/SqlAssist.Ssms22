using System.Linq;
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
            var header = new SqlTabHeader("欄位", SqlIcon.Column);
            header.Resources.MergedDictionaries.Add(palette.Resources);

            // 圖示在名稱前面；還沒有數字時不掛那一格。
            Assert.IsType<Grid>(header.Children[0]);
            Assert.Same(header.Glyph, header.Children[0]);
            Assert.Equal(2, header.Children.Count);

            header.ShowTotal(23);
            var chip = (Border)header.Children[2];
            var count = (TextBlock)chip.Child;
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

    [Fact]
    public void 分頁的前景與自動化名稱都跟著標籤走()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));
            var header = new SqlTabHeader("索引", SqlIcon.Index);
            var tabs = new TabControl { Template = SqlAssistChrome.CreateTabControlTemplate() };
            var resting = SqlAssistChrome.CreateTab("欄位", SqlIcon.Column);
            var selected = SqlAssistChrome.CreateTab(header);
            tabs.Items.Add(resting);
            tabs.Items.Add(selected);
            tabs.SelectedItem = selected;
            var host = new Border { Child = tabs };
            host.Resources.MergedDictionaries.Add(palette.Resources);
            // SSMS 宿主可能帶隱含的 TextBlock 樣式；名稱靠繼承的話會被它蓋成黑字。
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Black));
            host.Resources[typeof(TextBlock)] = textStyle;
            host.Measure(new Size(400, 200));
            host.Arrange(new Rect(0, 0, 400, 200));
            host.UpdateLayout();

            Assert.Same(palette.Resources[ThemeBrush.DimForeground], Label(resting).Foreground);
            Assert.Same(palette.Resources[ThemeBrush.ListForeground], Label(selected).Foreground);

            header.ShowTotal(4);
            Assert.Equal("索引，4 項", AutomationProperties.GetName(selected));

            header.ShowsLabel = false;
            Assert.Equal(Visibility.Collapsed, Label(selected).Visibility);
        });

        static TextBlock Label(TabItem tab) => ((SqlTabHeader)tab.Header).Children.OfType<TextBlock>().Single();
    }
}
