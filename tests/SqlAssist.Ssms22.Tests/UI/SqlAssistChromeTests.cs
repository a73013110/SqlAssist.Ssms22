using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlAssistChromeTests
{
    [Fact]
    public void BrandMarkUsesLiveThemeBrushesAndVectorGeometryAtMultipleDpi()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var mark = SqlAssistChrome.CreateBrandMark();
            mark.Resources.MergedDictionaries.Add(palette.Resources);
            var canvas = Assert.IsType<Canvas>(Assert.IsType<Viewbox>(mark.Child).Child);
            var database = Assert.IsType<System.Windows.Shapes.Path>(canvas.Children[0]);
            var caret = Assert.IsType<System.Windows.Shapes.Path>(canvas.Children[1]);
            var geometry = database.Data;

            foreach (var mode in new[] { "mango", "cool-breeze", "plum", "forest", "high-contrast", "light" })
            {
                var colors = ThemePaletteTests.ColorsFor(mode);
                palette.Update(colors);
                mark.Measure(new Size(48, 48));
                mark.Arrange(new Rect(0, 0, 48, 48));
                mark.UpdateLayout();

                Assert.Equal(colors[ThemeBrush.AccentBackground], Assert.IsType<SolidColorBrush>(mark.Background).Color);
                Assert.Equal(colors[ThemeBrush.ListForeground], Assert.IsType<SolidColorBrush>(database.Stroke).Color);
                Assert.Equal(colors[ThemeBrush.AccentBorder], Assert.IsType<SolidColorBrush>(caret.Stroke).Color);
                Assert.Same(geometry, database.Data);

                foreach (var dpi in new[] { 96, 144, 192 })
                {
                    var bitmap = new RenderTargetBitmap(48 * dpi / 96, 48 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(mark);
                    Assert.Equal(48 * dpi / 96, bitmap.PixelWidth);
                }
            }
        });
    }

    [Fact]
    public void TextBoxAppliesContentPaddingOnlyOnce()
    {
        WpfTest.Run(() =>
        {
            var field = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
            field.Text = "Loan";
            field.Measure(new Size(320, 100));
            field.Arrange(new Rect(0, 0, 320, field.DesiredSize.Height));
            field.UpdateLayout();

            var firstCharacter = field.GetRectFromCharacterIndex(0);
            Assert.False(firstCharacter.IsEmpty);
            Assert.InRange(firstCharacter.Left, field.Padding.Left, field.Padding.Left + 4);
            Assert.InRange(firstCharacter.Top, field.Padding.Top, field.Padding.Top + 4);
            Assert.Equal("Loan", field.Text);
        });
    }

    [Fact]
    public void MetadataKeepsFullTextAndFollowsThemeWithoutRebuilding()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var text = SqlAssistChrome.CreateMetadataText("Loan · 1,000 列", SqlAssistChrome.DefaultMetrics);
            var root = new Border { Child = text };
            root.Resources.MergedDictionaries.Add(palette.Resources);

            foreach (var mode in new[] { "mango", "cool-breeze", "plum", "forest", "high-contrast", "light" })
            {
                var colors = ThemePaletteTests.ColorsFor(mode);
                palette.Update(colors);
                root.Measure(new Size(90, 40));
                root.Arrange(new Rect(0, 0, 90, 40));
                root.UpdateLayout();

                Assert.Equal(colors[ThemeBrush.DimForeground], Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                Assert.Equal(FontWeights.Normal, text.FontWeight);
                Assert.Equal(default(Thickness), text.Margin);
                Assert.Equal(text.Text, text.ToolTip);
            }

            text.Text = "LoanDetail · 選取範圍";
            Assert.Equal(text.Text, text.ToolTip);
        });
    }

    [Fact]
    public void NumericCellsAndHeadersAlignWithoutChangingTheValue()
    {
        WpfTest.Run(() =>
        {
            var text = new TextBlock
            {
                Style = SqlAssistChrome.CreateCellTextStyle(TextAlignment.Right),
                Text = "12,345"
            };
            var header = new DataGridColumnHeader
            {
                Style = SqlAssistChrome.CreateColumnHeaderStyle(
                    SqlAssistChrome.DefaultMetrics, HorizontalAlignment.Right),
                Content = "NULL 數"
            };

            Assert.Equal(TextAlignment.Right, text.TextAlignment);
            Assert.Equal(HorizontalAlignment.Right, header.HorizontalContentAlignment);
            Assert.Equal("12,345", text.Text);
            Assert.Equal(text.Text, text.ToolTip);
            text.Text = "123,456";
            Assert.Equal(text.Text, text.ToolTip);
        });
    }

    [Fact]
    public void DefaultTextCellsKeepLeftAlignmentAndCompleteTooltip()
    {
        WpfTest.Run(() =>
        {
            var text = new TextBlock
            {
                Style = SqlAssistChrome.CreateCellTextStyle(),
                Text = "LoanDetail_" + new string('x', 200)
            };
            text.Measure(new Size(100, 30));
            text.Arrange(new Rect(0, 0, 100, 30));

            Assert.Equal(TextAlignment.Left, text.TextAlignment);
            Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
            Assert.Equal(text.Text, text.ToolTip);
        });
    }
}
