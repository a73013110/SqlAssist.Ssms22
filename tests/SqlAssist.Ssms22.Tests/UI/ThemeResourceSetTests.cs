using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class ThemeResourceSetTests
{
    [Fact]
    public void ExistingWindowsAndPrimaryButtonKeepFollowingColoredThemes()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var button = SqlAssistChrome.CreateButton("複製", SqlAssistChrome.DefaultMetrics, primary: true);
            var root = new Border { Child = button }
                .WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            var popup = new ContextMenu().WithTheme(Control.BackgroundProperty, ThemeBrush.ListBackground);
            root.Resources.MergedDictionaries.Add(palette.Resources);
            popup.Resources.MergedDictionaries.Add(palette.Resources);
            palette.Update(ThemePaletteTests.ColorsFor("mango"));
            button.ApplyTemplate();
            var surface = Assert.IsType<Border>(button.Template.FindName("bg", button));

            // 不重建已封存的樣板，也不只測深淺相反的兩個主題。
            foreach (var mode in new[] { "mango", "cool-breeze", "mango", "plum", "forest", "high-contrast", "mango" })
            {
                var colors = ThemePaletteTests.ColorsFor(mode);
                palette.Update(colors);
                Assert.Equal(colors[ThemeBrush.WindowBackground], ColorOf(root.Background));
                Assert.Equal(colors[ThemeBrush.ListBackground], ColorOf(popup.Background));
                Assert.Equal(colors[ThemeBrush.AccentBackground], ColorOf(surface.Background));
                Assert.Same(surface, button.Template.FindName("bg", button));
            }
        });
    }

    [Fact]
    public void ExistingControlsAndInlineTextFollowBothThemeDirections()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var run = new Run("Loan").WithTheme(TextElement.ForegroundProperty, ThemeBrush.ListForeground);
            var label = new TextBlock();
            label.Inlines.Add(run);
            var root = new Border { Child = label }
                .WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
            root.Resources.MergedDictionaries.Add(palette.Resources);

            foreach (var dark in new[] { false, true, false })
            {
                palette.Update(ColorsFor(dark));
                Assert.Equal(dark ? Colors.Black : Colors.White, ColorOf(root.Background));
                Assert.Equal(dark ? Colors.White : Colors.Black, ColorOf(run.Foreground));
                Assert.Same(run, label.Inlines.FirstInline);
            }
        });
    }

    [Fact]
    public void UnchangedColorsReuseFrozenBrushes()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ColorsFor(false));
            var previous = palette.Get(ThemeBrush.ListBackground);
            palette.Update(ColorsFor(false));
            Assert.Same(previous, palette.Get(ThemeBrush.ListBackground));
            Assert.True(previous.IsFrozen);
            palette.Update(ColorsFor(true));
            Assert.NotSame(previous, palette.Get(ThemeBrush.ListBackground));
        });
    }

    [Fact]
    public void DetachedPopupSharesPaletteWithoutInheritingOwnerResources()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var first = new Border().WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
            var popup = new ContextMenu().WithTheme(Control.BackgroundProperty, ThemeBrush.ListBackground);
            first.Resources.MergedDictionaries.Add(palette.Resources);
            popup.Resources.MergedDictionaries.Add(palette.Resources);
            palette.Update(ColorsFor(true));
            Assert.Same(first.Background, popup.Background);
            palette.Update(ColorsFor(false));
            Assert.Equal(Colors.White, ColorOf(popup.Background));
        });
    }

    [Fact]
    public void SealedTemplatesAndSelectedCellsKeepDynamicResources()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var button = SqlAssistChrome.CreateButton("複製", SqlAssistChrome.DefaultMetrics, primary: true);
            button.Resources.MergedDictionaries.Add(palette.Resources);
            palette.Update(ColorsFor(false));
            button.ApplyTemplate();
            var surface = Assert.IsType<Border>(button.Template.FindName("bg", button));
            var cell = new DataGridCell { Style = SqlAssistChrome.CreateCellStyle(), IsSelected = true };
            cell.Resources.MergedDictionaries.Add(palette.Resources);

            foreach (var dark in new[] { false, true, false })
            {
                palette.Update(ColorsFor(dark));
                Assert.Equal(dark ? Colors.Black : Colors.White, ColorOf(surface.Background));
                Assert.Same(palette.Get(ThemeBrush.RowSelected), cell.Background);
                Assert.Same(palette.Get(ThemeBrush.SelectedForeground), cell.Foreground);
            }

            // 高對比的選取文字必須與 Highlight 成對，而不是繼續用一般前景。
            palette.Update(new Dictionary<ThemeBrush, Color>
            {
                [ThemeBrush.RowSelected] = Colors.Yellow,
                [ThemeBrush.SelectedForeground] = Colors.Black
            });
            Assert.Equal(Colors.Yellow, ColorOf(cell.Background));
            Assert.Equal(Colors.Black, ColorOf(cell.Foreground));
        });
    }

    [Fact]
    public void TextBoxAndGridUseSameLivePalette()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var text = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
            var grid = SqlAssistChrome.CreateDataGrid(SqlAssistChrome.DefaultMetrics);
            var root = new StackPanel();
            root.Resources.MergedDictionaries.Add(palette.Resources);
            root.Children.Add(text);
            root.Children.Add(grid);
            foreach (var dark in new[] { true, false })
            {
                palette.Update(ColorsFor(dark));
                Assert.Same(grid.Background, text.Background);
                Assert.Same(grid.Foreground, text.CaretBrush);
                Assert.Same(palette.Get(ThemeBrush.RowAlternate), grid.AlternatingRowBackground);
            }
        });
    }

    [Fact]
    public void LegacySystemKeysAreScopedAndFollowPaletteChanges()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var originalSystemBrush = SystemColors.WindowBrush;
            foreach (var dark in new[] { false, true, false })
            {
                palette.Update(ColorsFor(dark));
                Assert.Same(palette.Get(ThemeBrush.ListBackground), palette.Resources[SystemColors.WindowBrushKey]);
                Assert.Equal(dark ? Colors.Black : Colors.White, palette.Resources[SystemColors.WindowColorKey]);
                Assert.Same(originalSystemBrush, SystemColors.WindowBrush);
            }
        });
    }

    [Fact]
    public void SharedPaletteDoesNotKeepClosedViewsAlive()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ColorsFor(false));
            var reference = CreateTemporaryView(palette);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(reference.IsAlive);
            GC.KeepAlive(palette);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateTemporaryView(ThemeResourceSet palette)
    {
        var view = new Border().WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
        view.Resources.MergedDictionaries.Add(palette.Resources);
        Assert.NotNull(view.Background);
        return new WeakReference(view);
    }

    internal static Dictionary<ThemeBrush, Color> ColorsFor(bool dark)
    {
        var result = new Dictionary<ThemeBrush, Color>();
        foreach (ThemeBrush key in Enum.GetValues(typeof(ThemeBrush)))
        {
            result[key] = dark ? Colors.Black : Colors.White;
        }

        result[ThemeBrush.ListForeground] = dark ? Colors.White : Colors.Black;
        result[ThemeBrush.SelectedForeground] = result[ThemeBrush.ListForeground];
        return result;
    }

    internal static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
}
