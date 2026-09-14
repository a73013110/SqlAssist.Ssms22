using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using SqlAssist.Ssms22.Preview;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Ssms22.UI;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.SqlMemory;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlMemoryVisualTests
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
            foreach (var label in new[] { "History", "Favorites" }) tabs.Items.Add(new TabItem { Header = label, Template = SqlAssistChrome.CreateTabItemTemplate() });
            tabs.SelectedIndex = 0;
            var current = SqlAssistChrome.CreateQueryConnectionButton();
            var toolbar = SqlAssistChrome.CreateQueryToolbar(tabs, current,
                SqlAssistChrome.CreateButton("重新整理", metrics), SqlAssistChrome.CreateButton("設定", metrics));
            header.Children.Add(toolbar);
            var search = SqlAssistChrome.CreateTextBox(metrics); search.Text = "Loan";
            header.Children.Add(SqlAssistChrome.CreateSearchBar(search, SqlAssistChrome.CreateQueryIconButton("Clear", "清除搜尋")));
            var filters = SqlAssistChrome.CreateQueryHistoryFilters(new SqlPillSelector("全部", "執行", "草稿"),
                new SqlPillSelector("今天", "7 天", "30 天", "不限") { SelectedIndex = 1 });
            header.Children.Add(filters);
            var scope = new SqlPillSelector("全域", "指定伺服器", "指定資料庫") { SelectedIndex = 2 };
            header.Children.Add(scope);
            var server = new SqlConnectionFilter("伺服器");
            server.SetOptions(new[] { "LibraryServer", "ArchiveServer", "BranchServer" }); header.Children.Add(server);
            var database = new SqlConnectionFilter("資料庫");
            database.SetOptions(new[] { "Library", "Archive" }); header.Children.Add(database);
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            footer.Children.Add(SqlAssistChrome.CreateMetadataText("已載入 50 筆", metrics));
            var list = new SqlMemoryList
            {
                ItemsSource = Enumerable.Range(0, 2000).Select(index => new SqlMemoryRow(new SqlHistoryItem(
                    Guid.NewGuid(), Guid.NewGuid(), index % 3 == 0 ? null : Guid.NewGuid(), "content", DateTimeOffset.Now.AddMinutes(-index * 3),
                    index % 2 == 0 ? SqlHistoryFilter.Drafts : SqlHistoryFilter.Executions,
                    index == 0 ? "借閱查詢" : "借閱明細 — " + index,
                    "SELECT LoanId, CopyNo\nFROM LoanDetail WHERE LoanId = 1;",
                    new SqlConnectionLabel("LibraryServer", "Library")))).ToArray(), SelectedIndex = 0
            }.WithTheme(Control.BackgroundProperty, ThemeBrush.WindowBackground);
            var historyRows = list.ItemsSource;
            var favoritesRows = Enumerable.Range(0, 2000).Select(index => new SqlMemoryRow(new SqlFavoriteItem(
                new SqlFavorite(Guid.NewGuid(), "借閱查詢 — " + index, "收藏說明", Guid.NewGuid(), SqlFavoriteScope.Database,
                    new SqlConnectionLabel("LibraryServer", "Library")), Guid.NewGuid(), "content",
                "SELECT LoanId, CopyNo\nFROM LoanDetail WHERE LoanId = 1;"))).ToArray();
            var viewer = SqlAssistChrome.CreateCodeViewer(metrics);
            var resources = new ResourceDictionary
            {
                [ScriptResource.FontFamily] = SqlAssistChrome.CodeFont, [ScriptResource.FontSize] = metrics.Body
            };
            viewer.Document = SqlScriptDocument.Build("-- 借閱明細\nSELECT LoanId, CopyNo\nFROM LoanDetail\nWHERE LoanId = 1;", resources);
            var summary = SqlAssistChrome.CreateMetadataText("借閱查詢 · LibraryServer · Library", metrics);
            var previewActions = new WrapPanel();
            foreach (var action in new[] { ("Copy", "複製全文"), ("Wrap", "顯示換行"), ("Favorite", "Add to Favorites"),
                ("Edit", "編輯 SQL"), ("Settings", "編輯收藏資料"), ("Remove", "Remove from Favorites"), ("Open", "開新 Query") })
            {
                var button = SqlAssistChrome.CreateQueryIconButton(action.Item1, action.Item2); button.Tag = action.Item1;
                previewActions.Children.Add(button);
            }
            var detail = SqlAssistChrome.CreateQueryDetailBody(viewer, SqlAssistChrome.CreateStatusText(metrics), previewActions);
            var split = new SqlMemorySplitView(list, detail, summary);
            root.Children.Add(split);
            var surface = new Border { Child = root }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            var directory = ThemeVisualTests.FindOutputDirectory();
            foreach (var mode in new[] { "light", "dark", "high-contrast", "mango", "forest", "light-again" })
            foreach (var favorites in new[] { false, true })
            {
                tabs.SelectedIndex = favorites ? 1 : 0;
                filters.Visibility = favorites ? Visibility.Collapsed : Visibility.Visible;
                scope.Visibility = favorites ? Visibility.Visible : Visibility.Collapsed;
                list.ItemsSource = favorites ? favoritesRows : historyRows; list.SelectedIndex = 0;
                foreach (Button action in previewActions.Children)
                    action.Visibility = (string)action.Tag is "Copy" or "Wrap" or "Open" || ((string)action.Tag == "Favorite") != favorites
                        ? Visibility.Visible : Visibility.Collapsed;
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                foreach (var role in new[] { ScriptResource.Foreground, ScriptResource.Keyword, ScriptResource.Comment, ScriptResource.String, ScriptResource.Number })
                    resources[role] = palette.Resources[ThemeBrush.ListForeground];
                resources[ScriptResource.Background] = palette.Resources[ThemeBrush.ListBackground];
                foreach (var width in new[] { 320, 440, 740 })
                foreach (var expanded in new[] { false, true })
                {
                    server.IsExpanded = database.IsExpanded = expanded;
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
                    Assert.True(list.ActualHeight >= 80);
                    Assert.True(detail.ActualHeight >= 100);
                    Assert.InRange(detail.TranslatePoint(new Point(0, detail.ActualHeight), surface).Y, 0, 600);
                    foreach (var dpi in new[] { 96, 144, 192 })
                    {
                        var bitmap = new RenderTargetBitmap(width * dpi / 96, 600 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                        bitmap.Render(surface);
                        if (directory is null) continue;
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, $"sql-memory-{(favorites ? "favorites" : "history")}-{mode}-{width}-{dpi}-{(expanded ? "filters" : "compact")}.png"));
                        encoder.Save(file);
                    }
                    AssertInkCenters(toolbarActions);
                    AssertDisclosureCenters(server);
                    AssertDisclosureCenters(database);
                }
            }
        });
    }

    [Fact]
    public void PillSelectionAndConnectionCollapsePreserveValuesAndSortIndependently()
    {
        WpfTest.Run(() =>
        {
            var filter = new SqlConnectionFilter("伺服器") { IsExpanded = true };
            filter.SetOptions(new[] { "BranchB", "BranchA" });
            var options = (WrapPanel)((ScrollViewer)filter.Children[1]).Content;
            var selectedPill = (RadioButton)options.Children[1];
            selectedPill.IsChecked = true;
            Assert.Same(selectedPill, options.Children[1]);
            Assert.Equal("BranchB", filter.Value);
            var heading = (Button)((DockPanel)filter.Children[0]).Children[0];
            heading.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(filter.IsExpanded);
            Assert.Equal("BranchB", filter.Value);
            var requested = 0;
            filter.OptionsRequested += (_, _) => requested++;
            filter.Sort = SqlConnectionFacetSort.Alphabetical;
            Assert.Equal(1, requested); Assert.Equal(0, filter.Offset); Assert.Equal("BranchB", filter.Value);
            filter.SetOptions(new[] { "BranchA", "BranchB" });
            heading.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(filter.IsExpanded);
            Assert.True(((RadioButton)options.Children[2]).IsChecked);
            ((RadioButton)options.Children[0]).IsChecked = true;
            Assert.Null(filter.Value);
        });
    }

    [Fact]
    public void CardTemplateHasThreeNamedActionsAndRecoveryCannotBeFavorited()
    {
        WpfTest.Run(() =>
        {
            var row = new SqlMemoryRow(new SqlHistoryItem(Guid.NewGuid(), Guid.NewGuid(), null, "id", DateTimeOffset.Now,
                SqlHistoryFilter.Drafts, "借閱查詢", "SELECT * FROM Loan;", null));
            var template = SqlAssistChrome.CreateSqlSummaryTemplate();
            template.Seal();
            var content = (FrameworkElement)template.LoadContent(); content.DataContext = row;
            content.Measure(new Size(400, 300)); content.Arrange(new Rect(0, 0, 400, 300)); content.UpdateLayout();
            var buttons = Descendants<Button>(content).ToArray();
            Assert.Equal(new[] { "Copy", "Open", "Favorite" }, buttons.Select(button => button.Tag));
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

    // 讀取 WPF 已產生的字形／向量繪圖 bounds，不把相同 Height 當作視覺置中的證據。
    private static double InkCenter(FrameworkElement element, Visual relativeTo)
    {
        var drawing = VisualTreeHelper.GetDrawing(element);
        Assert.NotNull(drawing);
        var bounds = drawing.Bounds;
        Assert.False(bounds.IsEmpty, "元件必須實際畫出可見內容。");
        return element.TransformToAncestor(relativeTo).Transform(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)).Y;
    }

    private static void AssertInkCenters(FrameworkElement root)
    {
        foreach (var button in Descendants<Button>(root))
        {
            var icon = Descendants<System.Windows.Shapes.Path>(button).FirstOrDefault();
            var label = Descendants<TextBlock>(button).FirstOrDefault(text => text.IsVisible || text.Visibility == Visibility.Visible);
            if (icon is null || label is null || label.ActualWidth == 0) continue;
            var iconCenter = InkCenter(icon, root); var textCenter = InkCenter(label, root);
            Assert.True(Math.Abs(iconCenter - textCenter) <= 2,
                $"{label.Text}: icon={iconCenter:F2}, text={textCenter:F2}, iconHeight={icon.ActualHeight}, textHeight={label.ActualHeight}");
        }
    }

    private static void AssertDisclosureCenters(SqlConnectionFilter filter)
    {
        AssertInkCenters(filter);
        var heading = (Button)((DockPanel)filter.Children[0]).Children[0];
        var label = Descendants<TextBlock>(heading).Single();
        var summary = (TextBlock)((DockPanel)filter.Children[0]).Children[2];
        Assert.InRange(Math.Abs(InkCenter(label, filter) - InkCenter(summary, filter)), 0, 2);
    }

    [Fact]
    public void SplitCollapsePreservesSelectionAndUserResizeWithoutOverflow()
    {
        WpfTest.Run(() =>
        {
            var list = new SqlMemoryList(); list.Items.Add("SQL"); list.SelectedIndex = 0;
            var detail = new Border();
            var split = new SqlMemorySplitView(list, detail);
            var changes = 0; split.DetailExpandedChanged += (_, _) => changes++;
            split.RowDefinitions[0].Height = new GridLength(5, GridUnitType.Star);
            split.RowDefinitions[2].Height = new GridLength(3, GridUnitType.Star);
            foreach (var height in new[] { 180, 400, 600 })
            {
                split.Measure(new Size(320, height)); split.Arrange(new Rect(0, 0, 320, height)); split.UpdateLayout();
                Assert.InRange(detail.TranslatePoint(new Point(0, detail.ActualHeight), split).Y, 0, height + 0.1);
                var expandedListHeight = list.ActualHeight;
                split.SetDetailExpanded(false); split.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
                Assert.True(list.ActualHeight > expandedListHeight);
                Assert.Equal(0, list.SelectedIndex);
                split.SetDetailExpanded(true); split.UpdateLayout();
                Assert.Equal(new GridLength(5, GridUnitType.Star), split.RowDefinitions[0].Height);
                Assert.Equal(new GridLength(3, GridUnitType.Star), split.RowDefinitions[2].Height);
            }
            Assert.Equal(6, changes);
        });
    }

    [Fact]
    public void SelectionDoesNotOpenQueryAndEnterOnActionDoesNotOpenTwice()
    {
        WpfTest.Run(() =>
        {
            var list = new SqlMemoryList();
            for (var i = 0; i < 2; i++) list.Items.Add(new SqlMemoryRow(new SqlHistoryItem(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), "content", DateTimeOffset.Now, SqlHistoryFilter.Executions, "借閱查詢", "SELECT 1;", null)));
            // 隱藏的 presentation source 供 WPF 路由鍵盤事件，不開使用者可見視窗。
            using var source = new HwndSource(new HwndSourceParameters("SQL Memory keyboard test") { Width = 440, Height = 300, WindowStyle = 0 });
            source.RootVisual = list;
            list.Measure(new Size(440, 300)); list.Arrange(new Rect(0, 0, 440, 300)); list.UpdateLayout();
            var opens = 0; var selections = 0; var actions = 0;
            list.OpenRequested += (_, _) => opens++;
            list.SelectionChanged += (_, _) => selections++;
            list.RowActionRequested += _ => actions++;
            list.SelectedIndex = 0; list.SelectedIndex = 1;
            Assert.Equal(2, selections); Assert.Equal(0, opens);
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var keyboard = new TestKeyboardDevice();
            var ctrlEnter = new KeyEventArgs(new TestKeyboardDevice(ModifierKeys.Control), source, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = row };
            list.RaiseEvent(ctrlEnter);
            Assert.False(ctrlEnter.Handled); Assert.Equal(0, opens);
            var enter = new KeyEventArgs(keyboard, source, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = row };
            list.RaiseEvent(enter);
            Assert.True(enter.Handled); Assert.Equal(1, opens);
            var button = Descendants<Button>(row).First();
            list.RaiseEvent(new KeyEventArgs(keyboard, source, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = button });
            Assert.Equal(1, opens);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            Assert.Equal(1, actions); Assert.Equal(1, opens);
        });
    }

    [Theory]
    [InlineData(SqlFavoriteScope.Global, "全域", "")]
    [InlineData(SqlFavoriteScope.Server, "LibraryServer", "")]
    [InlineData(SqlFavoriteScope.Database, "LibraryServer", "Library")]
    public void FavoriteCardsUseScopeBadgesAndCannotBeFavoriteAgain(SqlFavoriteScope scope, string server, string database)
    {
        var query = new SqlFavorite(Guid.NewGuid(), "借閱查詢", "", Guid.NewGuid(), scope,
            scope == SqlFavoriteScope.Global ? null : new SqlConnectionLabel("LibraryServer", scope == SqlFavoriteScope.Database ? "Library" : ""));
        var row = new SqlMemoryRow(new SqlFavoriteItem(query, Guid.NewGuid(), "content", "SELECT * FROM Loan;"));
        Assert.Equal(server, row.Server); Assert.Equal(database, row.Database);
        Assert.Equal("收藏", row.Status); Assert.False(row.CanAddFavorite);
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

    [Fact]
    public void FocusHoverAndSelectionChangeBrushesWithoutChangingLayout()
    {
        WpfTest.Run(() =>
        {
            var card = (ControlTemplate)SqlAssistChrome.CreateSqlCardStyle().Setters.OfType<Setter>()
                .Single(setter => setter.Property == Control.TemplateProperty).Value;
            var pill = (ControlTemplate)SqlAssistChrome.CreateQueryPillStyle().Setters.OfType<Setter>()
                .Single(setter => setter.Property == Control.TemplateProperty).Value;
            var layoutProperties = new[] { FrameworkElement.MarginProperty, Control.PaddingProperty, Border.PaddingProperty,
                Control.BorderThicknessProperty, Border.BorderThicknessProperty, FrameworkElement.HeightProperty, FrameworkElement.WidthProperty };
            foreach (var template in new[] { card, pill, SqlAssistChrome.CreateGhostButtonTemplate(), SqlAssistChrome.CreatePrimaryButtonTemplate() })
                foreach (var trigger in template.Triggers.OfType<Trigger>())
                    Assert.DoesNotContain(trigger.Setters.OfType<Setter>(), setter => layoutProperties.Contains(setter.Property));
        });
    }
}
