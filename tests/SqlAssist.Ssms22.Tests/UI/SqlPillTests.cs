using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlPillTests
{
    [Fact]
    public void 膠囊與清單列同一個形狀且字就是自動化名稱()
    {
        WpfTest.Run(() =>
        {
            var pill = new SqlPill(SqlIcon.PrimaryKey) { Text = "CopyNo, Branch" };

            Assert.Equal(new CornerRadius(SqlAssistChrome.PillRadius), pill.CornerRadius);
            Assert.Equal("CopyNo, Branch", AutomationProperties.GetName(pill));
            var content = Assert.IsType<DockPanel>(pill.Child);
            Assert.Same(pill.Glyph, content.Children[0]);
            Assert.Equal(Dock.Left, DockPanel.GetDock(pill.Glyph!));
        });
    }

    [Fact]
    public void 強調只換底與框不換字色()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));
            var pill = new SqlPill { Text = "資料表" };
            pill.Resources.MergedDictionaries.Add(palette.Resources);
            var text = (TextBlock)((DockPanel)pill.Child).Children[0];

            Assert.Same(palette.Resources[ThemeBrush.BadgeBackground], pill.Background);
            Assert.Same(palette.Resources[ThemeBrush.Hairline], pill.BorderBrush);

            pill.Tone = SqlPillTone.Accent;
            Assert.Same(palette.Resources[ThemeBrush.AccentBackground], pill.Background);
            Assert.Same(palette.Resources[ThemeBrush.AccentBorder], pill.BorderBrush);
            Assert.Same(palette.Resources[ThemeBrush.ListForeground], text.Foreground);
        });
    }
}
