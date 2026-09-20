using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Search;
using SqlAssist.Ssms22.Search;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlSearchVisualTests
{
    [Fact]
    public void 結果列在每一種主題都讀所屬控制項的動態筆刷()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var rows = Rows();
            var list = new SqlSearchList();
            list.SetRowsSource(rows);
            list.SelectedIndex = 0;

            var surface = new Border { Child = list }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            surface.Resources.MergedDictionaries.Add(palette.Resources);

            foreach (var mode in new[] { "light", "dark", "high-contrast", "mango", "forest", "light-again" })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                surface.Measure(new Size(440, 400));
                surface.Arrange(new Rect(0, 0, 440, 400));
                surface.UpdateLayout();

                // 取消了上下分組：命中部位由每一列自己的徽章表達，三種列在同一疊裡。
                var targets = Descendants<TextBlock>(list)
                    .Where(text => text.Text is "名稱" or "內容" or "欄位").ToArray();
                Assert.Equal(new[] { "名稱", "名稱", "內容" }, targets.Select(text => text.Text).ToArray());
                Assert.All(targets, text => Assert.Same(palette.Resources[ThemeBrush.ListForeground], text.Foreground));

                var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[0]);
                Assert.NotNull(selected);
                // 選取的那一列文字換成配對前景，不只換底色；高對比也一樣。
                Assert.Same(palette.Resources[ThemeBrush.SelectedForeground], selected.Foreground);

                var highlights = Descendants<SqlHighlightText>(list)
                    .SelectMany(text => text.Inlines.OfType<Run>())
                    .Where(run => run.Background is not null).ToArray();
                Assert.NotEmpty(highlights);
                Assert.All(highlights, run => Assert.Same(palette.Resources[ThemeBrush.AccentBackground], run.Background));
            }
        });
    }

    [Fact]
    public void 結果列的脈絡膠囊來自命中而不是酬載()
    {
        WpfTest.Run(() =>
        {
            var rows = Rows();
            var list = new SqlSearchList();
            list.SetRowsSource(rows);

            var host = new Border { Child = list };
            host.Measure(new Size(520, 400));
            host.Arrange(new Rect(0, 0, 520, 400));
            host.UpdateLayout();

            var badges = Descendants<TextBlock>(list).Select(text => text.Text).ToArray();
            Assert.Contains("LIBSRV", badges);
            Assert.Contains("LibArchive", badges);

            // 圖示插槽讀的是分類 Id 這個字串；認不得的分類留空插槽，那一列仍有標題與路徑。
            var icons = Descendants<SqlIconImage>(list).Where(icon => icon.CategoryId is { Length: > 0 }).ToArray();
            Assert.NotEmpty(icons);
            Assert.All(icons, icon => Assert.StartsWith("catalog.", icon.CategoryId));
        });
    }

    [Fact]
    public void 分段開關至少留一段且對應旗標()
    {
        WpfTest.Run(() =>
        {
            var segments = new SqlSearchSegments();
            var changes = 0;
            segments.ValueChanged += (_, _) => changes++;

            var toggles = Descendants<ToggleButton>(segments).ToArray();
            Assert.Equal(3, toggles.Length);
            Assert.Equal(SearchTargets.All, segments.Value);
            Assert.All(toggles, toggle => Assert.True(toggle.IsChecked));

            toggles[1].IsChecked = false;
            Assert.Equal(SearchTargets.Name | SearchTargets.Column, segments.Value);
            Assert.Equal(1, changes);

            toggles[2].IsChecked = false;
            Assert.Equal(SearchTargets.Name, segments.Value);
            Assert.Equal(2, changes);

            // 最後一段關不掉：一個部位都不掃的查詢找不到任何東西，而畫面上與「這個字串不存在」一樣。
            toggles[0].IsChecked = false;
            Assert.Equal(SearchTargets.Name, segments.Value);
            Assert.True(toggles[0].IsChecked);
            Assert.Equal(2, changes);
        });
    }

    [Fact]
    public void 工具列窄窗先收字再收行()
    {
        WpfTest.Run(() =>
        {
            var search = SqlAssistChrome.CreateSearchBar(
                SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics),
                SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋"));
            var segments = new SqlSearchSegments();
            var kinds = new SqlSearchFilterButton("種類", SqlIcon.Filter);
            var databases = new SqlSearchFilterButton("資料庫", SqlIcon.Database, filterable: true);
            var toolbar = new SqlSearchToolbar(search, segments, new[] { databases, kinds });

            var host = new Border { Child = toolbar };

            Size Layout(double width)
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
                return host.DesiredSize;
            }

            var full = Layout(SqlSearchToolbar.CompactWidth + 40);
            Assert.Equal(SqlSearchToolbarMode.Full, toolbar.Mode);
            Assert.False(kinds.IsCompact);

            var compact = Layout(SqlSearchToolbar.CompactWidth - 40);
            Assert.Equal(SqlSearchToolbarMode.Compact, toolbar.Mode);
            Assert.True(kinds.IsCompact);
            // 收字不換行：高度不變，搜尋框只是變窄。
            Assert.Equal(full.Height, compact.Height);

            var stacked = Layout(SqlSearchToolbar.StackedWidth - 40);
            Assert.Equal(SqlSearchToolbarMode.Stacked, toolbar.Mode);
            // 分段開關是常駐可見的，收掉字就等於收掉它；放不下時換到第二列。
            Assert.True(stacked.Height > compact.Height);

            // 拉回去要回到完整版，不停在窄版上。
            Layout(SqlSearchToolbar.CompactWidth + 40);
            Assert.Equal(SqlSearchToolbarMode.Full, toolbar.Mode);
            Assert.False(kinds.IsCompact);
        });
    }

    [Fact]
    public void 主從區寬到門檻才轉成左右而預設維持上下()
    {
        WpfTest.Run(() =>
        {
            var master = new Border { MinHeight = 40 };
            var detail = new Border { MinHeight = 40 };
            var responsive = new SqlMemorySplitView(master, detail, sideBySideWidth: 520);
            var host = new Border { Child = responsive };

            void Layout(double width)
            {
                host.Measure(new Size(width, 400));
                host.Arrange(new Rect(0, 0, width, 400));
                host.UpdateLayout();
            }

            Layout(360);
            Assert.False(responsive.IsSideBySide);

            Layout(600);
            Assert.True(responsive.IsSideBySide);
            Assert.Equal(3, responsive.ColumnDefinitions.Count);
            Assert.Equal(0, Grid.GetColumn(master));
            Assert.Equal(2, Grid.GetColumn(detail));

            Layout(360);
            Assert.False(responsive.IsSideBySide);
            Assert.Equal(3, responsive.RowDefinitions.Count);
            Assert.Equal(0, Grid.GetRow(master));
            Assert.Equal(2, Grid.GetRow(detail));

            // 不傳門檻就完全維持原行為；SQL Memory 的上下分割不由這一次順手改掉。
            var fixedSplit = new SqlMemorySplitView(new Border(), new Border());
            var fixedHost = new Border { Child = fixedSplit };
            fixedHost.Measure(new Size(1200, 400));
            fixedHost.Arrange(new Rect(0, 0, 1200, 400));
            fixedHost.UpdateLayout();
            Assert.False(fixedSplit.IsSideBySide);
        });
    }

    [Fact]
    public void 已選條件列只在非預設時出現()
    {
        WpfTest.Run(() =>
        {
            var chips = new SqlSearchChipBar();
            Assert.Equal(Visibility.Collapsed, chips.Visibility);

            var removed = new List<string>();
            chips.RemoveRequested += chip => removed.Add((string)chip);
            chips.SetChips(new[] { "種類: Table", "資料庫: LibArchive" }, chip => chip);
            Assert.Equal(Visibility.Visible, chips.Visibility);
            Assert.Equal(2, chips.Items.Count);

            var host = new Border { Child = chips };
            host.Measure(new Size(400, 100));
            host.Arrange(new Rect(0, 0, 400, 100));
            host.UpdateLayout();

            var buttons = Descendants<Button>(chips).ToArray();
            Assert.Equal(2, buttons.Length);
            buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "種類: Table" }, removed.ToArray());

            chips.SetChips(Array.Empty<string>(), chip => chip);
            // 沒篩選就不佔那一列；Collapsed 才真的不參與量測。
            Assert.Equal(Visibility.Collapsed, chips.Visibility);
        });
    }

    [Fact]
    public void 結果卡片分段開關與篩選chip的互動狀態只換筆刷不改版面尺寸()
    {
        WpfTest.Run(() =>
        {
            var layoutProperties = new[]
            {
                FrameworkElement.MarginProperty, Control.PaddingProperty, Border.PaddingProperty,
                Control.BorderThicknessProperty, Border.BorderThicknessProperty,
                FrameworkElement.HeightProperty, FrameworkElement.WidthProperty,
            };

            var card = (ControlTemplate)SqlAssistChrome.CreateSqlCardStyle(motion: false, removable: false).Setters
                .OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty).Value;
            var option = SqlAssistChrome.CreateCheckBoxTemplate();
            var segment = (ControlTemplate)SqlAssistChrome.CreateSegmentToggleStyle().Setters
                .OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty).Value;

            foreach (var template in new[] { card, option, segment })
            foreach (var trigger in template.Triggers.OfType<Trigger>())
            {
                Assert.DoesNotContain(trigger.Setters.OfType<Setter>(), setter => layoutProperties.Contains(setter.Property));
            }

            // 結果沒有刪除動作；留著 IsRemoving 的繫結只會在每一列上找一個不存在的屬性。
            Assert.DoesNotContain(card.Triggers.OfType<DataTrigger>(),
                trigger => (trigger.Binding as Binding)?.Path.Path == "IsRemoving");

            // 停駐才出現的動作列用 Hidden 保留尺寸；Collapsed 會讓列在停駐的瞬間重新排版。
            var reveal = SqlAssistChrome.CreateSearchHitTemplate().Triggers.OfType<DataTrigger>()
                .Where(trigger => trigger.Setters.OfType<Setter>().Any(setter => setter.TargetName == "actions"))
                .ToArray();
            Assert.Equal(2, reveal.Length);
            Assert.All(reveal, trigger => Assert.Equal(
                Visibility.Visible, trigger.Setters.OfType<Setter>().Single().Value));
        });
    }

    [Fact]
    public void 狀態回饋走RenderTransform不改變版面尺寸()
    {
        WpfTest.Run(() =>
        {
            var status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
            status.Text = "找到 12 項";
            var host = new Border { Child = status };
            host.Measure(new Size(320, 40));
            host.Arrange(new Rect(0, 0, 320, 40));
            host.UpdateLayout();
            var before = new Size(status.ActualWidth, status.ActualHeight);

            SqlAssistChrome.PlayStatusPop(status, motion: true);
            host.UpdateLayout();

            Assert.IsType<ScaleTransform>(status.RenderTransform);
            Assert.Equal(before.Width, status.ActualWidth);
            Assert.Equal(before.Height, status.ActualHeight);
            Assert.InRange(SqlAssistChrome.SearchStatusPop, TimeSpan.Zero, TimeSpan.FromMilliseconds(400));

            // 動畫關閉時把屬性交還基底值，「關掉動畫」不能變成關不掉。
            SqlAssistChrome.PlayStatusPop(status, motion: false);
            var scale = (ScaleTransform)status.RenderTransform;
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(1, scale.ScaleY);
        });
    }

    [Fact]
    public void 高亮只分段不改內容()
    {
        WpfTest.Run(() =>
        {
            var text = new SqlHighlightText
            {
                SourceText = "JOIN Loan AS l ON l.CopyNo = c.CopyNo",
                Spans = new[] { new MatchSpan(5, 4) },
            };

            // 讀 Inlines 而不是 TextBlock.Text：內容由 Run 組成時那個屬性是空的，
            // 拿它當證據會把「一個字都沒畫出來」讀成通過。
            Assert.Equal("JOIN Loan AS l ON l.CopyNo = c.CopyNo", Rendered(text));
            var runs = text.Inlines.OfType<Run>().ToArray();
            Assert.Equal(3, runs.Length);
            Assert.Equal("Loan", runs[1].Text);
            Assert.Equal(FontWeights.SemiBold, runs[1].FontWeight);

            // 區段超出範圍時整段忽略，不畫在別的字上。
            text.Spans = new[] { new MatchSpan(100, 4) };
            Assert.Equal("JOIN Loan AS l ON l.CopyNo = c.CopyNo", Rendered(text));
            Assert.Single(text.Inlines);
        });
    }

    [Fact]
    public void 名稱命中的高亮平移到限定名稱上而資料行取最後一段()
    {
        var table = new SqlSearchRow(Hit(SearchMatchTarget.Name, "[dbo].[Loan]", "Loan", new MatchSpan(0, 4)), "Table");
        Assert.Equal(new[] { new MatchSpan(7, 4) }, table.TitleSpans.ToArray());

        var column = new SqlSearchRow(
            Hit(SearchMatchTarget.Column, "[dbo].[Cat_BookCopy].[CopyNo]", "CopyNo", new MatchSpan(0, 6)), "Column");
        Assert.Equal(new[] { new MatchSpan(22, 6) }, column.TitleSpans.ToArray());

        // 本文命中的片段來自定義本文，與標題沒有關係。
        var body = new SqlSearchRow(Hit(SearchMatchTarget.Text, "[dbo].[Loan]", "  JOIN Loan l", new MatchSpan(7, 4)), "Table");
        Assert.Empty(body.TitleSpans);
        Assert.Equal("JOIN Loan l", body.Snippet);
        Assert.Equal(new[] { new MatchSpan(5, 4) }, body.SnippetSpans.ToArray());
    }

    [Fact]
    public void 命中部位的用字在開關與列上完全相同()
    {
        // 兩邊各叫各的，使用者會以為它們是兩件事。
        var row = new SqlSearchRow(Hit(SearchMatchTarget.Text, "[dbo].[Loan]", "JOIN Loan l", new MatchSpan(5, 4)), "Table");
        Assert.Equal(SqlSearchTargets.LabelFor(SearchMatchTarget.Text), row.TargetLabel);
        Assert.Equal("Table · 內容 · " + row.Path, row.Description);
    }

    private static string Rendered(SqlHighlightText text) =>
        string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text));

    private static ObservableCollection<SqlSearchRow> Rows()
    {
        return new ObservableCollection<SqlSearchRow>
        {
            new(Hit(SearchMatchTarget.Name, "[dbo].[Loan]", "Loan", new MatchSpan(0, 4)), "Table"),
            new(Hit(SearchMatchTarget.Name, "[dbo].[LoanDetail]", "LoanDetail", new MatchSpan(0, 4)), "Table"),
            new(Hit(SearchMatchTarget.Text, "[dbo].[Cat_BookCopy]", "    JOIN Loan l ON l.CopyNo = c.CopyNo", new MatchSpan(9, 4)), "Procedure"),
        };
    }

    private static SearchHit Hit(SearchMatchTarget matchTarget, string title, string snippet, MatchSpan span)
    {
        var parts = title.Split('.').Select(part => part.Trim('[', ']')).ToArray();
        SqlObjectPath.TryParseName(parts, out var path);
        return new SearchHit("catalog", "catalog.table", matchTarget, title, title + matchTarget, 10,
            path, snippet, new[] { span }, badges: new[]
            {
                new SearchBadge("LIBSRV", SearchBadge.ServerIcon),
                new SearchBadge("LibArchive", SearchBadge.DatabaseIcon)
            });
    }

    [Fact]
    public void 過濾面板只建看得到的選項並保住已勾的條件()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var databases = new SqlSearchFilterButton("資料庫", SqlIcon.Database, filterable: true);
            var selected = new List<(string Name, bool On)>();
            var names = Enumerable.Range(1, 400).Select(index => "Lib_Reader" + index).ToArray();

            databases.SetOptions(new[]
            {
                new SqlSearchFilterGroup("使用者資料庫", names
                    .Select(name => new SqlSearchFilterOption(name, "", name == "Lib_Reader7", on => selected.Add((name, on))))
                    .ToArray()),
                // 一個都不相符的段落不畫標題；空標題掛在那裡會看起來像有內容卻少了幾列。
                new SqlSearchFilterGroup("系統資料庫", Array.Empty<SqlSearchFilterOption>())
            });

            // 面板的內容不在宿主的視覺樹上，主題資源由宿主套一次；量測也得自己來。
            var surface = databases.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            surface.Measure(new Size(320, double.PositiveInfinity));
            surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
            surface.UpdateLayout();

            var boxes = Descendants<CheckBox>(surface).ToArray();
            // 400 個名稱只建得出面板裝得下的那十幾顆；捲軸與已勾的狀態都還在。
            Assert.InRange(boxes.Length, 1, 40);
            Assert.Contains(boxes, box => Equals(box.Content, "Lib_Reader1"));
            Assert.DoesNotContain(boxes, box => Equals(box.Content, "Lib_Reader400"));
            Assert.Single(Descendants<TextBlock>(surface), text => text.Text == "使用者資料庫");
            Assert.DoesNotContain(Descendants<TextBlock>(surface), text => text.Text == "系統資料庫");

            var seven = boxes.Single(box => Equals(box.Content, "Lib_Reader7"));
            Assert.True(seven.IsChecked);
            Assert.Empty(selected);

            // 面板不在宿主的視覺樹上，晚一步才建出來的列仍要讀得到宿主套上的那一份筆刷。
            Assert.All(boxes, box => Assert.Same(palette.Resources[ThemeBrush.ListForeground], box.Foreground));

            // 勾一顆就是一次使用者動作；回收容器換 DataContext 不算，所以下面重填不該再記一筆。
            boxes.Single(box => Equals(box.Content, "Lib_Reader1")).IsChecked = true;
            Assert.Equal(new[] { ("Lib_Reader1", true) }, selected);

            databases.SetOptions(new[]
            {
                new SqlSearchFilterGroup("使用者資料庫", names
                    .Select(name => new SqlSearchFilterOption(name, "", name is "Lib_Reader1" or "Lib_Reader7", _ => { }))
                    .ToArray())
            });
            surface.UpdateLayout();
            Assert.Single(selected);
            Assert.True(Descendants<CheckBox>(surface).Single(box => Equals(box.Content, "Lib_Reader1")).IsChecked);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
