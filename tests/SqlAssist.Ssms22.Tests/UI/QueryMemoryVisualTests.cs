using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Ssms22.UI;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Ssms22.QueryMemory;
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
            var root = new DockPanel { Margin = new Thickness(8) };
            System.Windows.Documents.TextElement.SetFontFamily(root, SqlAssistChrome.InterfaceFont);
            System.Windows.Documents.TextElement.SetFontSize(root, metrics.Body);
            var header = new StackPanel();
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var tabs = new TabControl { Template = SqlAssistChrome.CreateTabControlTemplate() };
            foreach (var label in new[] { "歷史", "收藏" }) tabs.Items.Add(new TabItem { Header = label, Template = SqlAssistChrome.CreateTabItemTemplate() });
            tabs.SelectedIndex = 0;
            var current = SqlAssistChrome.CreateQueryConnectionButton();
            var toolbar = SqlAssistChrome.CreateQueryToolbar(tabs, current,
                SqlAssistChrome.CreateButton("重新整理", metrics), SqlAssistChrome.CreateButton("設定", metrics));
            header.Children.Add(toolbar);
            var search = SqlAssistChrome.CreateTextBox(metrics); search.Text = "Loan";
            header.Children.Add(SqlAssistChrome.CreateSearchBar(search, SqlAssistChrome.CreateQueryIconButton("Clear", "清除搜尋")));
            var filters = new WrapPanel();
            filters.Children.Add(new SqlPillSelector("全部", "執行", "草稿") { Margin = new Thickness(0, 0, 8, 0) });
            filters.Children.Add(new SqlPillSelector("今天", "7 天", "30 天", "不限") { SelectedIndex = 1 });
            header.Children.Add(filters);
            var server = new SqlConnectionFilter("伺服器");
            server.SetOptions(new[] { "LibraryServer", "ArchiveServer", "BranchServer" }); header.Children.Add(server);
            var database = new SqlConnectionFilter("資料庫");
            database.SetOptions(new[] { "Library", "Archive" }); header.Children.Add(database);
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            footer.Children.Add(SqlAssistChrome.CreateMetadataText("已載入 50 筆", metrics));
            var list = new ListBox
            {
                BorderThickness = default,
                ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle(),
                ItemTemplate = SqlAssistChrome.CreateSqlSummaryTemplate(),
                ItemsSource = Enumerable.Range(0, 2000).Select(index => new QueryMemoryRow(new QueryHistoryItem(
                    Guid.NewGuid(), Guid.NewGuid(), index % 3 == 0 ? null : Guid.NewGuid(), "content", DateTimeOffset.Now.AddMinutes(-index * 3),
                    index % 2 == 0 ? QueryHistoryKind.Drafts : QueryHistoryKind.Executed,
                    index == 0 ? "借閱查詢" : "借閱明細 — " + index,
                    "SELECT LoanId, CopyNo\nFROM LoanDetail WHERE LoanId = 1;",
                    new QueryConnectionContext("LibraryServer", "Library"), false))).ToArray(), SelectedIndex = 0
            }.WithTheme(Control.BackgroundProperty, ThemeBrush.WindowBackground);
            ScrollViewer.SetCanContentScroll(list, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            root.Children.Add(list);
            var surface = new Border { Child = root }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            var directory = ThemeVisualTests.FindOutputDirectory();
            foreach (var mode in new[] { "light", "dark", "high-contrast", "mango", "forest", "light-again" })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                foreach (var width in new[] { 320, 440, 740 })
                {
                    surface.Measure(new Size(width, 600)); surface.Arrange(new Rect(0, 0, width, 600)); surface.UpdateLayout();
                    var tabCenter = tabs.TranslatePoint(new Point(0, tabs.ActualHeight / 2), toolbar).Y;
                    var toolbarActions = (StackPanel)toolbar.Children[1];
                    foreach (Button button in toolbarActions.Children)
                    {
                        Assert.InRange(Math.Abs(button.TranslatePoint(new Point(0, button.ActualHeight / 2), toolbar).Y - tabCenter), 0, 0.5);
                        Assert.Equal(28, button.ActualHeight);
                    }
                    Assert.Equal(0, tabs.TranslatePoint(new Point(), toolbar).X);
                    var settings = (Button)toolbarActions.Children[2];
                    Assert.InRange(Math.Abs(settings.TranslatePoint(new Point(settings.ActualWidth, 0), toolbar).X - toolbar.ActualWidth), 0, 0.5);
                    Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                    Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(1999));
                    Assert.True(list.ActualHeight > 200);
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

    [Fact]
    public void PillSelectionAndConnectionCollapsePreserveValuesAndSortIndependently()
    {
        WpfTest.Run(() =>
        {
            var filter = new SqlConnectionFilter("伺服器");
            filter.SetOptions(new[] { "BranchB", "BranchA" });
            var options = (WrapPanel)((ScrollViewer)((DockPanel)filter.Children[0]).Children[2]).Content;
            var selectedPill = (RadioButton)options.Children[1];
            selectedPill.IsChecked = true;
            Assert.Same(selectedPill, options.Children[1]);
            Assert.Equal("BranchB", filter.Value);
            var heading = (Button)((DockPanel)filter.Children[0]).Children[0];
            heading.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Collapsed, options.Visibility);
            Assert.Equal("BranchB", filter.Value);
            var requested = 0;
            filter.OptionsRequested += (_, _) => requested++;
            filter.SortOrder = 2;
            Assert.Equal(1, requested); Assert.Equal(0, filter.Offset); Assert.Equal("BranchB", filter.Value);
            filter.SetOptions(new[] { "BranchA", "BranchB" });
            heading.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, options.Visibility);
            Assert.True(((RadioButton)options.Children[2]).IsChecked);
            ((RadioButton)options.Children[0]).IsChecked = true;
            Assert.Null(filter.Value);
        });
    }

    [Fact]
    public void CardTemplateHasFourNamedActionsAndRecoveryCannotBeSaved()
    {
        WpfTest.Run(() =>
        {
            var row = new QueryMemoryRow(new QueryHistoryItem(Guid.NewGuid(), Guid.NewGuid(), null, "id", DateTimeOffset.Now,
                QueryHistoryKind.Drafts, "借閱查詢", "SELECT * FROM Loan;", null, false));
            var template = SqlAssistChrome.CreateSqlSummaryTemplate();
            template.Seal();
            var content = (FrameworkElement)template.LoadContent(); content.DataContext = row;
            content.Measure(new Size(400, 300)); content.Arrange(new Rect(0, 0, 400, 300)); content.UpdateLayout();
            var buttons = Descendants<Button>(content).ToArray();
            Assert.Equal(new[] { "Copy", "Open", "Favorite", "Preview" }, buttons.Select(button => button.Tag));
            Assert.False(buttons[2].IsEnabled);
            Assert.Contains("尚無版本", (string)buttons[2].ToolTip);
            Assert.All(buttons, button => Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button))));
            Assert.Null(SqlAssistChrome.CreateSqlCardStyle().Setters.OfType<Setter>().Single(setter => setter.Property == Control.FocusVisualStyleProperty).Value);
        });
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    [Theory]
    [InlineData(SavedQueryScope.Global, "全域", "")]
    [InlineData(SavedQueryScope.Server, "LibraryServer", "")]
    [InlineData(SavedQueryScope.Database, "LibraryServer", "Library")]
    public void SavedCardsUseScopeBadgesAndCannotBeSavedAgain(SavedQueryScope scope, string server, string database)
    {
        var query = new SavedQuery(Guid.NewGuid(), "借閱查詢", "", Guid.NewGuid(), scope,
            scope == SavedQueryScope.Global ? null : new QueryConnectionContext("LibraryServer", scope == SavedQueryScope.Database ? "Library" : ""), false);
        var row = new QueryMemoryRow(new SavedQueryEntry(query, Guid.NewGuid(), "content", "SELECT * FROM Loan;"));
        Assert.Equal(server, row.Server); Assert.Equal(database, row.Database);
        Assert.Equal("收藏", row.Status); Assert.False(row.CanSave);
    }

    [Fact]
    public void QueryButtonIconsAndLabelsFollowTemplateContrastForeground()
    {
        WpfTest.Run(() =>
        {
            var button = SqlAssistChrome.CreateQueryConnectionButton();
            button.Measure(new Size(200, 40)); button.Arrange(new Rect(0, 0, 200, 40)); button.UpdateLayout();
            var background = (Border)button.Template.FindName("bg", button);
            System.Windows.Documents.TextElement.SetForeground(background, Brushes.Lime);
            button.UpdateLayout();
            Assert.Same(Brushes.Lime, Descendants<System.Windows.Shapes.Path>(button).Single().Stroke);
            Assert.Same(Brushes.Lime, Descendants<TextBlock>(button).Single().Foreground);
        });
    }
}
