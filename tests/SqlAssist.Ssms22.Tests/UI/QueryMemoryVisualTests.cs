using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class QueryMemoryVisualTests
{
    [Fact]
    public void SqlSummaryRowsStayVirtualizedAndRenderAcrossThemesAndDpi()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var metrics = SqlAssistChrome.DefaultMetrics;
            var root = new DockPanel { Margin = new Thickness(12) };
            var header = new StackPanel();
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var tabs = new TabControl { Template = SqlAssistChrome.CreateTabControlTemplate() };
            foreach (var label in new[] { "歷史", "收藏" }) tabs.Items.Add(new TabItem { Header = label, Template = SqlAssistChrome.CreateTabItemTemplate() });
            tabs.SelectedIndex = 0; header.Children.Add(tabs);
            var search = SqlAssistChrome.CreateTextBox(metrics); search.Text = "Loan"; header.Children.Add(search);
            header.Children.Add(SqlAssistChrome.CreateHint("區分大小寫的字面搜尋 · 最近 7 天", metrics));
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            footer.Children.Add(SqlAssistChrome.CreateMetadataText("已載入 50 筆", metrics));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(SqlAssistChrome.CreateButton("預覽", metrics));
            actions.Children.Add(SqlAssistChrome.CreateButton("開啟至新查詢", metrics, true)); footer.Children.Add(actions);
            var list = new ListBox
            {
                BorderThickness = default,
                ItemContainerStyle = SqlAssistChrome.CreateListItemStyle(metrics),
                ItemTemplate = SqlAssistChrome.CreateSqlSummaryTemplate(),
                ItemsSource = Enumerable.Range(0, 2000).Select(index => new
                {
                    Name = index == 0 ? "借閱查詢" : "借閱明細 — " + index,
                    Preview = "SELECT LoanId, CopyNo FROM LoanDetail WHERE LoanId = 1;",
                    Detail = "2026/09/12 14:30 · 執行 · Library · Main"
                }).ToArray(), SelectedIndex = 0
            }.WithTheme(Control.BackgroundProperty, ThemeBrush.WindowBackground);
            ScrollViewer.SetCanContentScroll(list, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            root.Children.Add(list);
            var surface = new Border { Child = root }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            var directory = ThemeVisualTests.FindOutputDirectory();
            foreach (var mode in new[] { "light", "dark", "high-contrast", "light-again" })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                foreach (var width in new[] { 320, 440 })
                {
                    surface.Measure(new Size(width, 600)); surface.Arrange(new Rect(0, 0, width, 600)); surface.UpdateLayout();
                    Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                    Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(1999));
                    foreach (var dpi in new[] { 96, 144, 192 })
                    {
                        var bitmap = new RenderTargetBitmap(width * dpi / 96, 600 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                        bitmap.Render(surface);
                        if (directory is null) continue;
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, $"query-memory-components-{mode}-{width}-{dpi}.png"));
                        encoder.Save(file);
                    }
                }
            }
        });
    }
}
