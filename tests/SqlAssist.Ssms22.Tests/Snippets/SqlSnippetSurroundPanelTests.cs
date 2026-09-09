using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22.Snippets;
using SqlAssist.Ssms22.Tests.UI;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Snippets;

public sealed class SqlSnippetSurroundPanelTests
{
    [Fact]
    public void 搜尋空結果不能套用且清除搜尋還原預選()
    {
        WpfTest.Run(() =>
        {
            var panel = CreatePanel();
            var preferred = panel.SelectedSnippet;
            panel.Filter("TRR");
            Assert.Equal("trr", panel.SelectedSnippet?.Shortcut);
            Assert.Contains("ROLLBACK TRANSACTION;", panel.Preview.Text);
            Assert.True(panel.ApplyButton.IsEnabled);
            panel.Filter("不存在的片段");
            Assert.Null(panel.SelectedSnippet);
            Assert.Empty(panel.Preview.Text);
            Assert.False(panel.ApplyButton.IsEnabled);
            panel.Filter("");
            Assert.Same(preferred, panel.SelectedSnippet);
            Assert.True(panel.ApplyButton.IsEnabled);
            Assert.True(panel.Preview.IsReadOnly);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(panel.SearchBox)));
        });
    }

    [Fact]
    public void 完整捷徑優先於其他欄位的命中()
    {
        WpfTest.Run(() =>
        {
            var panel = new SqlSnippetSurroundPanel(new[]
            {
                Snippet("first", "可改用 be"), Snippet("be", "區塊")
            }, 0, Selection("SELECT 1;"));
            panel.Filter("be");
            Assert.Equal(2, panel.List.Items.Count);
            Assert.Equal("be", panel.SelectedSnippet?.Shortcut);
        });
    }

    [Fact]
    public void 預覽與真正展開共用文字且不修改片段()
    {
        WpfTest.Run(() =>
        {
            const string sql = "    SELECT '$end$' AS CopyNo;\n    SELECT 2;";
            var selection = Selection(sql);
            var snippet = Snippet("be", "區塊");
            var panel = new SqlSnippetSurroundPanel(new[] { snippet }, 0, selection);
            Assert.Equal(selection.BaseIndent + snippet.WithSurroundText(selection.Text).Expansion
                .GetText("\n", selection.BaseIndent, out _), panel.Preview.Text);
            Assert.Contains("'$end$'", panel.Preview.Text);
            Assert.True(snippet.CanSurround);
            Assert.Contains("$surround$", snippet.Code);
        });
    }

    [Fact]
    public void 長SQL只限制畫面不截斷真正展開內容()
    {
        WpfTest.Run(() =>
        {
            var sql = "SELECT '" + new string('a', 20000) + "';";
            var snippet = Snippet("be", "區塊");
            var panel = new SqlSnippetSurroundPanel(new[] { snippet }, 0, Selection(sql));
            Assert.Equal(16000, panel.Preview.Text.Length);
            Assert.Contains(sql, snippet.WithSurroundText(sql).Expansion.Text);
        });
    }

    [Theory]
    [InlineData("", "all")]
    [InlineData("wl", "filtered")]
    [InlineData("找不到的片段", "empty")]
    public void 深淺高對比與多DPI的面板都能配置且即時更新筆刷(string query, string state)
    {
        WpfTest.Run(() =>
        {
            var panel = CreatePanel();
            panel.SearchBox.Text = query;
            panel.Filter(query);
            var palette = new ThemeResourceSet();
            var root = new Border { Child = panel }
                .WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
            root.Resources.MergedDictionaries.Add(palette.Resources);
            var directory = ThemeVisualTests.FindOutputDirectory();
            foreach (var mode in new[] { "light", "dark", "high-contrast", "light-again" })
            {
                var colors = ThemePaletteTests.ColorsFor(mode);
                palette.Update(colors);
                foreach (var width in new[] { 560, 720 })
                {
                    root.Measure(new Size(width, 440));
                    root.Arrange(new Rect(0, 0, width, 440));
                    root.UpdateLayout();
                    Assert.True(panel.List.ActualWidth > 150);
                    Assert.True(panel.Preview.ActualWidth > 250);
                    Assert.True(panel.ApplyButton.ActualHeight > 0);
                    Assert.Equal(colors[ThemeBrush.ListForeground], ((SolidColorBrush)panel.Preview.Foreground).Color);
                    Assert.Equal(colors[ThemeBrush.ListBackground], ((SolidColorBrush)panel.SearchBox.Background).Color);
                    foreach (var dpi in new[] { 96, 144, 192 })
                    {
                        var bitmap = new RenderTargetBitmap(width * dpi / 96, 440 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        if (directory is not null)
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(directory, $"surround-{state}-{mode}-{width}-{dpi}.png"));
                            encoder.Save(file);
                        }
                    }
                }
            }
        });
    }

    private static SqlSnippetSurroundPanel CreatePanel() => new(
        SqlSnippetDefaults.Current.Snippets.Where(item => item.CanSurround).ToArray(), 2,
        Selection("    SELECT CopyNo\n    FROM dbo.Copy;"));

    private static SqlSnippetSurroundSelection Selection(string sql) =>
        SqlSnippetSurroundSelection.Resolve(new SqlStringText(sql), 0, sql.Length);

    private static SqlSnippet Snippet(string shortcut, string description) => new(shortcut,
        "BEGIN\n    $surround$\nEND$end$", description: description,
        placeholders: new[] { new SqlSnippetPlaceholder("surround") });
}
