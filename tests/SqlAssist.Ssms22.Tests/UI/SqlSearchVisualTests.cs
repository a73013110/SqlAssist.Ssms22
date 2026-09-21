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
using System.Windows.Media.Animation;
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

    /// <summary>
    /// 那一列的規範：框裡是修飾搜尋字串的直接控制，框外右緣是作用在這一份結果的操作。
    /// </summary>
    /// <remarks>
    /// 輸入框吃剩餘寬度而不是平分：讓得起的只有它，右邊那幾顆是固定寬的。收起的那一顆
    /// 連它前面那一段間距一起讓開，否則 SQL Memory 切到用量分頁時右邊會留一塊空白。
    /// </remarks>
    [Fact]
    public void 輸入列讓輸入框吃剩餘寬度而收起的操作連間距一起讓開()
    {
        WpfTest.Run(() =>
        {
            var input = SqlAssistChrome.CreateInputBar(
                SqlIcon.Search,
                SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics),
                SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋"));
            var connection = SqlAssistChrome.CreateMemoryConnectionButton();
            var refresh = SqlAssistChrome.CreateIconButton(SqlIcon.Refresh, "重新整理");
            var row = new SqlInputRow(input, connection, refresh);
            var host = new Border { Child = row };

            void Layout(double width)
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
            }

            Layout(400);
            var tail = connection.ActualWidth + refresh.ActualWidth;
            Assert.InRange(input.ActualWidth, 400 - tail - 20, 400 - tail - 8);
            // 每一顆都在輸入框右邊，而且排得進這一列。
            Assert.InRange(connection.TranslatePoint(new Point(), host).X, input.ActualWidth, 400);
            Assert.InRange(refresh.TranslatePoint(new Point(refresh.ActualWidth, 0), host).X, 0, 400.5);
            // 同一條視覺中心線；高度不同的控制項不各自貼著上緣。
            Assert.Equal(
                input.TranslatePoint(new Point(0, input.ActualHeight / 2), host).Y,
                refresh.TranslatePoint(new Point(0, refresh.ActualHeight / 2), host).Y,
                1);

            var narrowInput = input.ActualWidth;
            var refreshEdge = refresh.TranslatePoint(new Point(), host).X;
            var hidden = connection.ActualWidth;
            connection.Visibility = Visibility.Collapsed;
            row.InvalidateMeasure();
            Layout(400);
            // 收起的那一顆連它前面那一段間距一起還給輸入框；只跳過元素的話這裡會少 4 DIP，
            // 而右緣那一顆仍然貼著同一個位置。
            Assert.Equal(narrowInput + hidden + 4, input.ActualWidth, 1);
            Assert.Equal(refreshEdge, refresh.TranslatePoint(new Point(), host).X, 1);

            // 再窄也保留打得下字的欄位；右邊那幾顆不被擠掉。
            Layout(160);
            Assert.InRange(input.ActualWidth, SqlInputRow.MinInputWidth, 160);
        });
    }

    /// <summary>
    /// 搜尋框裡那兩顆開關「開著」時看得出來：強調底與強調框，不是與底色同色的一圈外框。
    /// </summary>
    /// <remarks>
    /// 它們不上已選條件列，所以這一顆本身就是唯一的呈現；關著與開著只差一條髮絲線的那一版
    /// 等於沒有狀態。
    /// </remarks>
    [Fact]
    public void 搜尋框裡的開關開著時用強調色而不是與搜尋框同底的外框()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var toggle = SqlAssistChrome.CreateSearchToggle(SqlIcon.MatchCase, "大小寫", "只取大小寫完全相同的本文命中。");
            var bar = SqlAssistChrome.CreateInputBar(
                SqlIcon.Search, SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics),
                SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋"), toggle);
            var host = new Border { Child = bar };
            host.Resources.MergedDictionaries.Add(palette.Resources);

            foreach (var mode in new[] { "light", "dark", "high-contrast" })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                host.Measure(new Size(300, 60)); host.Arrange(new Rect(0, 0, 300, 60)); host.UpdateLayout();

                var box = (Border)toggle.Template.FindName("toggle", toggle);
                Assert.Same(Brushes.Transparent, box.Background);

                toggle.IsChecked = true;
                host.UpdateLayout();
                Assert.Same(palette.Resources[ThemeBrush.AccentBackground], box.Background);
                Assert.Same(palette.Resources[ThemeBrush.AccentBorder], box.BorderBrush);
                // 底色與搜尋框自己的底不是同一個，否則開著與關著在畫面上讀不出差別；
                // 高對比沒有底色可分，改由強調框負責，而它上面已經驗過了。
                if (mode != "high-contrast")
                {
                    Assert.NotEqual(
                        ((SolidColorBrush)bar.Background).Color, ((SolidColorBrush)box.Background).Color);
                }

                toggle.IsChecked = false;
                host.UpdateLayout();
                Assert.Same(Brushes.Transparent, box.Background);
            }
        });
    }

    [Fact]
    public void 工具列兩層且窄窗先收字再依群組換行()
    {
        WpfTest.Run(() =>
        {
            var search = SqlAssistChrome.CreateInputBar(
                SqlIcon.Search,
                SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics),
                SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋"));
            var segments = new SqlSearchSegments();
            var kinds = new SqlFilterFlyout("種類", SqlIcon.Filter);
            var databases = new SqlFilterFlyout("資料庫", SqlIcon.Database, SqlFilterMode.SearchableMultiple);
            var sort = SqlAssistChrome.CreateIconButton(SqlIcon.SortDescending, "排序");
            var refresh = SqlAssistChrome.CreateIconButton(SqlIcon.Refresh, "重新整理");
            var toolbar = new SqlSearchToolbar(
                search, segments, new[] { new[] { databases }, new[] { kinds } }, sort, refresh);

            var host = new Border { Child = toolbar };

            Size Layout(double width)
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
                return host.DesiredSize;
            }

            // 同一列的控制項高度不同（圖示鈕比搜尋框矮），比的是視覺中心線而不是上緣。
            double Middle(FrameworkElement element) =>
                element.TranslatePoint(new Point(0, element.ActualHeight / 2), toolbar).Y;

            var wide = Layout(720);
            Assert.Equal(SqlSearchToolbarMode.Full, toolbar.Mode);
            Assert.False(kinds.IsCompact);

            // 第一層是搜尋框與排序／重新整理；filters 與分段開關在第二層。
            Assert.InRange(Math.Abs(Middle(sort) - Middle(search)), 0, 1);
            Assert.InRange(Math.Abs(Middle(refresh) - Middle(search)), 0, 1);
            Assert.True(Middle(databases) > Middle(search) + search.ActualHeight / 2);
            Assert.InRange(Math.Abs(Middle(segments) - Middle(databases)), 0, 1);
            // 搜尋框吃滿第一層剩下的寬度：右邊只留那兩顆圖示鈕。
            Assert.True(search.ActualWidth > 720 - sort.DesiredSize.Width - refresh.DesiredSize.Width - 40);

            // 第二層放不下帶字的過濾按鈕就先收字；不換行，所以高度不變。
            // 門檻是量出來的內容寬度：差一個 DIP 就該換一級，不必寫死一個數字。
            // 用 DesiredSize 而不是 ActualWidth：工具列量的是含外距的那一份，兩者差幾個 DIP
            // 就足以讓門檻算在錯的一級上。
            // 群距由共用的分隔線決定（它自己帶左右間距），不是寫死的兩個 ItemGap。
            var gap = Measured(SqlAssistChrome.CreateFilterGroupDivider()).Width * 2;
            var second = databases.DesiredSize.Width + kinds.DesiredSize.Width + segments.DesiredSize.Width + gap;
            var compact = Layout(second - 1);
            Assert.Equal(SqlSearchToolbarMode.Compact, toolbar.Mode);
            Assert.True(kinds.IsCompact);
            Assert.Equal(wide.Height, compact.Height);
            Assert.InRange(Math.Abs(Middle(segments) - Middle(databases)), 0, 1);

            // 再窄就整群換行：分段開關是常駐可見的，而它與過濾按鈕不會拆成一半一半。
            var wrapped = Layout(
                databases.DesiredSize.Width + kinds.DesiredSize.Width + segments.DesiredSize.Width + gap - 1);
            Assert.Equal(SqlSearchToolbarMode.Wrapped, toolbar.Mode);
            Assert.True(wrapped.Height > compact.Height);
            Assert.True(Middle(segments) > Middle(databases) + databases.ActualHeight / 2);
            // 換行的單位是群：兩顆過濾按鈕仍在同一列，不會掉一顆下去。
            Assert.InRange(Math.Abs(Middle(kinds) - Middle(databases)), 0, 1);

            // 拉回去要回到完整版，不停在窄版上。
            Layout(720);
            Assert.Equal(SqlSearchToolbarMode.Full, toolbar.Mode);
            Assert.False(kinds.IsCompact);
            Assert.InRange(Math.Abs(Middle(segments) - Middle(databases)), 0, 1);
        });
    }

    [Fact]
    public void 主從區寬到門檻才轉成左右()
    {
        WpfTest.Run(() =>
        {
            var master = new Border { MinHeight = 40 };
            var detail = new Border { MinHeight = 40 };
            var responsive = new MasterDetailView(master, detail, sideBySideWidth: 520);
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

            // 不傳門檻的主從區永遠上下分割；SQL Memory 跟 SQL Search 一樣會傳門檻，這裡只驗「沒傳就不轉向」。
            var fixedSplit = new MasterDetailView(new Border(), new Border());
            var fixedHost = new Border { Child = fixedSplit };
            fixedHost.Measure(new Size(1200, 400));
            fixedHost.Arrange(new Rect(0, 0, 1200, 400));
            fixedHost.UpdateLayout();
            Assert.False(fixedSplit.IsSideBySide);
        });
    }

    [Fact]
    public void 已選條件列只在非預設時出現而且永遠只有一列()
    {
        WpfTest.Run(() =>
        {
            var chips = new SqlFilterChipBar();
            Assert.Equal(Visibility.Collapsed, chips.Visibility);

            var removed = new List<string>();
            var opened = new List<string>();
            chips.RemoveRequested += chip => removed.Add((string)chip);
            chips.OpenRequested += chip => opened.Add((string)chip);
            // 上這一列的都是清得掉、也開得了面板的維度；大小寫與全字是搜尋框裡常駐的開關，不上來。
            chips.SetChips(new[] { "伺服器: LIBSQL01", "資料庫: 8 個", "種類: 2 種" }, chip => chip);
            Assert.Equal(Visibility.Visible, chips.Visibility);

            var host = new Border { Child = chips };
            host.Measure(new Size(400, 100));
            host.Arrange(new Rect(0, 0, 400, 100));
            host.UpdateLayout();

            // 每一顆都有本體與十字。
            var buttons = Descendants<Button>(chips).ToArray();
            Assert.Equal(6, buttons.Length);

            // 十字清掉整個維度；本體開的是那個維度自己的面板。
            buttons.Single(button => (string)button.ToolTip == "清除條件：資料庫: 8 個")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "資料庫: 8 個" }, removed.ToArray());
            buttons.Single(button => (string)button.ToolTip == "種類: 2 種：開啟面板調整")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "種類: 2 種" }, opened.ToArray());

            // 條件再多也只有一列：放不下時橫向捲動，不換行。
            var strip = Descendants<StackPanel>(chips).First(panel => panel.Orientation == Orientation.Horizontal);
            var single = strip.DesiredSize.Height;
            chips.SetChips(
                new[] { "伺服器: LIBSQL01.分公司.內部網路", "資料庫: 12 個", "種類: 6 種" },
                chip => chip);
            host.Measure(new Size(200, 100));
            host.Arrange(new Rect(0, 0, 200, 100));
            host.UpdateLayout();
            Assert.Equal(single, strip.DesiredSize.Height, 1);
            Assert.InRange(chips.DesiredSize.Height, 0, single + 12);

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
            var toggle = (ControlTemplate)SqlAssistChrome.CreateInputToggleStyle().Setters
                .OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty).Value;

            foreach (var template in new[] { card, option, segment, toggle })
            foreach (var trigger in template.Triggers.OfType<Trigger>())
            {
                Assert.DoesNotContain(trigger.Setters.OfType<Setter>(), setter => layoutProperties.Contains(setter.Property));
            }

            // 結果沒有刪除動作；留著 IsRemoving 的繫結只會在每一列上找一個不存在的屬性。
            Assert.DoesNotContain(card.Triggers.OfType<DataTrigger>(),
                trigger => (trigger.Binding as Binding)?.Path.Path == "IsRemoving");

            // 停駐才出現的操作層浮在列的右緣，不參與量測，所以收起來是 Collapsed 而不是
            // 佔著寬度的 Hidden；揭露與收起都不動版面。
            var hit = SqlAssistChrome.CreateSearchHitTemplate();
            var reveal = hit.Triggers.OfType<DataTrigger>()
                .Where(trigger => trigger.Setters.OfType<Setter>().Any(setter => setter.TargetName == "actions"))
                .ToArray();
            Assert.Equal(2, reveal.Length);
            Assert.All(reveal, trigger => Assert.Equal(
                Visibility.Visible, trigger.Setters.OfType<Setter>().Single().Value));

            // 操作層的底色跟著列的停駐與選取走；不跟著的話右邊會浮出一塊沒有染色的方塊。
            var tint = hit.Triggers.OfType<DataTrigger>()
                .Where(trigger => trigger.Setters.OfType<Setter>().Any(setter => setter.TargetName == "actionsTint"))
                .ToArray();
            Assert.Equal(2, tint.Length);
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

    [Fact]
    public void 結果列第一列以名稱開頭而片段只在本文命中時出現()
    {
        WpfTest.Run(() =>
        {
            // 樣板在 STA 執行緒上建立；先建好再交給另一條執行緒套用會在 Seal 擋下來。
            var template = SqlAssistChrome.CreateSearchHitTemplate();
            // 第一列：物件名稱 → 物件類型 → 命中部位 → 彈性空白 → 伺服器 → 資料庫，操作浮在右緣。
            var wide = Render(template, new SqlSearchRow(
                Hit(SearchMatchTarget.Name, "[dbo].[Loan]", "Loan", new MatchSpan(0, 4)), "Table"), 740);
            var order = new[] { "name", "kind", "target", "badges" };
            var lefts = order.Select(part => Left(wide, part)).ToArray();
            for (var index = 1; index < order.Length; index++)
                Assert.True(lefts[index] > lefts[index - 1], order[index] + " 應該排在 " + order[index - 1] + " 右邊");
            // 名稱固定最左：圖示排在它前面，每一列要掃的那一欄就從不同的位置開始。
            Assert.Equal(0, Left(wide, "name"), 1);
            var center = Center(wide, "name");
            Assert.All(order, part => Assert.InRange(Center(wide, part) - center, -0.6, 0.6));
            Assert.True(Right(wide, "target") + 8 < Left(wide, "badges"));
            // 兩顆連線膠囊在第一列上，不再自己占一行的右半；操作層不佔寬度，所以它們排到最右。
            Assert.Equal(2, Descendants<SqlIconImage>(Part(wide, "badges")).Count());
            Assert.InRange(740 - Right(wide, "badges"), 0, 8);
            Assert.Equal(Visibility.Collapsed, Part(wide, "actions").Visibility);
            // 物件類型是看得見的 icon＋文字，不是只有形狀加 Tooltip。
            var kind = Part(wide, "kind");
            Assert.Equal("Table", Descendants<TextBlock>(kind).Single().Text);
            Assert.Equal("Table", (string)kind.ToolTip);
            Assert.Equal(Visibility.Collapsed, Part(wide, "overflow").Visibility);

            // 片段列只在本文命中時出現：名稱命中的片段就是名稱本體，不壓成固定兩列。
            Assert.Equal(Visibility.Collapsed, Part(wide, "code").Visibility);
            Assert.Equal(Visibility.Collapsed, Part(Render(template, new SqlSearchRow(
                Hit(SearchMatchTarget.Column, "[dbo].[Cat_BookCopy].[CopyNo]", "CopyNo", new MatchSpan(0, 6)),
                "Column"), 740), "code").Visibility);
            Assert.Equal(Visibility.Visible, Part(Render(template, new SqlSearchRow(
                Hit(SearchMatchTarget.Text, "[dbo].[Cat_BookCopy]", "    JOIN Loan l", new MatchSpan(9, 4)),
                "Procedure"), 740), "code").Visibility);

            // 工具窗停在右側時整列約 300 DIP；長名稱先省略中段，但仍從最左讀得到。
            var narrow = Render(template, new SqlSearchRow(Hit(SearchMatchTarget.Name,
                "[dbo].[" + new string('L', 60) + "]", "Loan", new MatchSpan(0, 4)), "Table"), 300);
            var name = Part(narrow, "name");
            Assert.Equal(0, Left(narrow, "name"), 1);
            Assert.InRange(name.ActualWidth, 1, SqlAssistChrome.RowNameMaxWidth);
            Assert.Equal(TextTrimming.CharacterEllipsis, ((TextBlock)name).TextTrimming);
            Assert.InRange(Right(narrow, "target"), 0, 300);
            // 窄版降級：連線膠囊只剩圖示，次要操作收進 overflow；物件類型是高優先，文字留著。
            Assert.Equal(Visibility.Visible, Part(narrow, "kindText").Visibility);
            Assert.Equal(Visibility.Visible, Part(narrow, "overflow").Visibility);
            Assert.Equal(Visibility.Visible, Part(narrow, "action" + SqlSearchRowAction.Activate).Visibility);
            Assert.Equal(Visibility.Collapsed, Part(narrow, "action" + SqlSearchRowAction.Copy).Visibility);
            Assert.All(Descendants<TextBlock>(Part(narrow, "badges")),
                text => Assert.Equal(Visibility.Collapsed, text.Visibility));
            // 每一組都仍在這一列的範圍內，沒有被推出去。
            foreach (var part in new[] { "name", "kind", "target", "badges" })
                Assert.InRange(Right(narrow, part), 0, 300);

            // 沒有路徑概念的來源不留一條空白列。
            var pathless = Render(template, new SqlSearchRow(new SearchHit("snippets", "snippets.snippet",
                SearchMatchTarget.Name, "SelectTemplate", "SelectTemplate", 10, null, "", new[] { new MatchSpan(0, 6) }),
                "Snippet"), 740);
            Assert.Equal(Visibility.Collapsed, Part(pathless, "path").Visibility);
        });
    }

    /// <summary>
    /// 預覽資訊列與結果列第一列是同一個順序。
    /// </summary>
    /// <remarks>
    /// 兩個表面各排各的話，使用者在清單上選一筆、眼睛移到資訊列，同一組事實卻換了位置，
    /// 等於每一次都要重讀一遍。限定名稱排在最後，那是它比清單第一列多出來的東西。
    /// </remarks>
    [Fact]
    public void 預覽資訊列與結果列同一個順序而且缺值直接收起()
    {
        WpfTest.Run(() =>
        {
            var template = SqlAssistChrome.CreateSearchMetadataTemplate();
            var row = Render(template, new SqlSearchRow(
                Hit(SearchMatchTarget.Name, "[dbo].[Loan]", "Loan", new MatchSpan(0, 4)), "Table"), 740);

            var order = new[] { "name", "kind", "target", "badges", "path" };
            var lefts = order.Select(part => Left(row, part)).ToArray();
            for (var index = 1; index < order.Length; index++)
                Assert.True(lefts[index] > lefts[index - 1], order[index] + " 應該排在 " + order[index - 1] + " 右邊");
            Assert.All(order, part => Assert.True(Part(row, part).ActualWidth > 0, part));

            // 種類與命中部位在這裡只各出現一次；預覽內容裡不再放第二份同樣的字。
            Assert.Equal("Table", Descendants<TextBlock>(Part(row, "kind")).Single().Text);
            Assert.Equal("名稱", Descendants<TextBlock>(Part(row, "target")).Single().Text);

            var pathless = Render(template, new SqlSearchRow(new SearchHit("snippets", "snippets.snippet",
                SearchMatchTarget.Name, "SelectTemplate", "SelectTemplate", 10, null, "", new[] { new MatchSpan(0, 6) }),
                "Snippet"), 740);
            Assert.Equal(Visibility.Collapsed, Part(pathless, "path").Visibility);
        });
    }

    /// <summary>
    /// 停駐時的操作層要<b>蓋得住</b>底下那幾顆膠囊。
    /// </summary>
    /// <remarks>
    /// 遮罩用相對座標的那一版兩個停駐點落在 0 與 1，整片操作層由左到右從全透明升到不透明，
    /// 結果是整排圖示都半透明、連線膠囊一路透出來疊在上面——畫面上就是「功能與後面的東西糊在一起」。
    /// 絕對座標讓漸層只發生在左緣那一小段，其餘由 Pad 補成實心。
    /// </remarks>
    [Fact]
    public void 列操作層只有左緣淡出其餘不透明()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var rendered = Render(SqlAssistChrome.CreateSearchHitTemplate(motion: false), new SqlSearchRow(
                Hit(SearchMatchTarget.Name, "[dbo].[Loan]", "Loan", new MatchSpan(0, 4)), "Table"), 740, palette);
            var layer = Assert.IsType<Border>(Part(rendered, "actions"));

            var mask = Assert.IsType<LinearGradientBrush>(layer.OpacityMask);
            Assert.Equal(BrushMappingMode.Absolute, mask.MappingMode);
            Assert.Equal(new Point(0, 0), mask.StartPoint);
            // 淡出只發生在左緣那一小段；相對座標的那一版終點落在 x = 1，整片都是半透明的。
            Assert.InRange(mask.EndPoint.X, 8, 40);
            Assert.Equal(GradientSpreadMethod.Pad, mask.SpreadMethod);
            Assert.Equal(Colors.Transparent, mask.GradientStops[0].Color);
            Assert.Equal(Colors.Black, mask.GradientStops[mask.GradientStops.Count - 1].Color);
            Assert.True(mask.IsFrozen);

            // 遮罩補成實心也要有底色可蓋；透明的層等於沒有遮罩，底下的膠囊照樣透出來。
            foreach (var mode in new[] { "light", "dark", "high-contrast" })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                var fill = Assert.IsType<SolidColorBrush>(layer.Background);
                Assert.Equal(byte.MaxValue, fill.Color.A);
            }
        });
    }

    [Fact]
    public void 列操作揭露有淡入與滑入而動畫關掉時直接顯示()
    {
        WpfTest.Run(() =>
        {
            static DataTrigger[] Reveal(DataTemplate template) => template.Triggers.OfType<DataTrigger>()
                .Where(trigger => trigger.Setters.OfType<Setter>().Any(setter => setter.TargetName == "actions"))
                .ToArray();

            var animated = SqlAssistChrome.CreateSearchHitTemplate(motion: true);
            var reveal = Reveal(animated);
            // 滑鼠與鍵盤焦點兩條路都揭露，而且兩條都有動畫：只加一邊的話，只用鍵盤的人
            // 看到的是一整片直接閃出來的圖示。
            Assert.Equal(2, reveal.Length);
            Assert.All(reveal, trigger =>
            {
                var begin = Assert.IsType<BeginStoryboard>(Assert.Single(trigger.EnterActions));
                Assert.Equal(2, begin.Storyboard.Children.Count);
                Assert.Equal(FillBehavior.Stop, begin.Storyboard.FillBehavior);
                Assert.All(begin.Storyboard.Children,
                    animation => Assert.Equal(SqlAssistChrome.RowActionRevealDuration, animation.Duration.TimeSpan));
            });

            // 揭露動畫這一級不超過內容表面出現的長度。
            Assert.InRange(SqlAssistChrome.RowActionRevealDuration, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));

            // 關掉動畫是真的關掉：仍然揭露，只是不播。
            var still = Reveal(SqlAssistChrome.CreateSearchHitTemplate(motion: false));
            Assert.Equal(2, still.Length);
            Assert.All(still, trigger =>
            {
                Assert.Empty(trigger.EnterActions);
                Assert.Equal(Visibility.Visible, trigger.Setters.OfType<Setter>().Single().Value);
            });

            // SQL Memory 的卡片走同一份，不各寫一次。
            Assert.Equal(2, SqlAssistChrome.CreateSqlSummaryTemplate(motion: true).Triggers.OfType<DataTrigger>()
                .Count(trigger => trigger.EnterActions.Count == 1 &&
                                  trigger.Setters.OfType<Setter>().Any(setter => setter.TargetName == "actions")));
        });
    }

    /// <summary>
    /// 工具列第二層每一顆之間都有分隔線，群間那一條比群內的高一截、寬一截。
    /// </summary>
    /// <remarks>
    /// 全部畫成同一種的話，伺服器、資料庫與種類看起來是同一組可以互相取代的選項；
    /// 一條都不畫的話，窄窗收完字之後每一顆只剩一個圖示，界線更分不出來。
    /// 分段開關換到第三列時，它前面那一條會變成第三列開頭的一條孤線，所以整條收起；
    /// 列首（伺服器前面）那一條同理，一開始就不畫。
    /// </remarks>
    [Fact]
    public void 篩選之間都有分隔線而列首與換行的那一條收起()
    {
        WpfTest.Run(() =>
        {
            var search = SqlAssistChrome.CreateInputBar(
                SqlIcon.Search,
                SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics),
                SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋"));
            var segments = new SqlSearchSegments();
            var server = new SqlFilterFlyout("伺服器", SqlIcon.Server, SqlFilterMode.Single);
            var databases = new SqlFilterFlyout("資料庫", SqlIcon.Database, SqlFilterMode.SearchableMultiple);
            var kinds = new SqlFilterFlyout("種類", SqlIcon.Filter);
            var toolbar = new SqlSearchToolbar(
                search, segments, new[] { new[] { server, databases }, new[] { kinds } });
            var host = new Border { Child = toolbar };

            void Layout(double width)
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
            }

            Layout(900);
            Assert.Equal(SqlSearchToolbarMode.Full, toolbar.Mode);

            // 三條看得見：伺服器｜資料庫（群內）、資料庫｜種類、種類｜分段開關（群間）。
            // 列首那一條（伺服器前面）一開始就收起，不然第二列會從一條孤線開始。
            Border[] Dividers() => Descendants<Border>(toolbar)
                .Where(child => child.Width == 1)
                .OrderBy(child => child.TranslatePoint(new Point(), toolbar).X)
                .ToArray();

            var all = Dividers();
            Assert.Equal(4, all.Length);
            Assert.Equal(Visibility.Collapsed, all[0].Visibility);
            var dividers = all.Skip(1).ToArray();
            Assert.All(dividers, divider => Assert.Equal(Visibility.Visible, divider.Visibility));

            double Left(FrameworkElement element) => element.TranslatePoint(new Point(), toolbar).X;
            Assert.True(Left(server) < Left(dividers[0]));
            Assert.True(Left(dividers[0]) < Left(databases));
            Assert.True(Left(databases) < Left(dividers[1]));
            Assert.True(Left(dividers[1]) < Left(kinds));
            Assert.True(Left(kinds) < Left(dividers[2]));
            Assert.True(Left(dividers[2]) < Left(segments));

            // 群內那一條矮一截、間距也窄一截：兩個數字的差就是「這是兩群」。
            Assert.True(dividers[0].Height < dividers[1].Height);
            var inside = Left(databases) - (Left(server) + server.ActualWidth);
            var between = Left(kinds) - (Left(databases) + databases.ActualWidth);
            Assert.True(between > inside, "群間距要大於群內距");

            // 分隔線與按鈕同一條視覺中心線。
            double Middle(FrameworkElement element) =>
                element.TranslatePoint(new Point(0, element.ActualHeight / 2), toolbar).Y;
            Assert.All(dividers, divider => Assert.InRange(Math.Abs(Middle(divider) - Middle(kinds)), 0, 1));

            // 窄到分段開關換行時，它前面那一條收起；前面兩條留著。
            Layout(server.DesiredSize.Width + databases.DesiredSize.Width + kinds.DesiredSize.Width);
            Assert.Equal(SqlSearchToolbarMode.Wrapped, toolbar.Mode);
            Assert.Equal(Visibility.Visible, dividers[0].Visibility);
            Assert.Equal(Visibility.Visible, dividers[1].Visibility);
            Assert.Equal(Visibility.Collapsed, dividers[2].Visibility);

            Layout(900);
            Assert.Equal(SqlSearchToolbarMode.Full, toolbar.Mode);
            Assert.All(dividers, divider => Assert.Equal(Visibility.Visible, divider.Visibility));
        });
    }

    /// <summary>量一個還沒進版面的元素；分隔線的寬度含它自己的左右間距。</summary>
    private static Size Measured(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    /// <summary>把樣板套在固定寬度上；ContentControl 預設只給內容自己的寬度，量不到靠右那一組。</summary>
    /// <param name="palette">要驗動態筆刷時掛上的那一份；null 表示這一輪只看版面。</param>
    private static ContentControl Render(DataTemplate template, object row, double width, ThemeResourceSet? palette = null)
    {
        var host = new ContentControl
        {
            ContentTemplate = template, Content = row, Width = width,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        if (palette is not null) host.Resources.MergedDictionaries.Add(palette.Resources);
        // 寬度模式由宿主量：與清單走同一條路徑，測試才驗得到真正的門檻而不是自己設的旗標。
        SqlRowLayout.Track(host);
        host.Measure(new Size(width, 400)); host.Arrange(new Rect(0, 0, width, 400)); host.UpdateLayout();
        return host;
    }

    private static FrameworkElement Part(ContentControl host, string name)
    {
        var presenter = Descendants<ContentPresenter>(host).First();
        return Assert.IsAssignableFrom<FrameworkElement>(presenter.ContentTemplate.FindName(name, presenter));
    }

    private static double Left(ContentControl host, string name) =>
        Part(host, name).TranslatePoint(new Point(), host).X;

    private static double Right(ContentControl host, string name)
    {
        var part = Part(host, name);
        return part.TranslatePoint(new Point(part.ActualWidth, 0), host).X;
    }

    private static double Center(ContentControl host, string name)
    {
        var part = Part(host, name);
        return part.TranslatePoint(new Point(0, part.ActualHeight / 2), host).Y;
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

            var databases = new SqlFilterFlyout("資料庫", SqlIcon.Database, SqlFilterMode.SearchableMultiple);
            var selected = new List<(string Name, bool On)>();
            var names = Enumerable.Range(1, 400).Select(index => "Lib_Reader" + index).ToArray();

            databases.SetOptions(new[]
            {
                new SqlFilterGroup("使用者資料庫", names
                    .Select(name => new SqlFilterOption(name, "", name == "Lib_Reader7", on => selected.Add((name, on))))
                    .ToArray()),
                // 一個都不相符的段落不畫標題；空標題掛在那裡會看起來像有內容卻少了幾列。
                new SqlFilterGroup("系統資料庫", Array.Empty<SqlFilterOption>())
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
                new SqlFilterGroup("使用者資料庫", names
                    .Select(name => new SqlFilterOption(name, "", name is "Lib_Reader1" or "Lib_Reader7", _ => { }))
                    .ToArray())
            });
            surface.UpdateLayout();
            Assert.Single(selected);
            Assert.True(Descendants<CheckBox>(surface).Single(box => Equals(box.Content, "Lib_Reader1")).IsChecked);
        });
    }

    [Fact]
    public void 單選面板用radio選完關閉且不顯示全選與清除()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var server = new SqlFilterFlyout("伺服器", SqlIcon.Server, SqlFilterMode.Single);
            var picked = new List<(string Name, bool On)>();
            var names = new[] { "跟著查詢視窗", "LIBSRV", "LIBARCHIVE" };

            void Fill(string selected) => server.SetOptions(new[]
            {
                new SqlFilterGroup("", names
                    .Select(name => new SqlFilterOption(name, "", name == selected, on => picked.Add((name, on))))
                    .ToArray())
            });

            Fill("跟著查詢視窗");

            var surface = server.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            surface.Measure(new Size(320, double.PositiveInfinity));
            surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
            surface.UpdateLayout();

            // 形狀就是語意：單選畫 radio，不畫勾選框。
            Assert.Empty(Descendants<CheckBox>(surface));
            var radios = Descendants<RadioButton>(surface).ToArray();
            Assert.Equal(names, radios.Select(radio => (string)radio.Content).ToArray());
            Assert.True(radios[0].IsChecked);

            // 全選對互斥的選項沒有意義，清除等於一個範圍都不選；兩顆都不畫。
            var labels = Descendants<TextBlock>(surface).Select(text => text.Text).ToArray();
            Assert.DoesNotContain("全選", labels);
            Assert.DoesNotContain("清除", labels);

            // 每一列各自一個群組名：互斥交給模型，WPF 不自動去取消上一個。
            Assert.Equal(radios.Length, radios.Select(radio => radio.GroupName).Distinct().Count());

            server.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(server.IsOpen);

            radios[1].IsChecked = true;
            // 只回報使用者選上的那一個；自動互斥會多送一筆上一個選項的取消，而那會把剛換好的範圍清掉。
            Assert.Equal(new[] { ("LIBSRV", true) }, picked);
            // 單選一次只改得了一項：選完就關，不要使用者再按一次外面。
            Assert.False(server.IsOpen);
        });
    }

    [Fact]
    public void 複選面板選完不關且勾得起好幾個()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var kinds = new SqlFilterFlyout("種類", SqlIcon.Filter);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var names = new[] { "Table", "View", "Procedure" };

            kinds.SetOptions(new[]
            {
                new SqlFilterGroup("", names
                    .Select(name => new SqlFilterOption(name, "", selected.Contains(name), on =>
                    {
                        if (on) selected.Add(name);
                        else selected.Remove(name);
                    }))
                    .ToArray())
            });

            var surface = kinds.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            surface.Measure(new Size(320, double.PositiveInfinity));
            surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
            surface.UpdateLayout();

            Assert.Empty(Descendants<RadioButton>(surface));
            var boxes = Descendants<CheckBox>(surface).ToArray();
            Assert.Equal(names, boxes.Select(box => (string)box.Content).ToArray());

            // 種類面板沒有命令鈕：「全部」是第一列那個預設，而全選會送出一份結果相同、
            // chip 卻完全不同的條件。
            var labels = Descendants<TextBlock>(surface).Select(text => text.Text).ToArray();
            Assert.DoesNotContain("全選", labels);
            Assert.DoesNotContain("清除", labels);

            kinds.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(kinds.IsOpen);

            boxes[0].IsChecked = true;
            boxes[2].IsChecked = true;
            // 複選是「連勾好幾個」，面板關掉的話每一個條件都要重開一次。
            Assert.True(kinds.IsOpen);
            Assert.Equal(new[] { "Procedure", "Table" }, selected.OrderBy(name => name, StringComparer.Ordinal).ToArray());

            boxes[0].IsChecked = false;
            Assert.Equal(new[] { "Procedure" }, selected.ToArray());
            Assert.True(kinds.IsOpen);
        });
    }

    /// <summary>
    /// 「沒有勾任何一個」是一個實際的預設，所以面板第一列就是它。
    /// </summary>
    /// <remarks>
    /// 摘要寫著「全部」而清單上一個勾都沒有時，使用者會以為自己把條件弄丟了，
    /// 或以為這個下拉壞了。取消勾它不是一個狀態，所以它按得上去、取消不掉。
    /// </remarks>
    [Fact]
    public void 過濾面板第一列是沒有指名時的那個預設()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var kinds = new SqlFilterFlyout("種類", SqlIcon.Filter);
            var cleared = 0;
            kinds.SetEmptyOption(new SqlFilterOption("全部", "不限物件種類。", true, on =>
            {
                if (on) cleared++;
            }));
            kinds.SetOptions(new[]
            {
                new SqlFilterGroup("", new[]
                {
                    new SqlFilterOption("Table", "", false, _ => { })
                })
            });

            var surface = kinds.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            surface.Measure(new Size(320, double.PositiveInfinity));
            surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
            surface.UpdateLayout();

            var boxes = Descendants<CheckBox>(surface).ToArray();
            Assert.Equal(new[] { "全部", "Table" }, boxes.Select(box => (string)box.Content).ToArray());
            Assert.True(boxes[0].IsChecked);

            // 取消勾「全部」不是使用者做得到的狀態：勾選框彈起來了，要把它按回去，
            // 否則畫面上這個維度看起來沒有條件，而實際上也真的沒有。
            boxes[0].IsChecked = false;
            surface.UpdateLayout();
            Assert.True(boxes[0].IsChecked);
            Assert.Equal(0, cleared);

            // 別的選項變動之後由宿主改這一列，不重建整份清單：使用者可能正在連勾好幾個。
            kinds.SyncEmptyOption(false);
            surface.UpdateLayout();
            Assert.False(boxes[0].IsChecked);
            Assert.Equal(0, cleared);

            boxes[0].IsChecked = true;
            Assert.Equal(1, cleared);
        });
    }

    /// <summary>
    /// 清單還在路上時面板說一句，而不是先給一份空的。
    /// </summary>
    /// <remarks>
    /// 空面板與「這台伺服器上一個都沒有」在畫面上一模一樣，使用者會關掉它去別的地方找。
    /// 轉圈只在忙碌時跑：停靠面板裡的下拉關掉之後仍留在視覺樹上，少了那一道就是一個看不見的
    /// 圈永遠佔著算繪。
    /// </remarks>
    [Fact]
    public void 面板在等清單時先說一句而且只有這時候才轉()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var databases = new SqlFilterFlyout("資料庫", SqlIcon.Database, SqlFilterMode.SearchableMultiple);
            var surface = databases.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);

            void Layout()
            {
                surface.Measure(new Size(320, double.PositiveInfinity));
                surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
                surface.UpdateLayout();
            }

            databases.SetOptions(Array.Empty<SqlFilterGroup>());
            Layout();
            var notice = Assert.Single(Descendants<SqlBusyNotice>(surface));
            Assert.Equal(Visibility.Collapsed, notice.Visibility);

            databases.SetNotice("正在讀取資料庫清單…", busy: true);
            Layout();
            Assert.Equal(Visibility.Visible, notice.Visibility);
            Assert.Contains(Descendants<TextBlock>(surface), text => text.Text == "正在讀取資料庫清單…");
            var arc = Assert.Single(Descendants<System.Windows.Shapes.Path>(notice));
            Assert.Equal(Visibility.Visible, arc.Visibility);
            var rotation = Assert.IsType<RotateTransform>(arc.RenderTransform);

            // 問不到是一句答案而不是一個等待：字留著，圈收起來。
            databases.SetNotice("問不到資料庫清單；仍搜得到目前連線的那一個。");
            Layout();
            Assert.Equal(Visibility.Collapsed, arc.Visibility);
            Assert.False(rotation.HasAnimatedProperties);

            databases.SetNotice("");
            Layout();
            Assert.Equal(Visibility.Collapsed, notice.Visibility);
        });
    }

    /// <summary>
    /// 續頁鈕在清單外面，按下去只問下一頁，不從第一頁重來。
    /// </summary>
    /// <remarks>
    /// 面板第一列那個預設與已經勾好的條件都在清單裡；把續頁併進
    /// <c>OptionsRequested</c> 的那一版會讓按下更多之後整份重建，使用者剛捲到的位置與
    /// 正在走的鍵盤焦點一起沒了。字與「還有沒有下一頁」都由宿主給：一頁幾筆是儲存層的契約，
    /// 面板不認得它。
    /// </remarks>
    [Fact]
    public void 續頁鈕浮在清單外面而且只問下一頁()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var names = new SqlFilterFlyout("名稱", SqlIcon.Server, SqlFilterMode.Single);
            var refilled = 0;
            var more = 0;
            names.OptionsRequested += (_, _) => refilled++;
            names.MoreRequested += (_, _) => more++;

            void Fill(int count) => names.SetOptions(new[]
            {
                new SqlFilterGroup("", Enumerable.Range(1, count)
                    .Select(index => new SqlFilterOption("Node" + index, "", false, _ => { }))
                    .ToArray())
            });

            Fill(2);

            var surface = names.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            void Layout()
            {
                surface.Measure(new Size(320, double.PositiveInfinity));
                surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
                surface.UpdateLayout();
            }
            Layout();

            // 沒有人說還有下一頁時整顆收起；空面板底下不留一條沒有作用的字。
            Assert.DoesNotContain(Descendants<TextBlock>(surface), text => text.Text == "更多名稱");

            names.SetMore("更多名稱");
            Layout();
            var button = Descendants<Button>(surface).Single(
                candidate => System.Windows.Automation.AutomationProperties.GetName(candidate) == "更多名稱");

            // 在清單外面：清單捲到底才看得到的那一版，正好在使用者需要它的時候看不見。
            var list = Descendants<ItemsControl>(surface).Single(candidate => candidate.ItemsSource is not null);
            Assert.Null(Descendants<Button>(list).FirstOrDefault(
                candidate => ReferenceEquals(candidate, button)));

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, more);
            // 續頁不是「這份清單從頭來」：宿主接在後面，面板不重問整份。
            Assert.Equal(0, refilled);

            Fill(4);
            names.SetMore(null);
            Layout();
            Assert.Equal(4, Descendants<RadioButton>(surface).Count());
            Assert.Equal(Visibility.Collapsed, button.Visibility);
        });
    }

    /// <summary>
    /// 排序入口在面板裡，按鈕與選單讀同一份選項，而且不就地改狀態。
    /// </summary>
    /// <remarks>
    /// 面板先亮起新的排序、宿主那一輪卻失敗或被新的一輪取代時，使用者看不出是哪一邊錯了。
    /// 選單是自己的一個 popup，它開起來時這個面板會被當成「按到外面」——不擋的症狀是
    /// 按下排序的那一刻清單就不見了，而使用者按它正是為了看重排之後的那一份。
    /// </remarks>
    [Fact]
    public void 排序入口在面板裡而且換排序要由宿主寫回來()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var sorts = new[]
            {
                new SqlFilterSortOption("recent", "最近使用優先", "最近", SqlIcon.SortDescending),
                new SqlFilterSortOption("name", "名稱 A–Z", "A–Z", SqlIcon.SortAscending)
            };
            var asked = new List<object>();
            var names = new SqlFilterFlyout("名稱", SqlIcon.Server, SqlFilterMode.Single);
            names.SortRequested += value => asked.Add(value);

            var surface = names.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            void Layout()
            {
                surface.Measure(new Size(320, double.PositiveInfinity));
                surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
                surface.UpdateLayout();
            }
            Layout();

            var button = Descendants<Button>(surface).Single(
                candidate => System.Windows.Automation.AutomationProperties.GetName(candidate) == "名稱排序");
            // 沒有人給排序的面板不長出那一列；種類與資料庫那兩份不需要它。
            Assert.Equal(Visibility.Collapsed, button.Visibility);

            names.SetSortOptions(sorts, "recent");
            Layout();
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Contains(Descendants<TextBlock>(button), text => text.Text == "最近");
            Assert.Equal(sorts.Select(sort => sort.Label), names.SortMenu.Items.Cast<MenuItem>().Select(item => (string)item.Header));

            // 同一份選項只建一次選單：每次換排序都重建的話，正開著的那一份會被抽掉。
            var items = names.SortMenu.Items.Cast<MenuItem>().ToArray();
            names.SetSortOptions(sorts, "name");
            Layout();
            Assert.Equal(items, names.SortMenu.Items.Cast<MenuItem>().ToArray());
            Assert.Contains(Descendants<TextBlock>(button), text => text.Text == "A–Z");

            // 選單開著的期間面板不自己關；選單是另一個 popup，滑鼠按下去就落在這個面板外面。
            names.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(names.IsOpen);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(names.SortMenu.IsOpen);
            Assert.True(names.IsOpen);
            Assert.Equal(new[] { false, true }, items.Select(item => item.IsChecked));

            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            // 只問出去，不就地改：按鈕仍是宿主上一次寫回來的那一個。
            Assert.Equal(new object[] { "recent" }, asked);
            Assert.Contains(Descendants<TextBlock>(button), text => text.Text == "A–Z");

            names.SortMenu.IsOpen = false;
            names.SetSortOptions(sorts, "recent");
            Layout();
            Assert.Contains(Descendants<TextBlock>(button), text => text.Text == "最近");
        });
    }

    /// <summary>
    /// 命令鈕只有一顆，全勾之後換成取消全選；取消全選走的是第一列那個預設的清除路徑。
    /// </summary>
    /// <remarks>
    /// 全選之後只想留兩個的人，在沒有這一顆的那一版要自己取消掉幾十個勾，而唯一的出口是
    /// 第一列那個寫著「連線預設」的預設——它看起來不像取消全選。並排兩顆的那一版則總有一顆是灰的。
    /// </remarks>
    [Fact]
    public void 整批命令只有一顆而且字跟著狀態換()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));

            var databases = new SqlFilterFlyout("資料庫", SqlIcon.Database, SqlFilterMode.SearchableMultiple);
            var surface = databases.PopupSurface;
            surface.Resources.MergedDictionaries.Add(palette.Resources);
            var all = 0;
            var cleared = 0;
            databases.SelectAllRequested += (_, _) => all++;
            databases.ClearAllRequested += (_, _) => cleared++;

            void Layout()
            {
                surface.Measure(new Size(320, double.PositiveInfinity));
                surface.Arrange(new Rect(0, 0, 320, surface.DesiredSize.Height));
                surface.UpdateLayout();
            }

            Button? Command(string label) => Descendants<Button>(surface)
                .FirstOrDefault(button => Descendants<TextBlock>(button).Any(text => text.Text == label));

            // 沒有人要的面板不留那一列；種類那一顆就是這樣。
            Layout();
            Assert.Null(Command("全選"));
            Assert.Null(Command("取消全選"));

            databases.SetBulkSelection(SqlFilterBulkSelection.SelectAll);
            Layout();
            var bulk = Command("全選");
            Assert.NotNull(bulk);
            bulk!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal((1, 0), (all, cleared));

            // 換狀態只換字，不重建按鈕：重建的那一版會在使用者按下去的那一刻把按鈕抽掉。
            databases.SetBulkSelection(SqlFilterBulkSelection.ClearAll);
            Layout();
            Assert.Same(bulk, Command("取消全選"));
            Assert.Null(Command("全選"));
            bulk.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal((1, 1), (all, cleared));

            databases.SetBulkSelection(SqlFilterBulkSelection.None);
            Layout();
            Assert.Equal(Visibility.Collapsed, bulk.Visibility);

            // 單選不畫這一顆：全選對互斥的選項沒有意義。
            var server = new SqlFilterFlyout("伺服器", SqlIcon.Server, SqlFilterMode.Single);
            server.PopupSurface.Resources.MergedDictionaries.Add(palette.Resources);
            server.SetBulkSelection(SqlFilterBulkSelection.SelectAll);
            server.PopupSurface.Measure(new Size(320, double.PositiveInfinity));
            server.PopupSurface.Arrange(new Rect(0, 0, 320, server.PopupSurface.DesiredSize.Height));
            Assert.DoesNotContain(Descendants<TextBlock>(server.PopupSurface), text => text.Text == "全選");
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
