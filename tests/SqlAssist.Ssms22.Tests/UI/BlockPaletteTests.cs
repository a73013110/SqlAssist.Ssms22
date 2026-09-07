using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class BlockPaletteTests
{
    [Fact]
    public void 符號留空繼承全域且可單獨覆寫任一通道()
    {
        var defaults = new SqlAssistSettings { BlockKeywordForeground = "#202020", BlockKeywordBackground = "#EEDDAA" };
        var inherited = BlockPalette.Create(Colors.White, Colors.Black, Colors.Blue, "#4F86C6", false, defaults);
        Assert.Equal(inherited[ThemeBrush.BlockKeywordForeground], inherited[ThemeBrush.BlockSymbolForeground]);
        Assert.Equal(inherited[ThemeBrush.BlockKeywordBackground], inherited[ThemeBrush.BlockSymbolBackground]);
        var overridden = BlockPalette.Create(Colors.White, Colors.Black, Colors.Blue, "#4F86C6", false,
            new SqlAssistSettings { BlockKeywordForeground = "#202020", BlockKeywordBackground = "#EEDDAA", BlockSymbolBackground = "#303080" });
        Assert.Equal(inherited[ThemeBrush.BlockKeywordBackground], overridden[ThemeBrush.BlockKeywordBackground]);
        Assert.NotEqual(overridden[ThemeBrush.BlockKeywordBackground], overridden[ThemeBrush.BlockSymbolBackground]);
        Assert.True(ThemeColorMath.Contrast(overridden[ThemeBrush.BlockSymbolForeground], overridden[ThemeBrush.BlockSymbolBackground]) >= 4.5);
        Assert.True(ThemeColorMath.Contrast(overridden[ThemeBrush.BlockHintHoverForeground], overridden[ThemeBrush.BlockHintBackground]) >= 4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("mango")]
    [InlineData("forest")]
    public void 端點前背景分開自訂且維持文字與編輯器對比(string mode)
    {
        var shell = ThemePaletteTests.ColorsFor(mode);
        var settings = new SqlAssistSettings
        {
            BlockKeywordBackground = "#FFE000", BlockKeywordForeground = "#FFFF00",
            BlockSymbolBackground = "#0044CC", BlockSymbolForeground = "#0022AA"
        };
        var colors = BlockPalette.Create(shell[ThemeBrush.ListBackground], shell[ThemeBrush.ListForeground],
            shell[ThemeBrush.AccentBorder], "#4F86C6", false, settings);
        Assert.NotEqual(colors[ThemeBrush.BlockKeywordBackground], colors[ThemeBrush.BlockSymbolBackground]);
        foreach (var pair in new[] { (ThemeBrush.BlockKeywordForeground, ThemeBrush.BlockKeywordBackground),
            (ThemeBrush.BlockSymbolForeground, ThemeBrush.BlockSymbolBackground) })
        {
            Assert.Equal((byte)255, colors[pair.Item2].A);
            Assert.True(ThemeColorMath.Contrast(colors[pair.Item1], colors[pair.Item2]) >= 4.5);
            Assert.True(ThemeColorMath.Contrast(colors[pair.Item2], shell[ThemeBrush.ListBackground]) >= 3);
        }
    }

    [Fact]
    public void 端點無效色碼回退與高對比覆寫全部自訂色()
    {
        var defaults = BlockPalette.Create(Colors.Black, Colors.White, Colors.Blue, null, false);
        var invalid = BlockPalette.Create(Colors.Black, Colors.White, Colors.Blue, null, false,
            new SqlAssistSettings { BlockKeywordForeground = "bad", BlockSymbolBackground = "#00" });
        Assert.Equal(defaults, invalid);
        var custom = new SqlAssistSettings { BlockKeywordForeground = "#FF0000", BlockSymbolBackground = "#00FF00" };
        var colors = BlockPalette.Create(Colors.Black, Colors.Yellow, Colors.Blue, "#00FFFF", true, custom);
        Assert.Equal(Colors.Yellow, colors[ThemeBrush.BlockKeywordBackground]);
        Assert.Equal(Colors.Black, colors[ThemeBrush.BlockKeywordForeground]);
        Assert.Equal(colors[ThemeBrush.BlockKeywordBackground], colors[ThemeBrush.BlockSymbolBackground]);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("mango")]
    [InlineData("plum")]
    [InlineData("cool-breeze")]
    [InlineData("forest")]
    public void 自訂色在各主題維持低干擾背景與可讀提示(string mode)
    {
        var shell = ThemePaletteTests.ColorsFor(mode);
        var background = shell[ThemeBrush.ListBackground];
        var foreground = shell[ThemeBrush.ListForeground];
        foreach (var preference in new[] { "", "#4F86C6", "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#888888" })
        {
            var colors = BlockPalette.Create(background, foreground, shell[ThemeBrush.AccentBorder], preference, false);
            Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.Block], background) >= 3);
            var range = colors[ThemeBrush.BlockRange];
            Assert.InRange(range.A, (byte)1, (byte)31);
            Assert.NotEqual(background, ThemeColorMath.Composite(range, background));
            Assert.True(ThemeColorMath.Contrast(foreground, ThemeColorMath.Composite(range, background)) >= 4.5);
            Assert.Equal((byte)255, colors[ThemeBrush.BlockHintBackground].A);
            Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.BlockHintForeground], colors[ThemeBrush.BlockHintBackground]) >= 4.5);
            Assert.Equal(colors[ThemeBrush.Block].R, range.R);
            Assert.Equal(colors[ThemeBrush.Block].G, range.G);
            Assert.Equal(colors[ThemeBrush.Block].B, range.B);
        }
    }

    [Fact]
    public void 自訂編輯器底色與殼層相反仍依實際底色適配()
    {
        foreach (var background in new[] { Colors.White, Colors.Black, Color.FromRgb(65, 75, 85) })
        {
            var colors = BlockPalette.Create(background, background, Colors.Blue, "#4F86C6", false);
            Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.Block], background) >= 3);
            Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.BlockHintForeground], colors[ThemeBrush.BlockHintBackground]) >= 4.5);
        }
    }

    [Fact]
    public void 高對比忽略自訂色且不塗區間背景()
    {
        var colors = BlockPalette.Create(Colors.Black, Colors.Yellow, Colors.Blue, "#FF0000", true);
        Assert.Equal(Colors.Yellow, colors[ThemeBrush.Block]);
        Assert.Equal(Colors.Black, colors[ThemeBrush.BlockHintBackground]);
        Assert.Equal(Colors.Yellow, colors[ThemeBrush.BlockHintForeground]);
        Assert.Equal((byte)0, colors[ThemeBrush.BlockRange].A);
    }

    [Fact]
    public void 無效色碼回退且重複深淺切換不累積透明度()
    {
        var first = BlockPalette.Create(Colors.White, Colors.Black, Colors.Blue, "#4F86C6", false);
        for (var i = 0; i < 20; i++)
        {
            var dark = BlockPalette.Create(Colors.Black, Colors.White, Colors.Red, "#4F86C6", false);
            Assert.NotEqual(ThemeColorMath.Composite(first[ThemeBrush.BlockRange], Colors.White),
                ThemeColorMath.Composite(dark[ThemeBrush.BlockRange], Colors.Black));
            var light = BlockPalette.Create(Colors.White, Colors.Black, Colors.Blue, "#4F86C6", false);
            Assert.Equal(first, light);
        }
        Assert.Equal(BlockPalette.Create(Colors.White, Colors.Black, Colors.Blue, null, false),
            BlockPalette.Create(Colors.White, Colors.Black, Colors.Blue, "invalid", false));
    }

    [Fact]
    public void 動態提示與背景共用凍結筆刷並保留元素及文字() => ExercisePreview(render: false);

    [Fact]
    public void 區塊提示與背景在多DPI及窄視窗離屏渲染() => ExercisePreview(render: true);

    private static void ExercisePreview(bool render)
    {
        WpfTest.Run(() =>
        {
            if (render)
            {
                // 某些非互動工作階段連純色 DrawingVisual 都只回傳透明像素，不能誤報為視覺驗收通過。
                var probe = new DrawingVisual();
                using (var drawing = probe.RenderOpen())
                    drawing.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 1, 1));
                var sample = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
                sample.Render(probe);
                var pixel = new byte[4];
                sample.CopyPixels(pixel, 4, 0);
                Assert.SkipWhen(pixel[3] == 0, "目前工作階段的 WPF 離屏渲染只回傳透明像素；需在可渲染的桌面執行視覺驗收。");
            }
            var resources = new ThemeResourceSet();
            var hint = SqlAssistChrome.CreateBlockContext(out var text);
            text.Text = "↑ BEGIN CATCH（第 42 行）";
            var sql = new TextBlock
            {
                FontFamily = SqlAssistChrome.CodeFont, FontSize = 14
            }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
            sql.Inlines.Add(new Run("BEGIN").WithTheme(TextElement.ForegroundProperty, ThemeBrush.BlockKeywordForeground)
                .WithTheme(TextElement.BackgroundProperty, ThemeBrush.BlockKeywordBackground));
            sql.Inlines.Add(new Run("\n    SELECT CopyNo FROM Copy;\n"));
            sql.Inlines.Add(new Run("END").WithTheme(TextElement.ForegroundProperty, ThemeBrush.BlockKeywordForeground)
                .WithTheme(TextElement.BackgroundProperty, ThemeBrush.BlockKeywordBackground));
            var range = new Border { Height = 88, Padding = new Thickness(8), Child = sql }
                .WithTheme(Border.BackgroundProperty, ThemeBrush.BlockRange);
            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(hint);
            panel.Children.Add(range);
            var root = new Border { Child = panel }.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
            root.Resources.MergedDictionaries.Add(resources.Resources);
            var directory = Path.Combine(ThemeVisualTests.FindOutputDirectory() ?? AppContext.BaseDirectory, "block-qa");
            Directory.CreateDirectory(directory);
            foreach (var mode in new[] { "light", "dark", "mango", "plum", "high-contrast", "light" })
            {
                var shell = ThemePaletteTests.ColorsFor(mode);
                resources.Update(shell);
                var palette = BlockPalette.Create(shell[ThemeBrush.ListBackground], shell[ThemeBrush.ListForeground],
                    shell[ThemeBrush.AccentBorder], "#4F86C6", mode == "high-contrast");
                resources.Update(palette);
                var old = resources.Get(ThemeBrush.BlockRange);
                resources.Update(palette);
                Assert.Same(old, resources.Get(ThemeBrush.BlockRange));
                Assert.All(palette.Keys, role => Assert.True(resources.Get(role).IsFrozen));
                Assert.Equal("↑ BEGIN CATCH（第 42 行）", text.Text);
                Assert.Same(text, hint.Content);
                Assert.Equal(1, hint.Opacity);
                Assert.True(hint.IsHitTestVisible);
                Assert.False(hint.Focusable);
                Assert.False(hint.IsTabStop);
                Assert.Same(System.Windows.Input.Cursors.Hand, hint.Cursor);
                foreach (var width in new[] { 220, 520 })
                foreach (var dpi in new[] { 96, 144, 192 })
                {
                    root.Measure(new Size(width, 136));
                    root.Arrange(new Rect(0, 0, width, 136));
                    root.UpdateLayout();
                    root.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                    Assert.Same(resources.Get(ThemeBrush.BlockHintBackground), hint.Background);
                    Assert.Same(resources.Get(ThemeBrush.BlockHintForeground), text.Foreground);
                    if (!render) continue;
                    var bitmap = new RenderTargetBitmap(width * dpi / 96, 136 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    Assert.Contains(Enumerable.Range(0, bitmap.PixelWidth * bitmap.PixelHeight), i => pixels[i * 4 + 3] != 0);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"{mode}-{width}-{dpi}.png"));
                    encoder.Save(file);
                }
            }
        });
    }
}
