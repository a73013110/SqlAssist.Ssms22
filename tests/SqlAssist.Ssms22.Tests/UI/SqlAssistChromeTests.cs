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
    [Theory]
    [InlineData("刪除")]
    [InlineData("停用")]
    [InlineData("還原預設")]
    public void ConfirmationDefaultsToCancelAndKeepsActionsOutsideScrollableContent(string action)
    {
        WpfTest.Run(() =>
        {
            var message = "要移除「Loan_" + new string('x', 2000) + "」嗎？";
            var content = SqlAssistChrome.CreateConfirmationContent(
                message, "按「儲存」後才會寫回檔案。", action, out var confirm, out var cancel);
            Assert.Equal(action, confirm.Content);
            Assert.False(confirm.IsDefault);
            Assert.False(confirm.IsCancel);
            Assert.True(cancel.IsDefault);
            Assert.True(cancel.IsCancel);
            Assert.Same(cancel, System.Windows.Input.FocusManager.GetFocusedElement(content));
            Assert.Equal(SqlAssistChrome.DefaultMetrics.Body, confirm.FontSize);
            Assert.Equal(confirm.FontSize, cancel.FontSize);
            Assert.Equal(confirm.MinWidth, cancel.MinWidth);

            var scroll = Assert.IsType<ScrollViewer>(content.Children[0]);
            var body = Assert.IsType<StackPanel>(scroll.Content);
            var text = Assert.IsType<TextBlock>(body.Children[0]);
            var footer = Assert.IsType<DockPanel>(content.Children[1]);
            var actions = Assert.IsType<StackPanel>(footer.Children[0]);
            Assert.Equal(message, text.Text);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.Same(cancel, actions.Children[0]);
            Assert.Same(confirm, actions.Children[1]);
            Assert.Equal(1, Grid.GetRow(footer));

            content.Measure(new Size(408, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.InRange(scroll.ActualHeight, 1, 240);
            Assert.True(footer.TranslatePoint(new Point(), content).Y >= scroll.ActualHeight);
            Assert.InRange(footer.ActualWidth, 1, content.ActualWidth);
            Assert.True(confirm.ActualHeight > 0);
            Assert.Equal(confirm.ActualHeight, cancel.ActualHeight);
        });
    }

    [Fact]
    public void OverlayScrollOverridesNativeScrollBarStyleAndDoesNotTakeLayoutWidth()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var colors = ThemePaletteTests.ColorsFor("plum");
            palette.Update(colors);
            var content = new StackPanel();
            for (var i = 0; i < 8; i++) content.Children.Add(new TextBlock { Text = "Lib_Reader" + i, Height = 20 });
            var scroll = new ScrollViewer { Content = content, Width = 120, Height = 60 };
            scroll.Resources.MergedDictionaries.Add(palette.Resources);
            // 模擬 VsThemeBrushes 發布的原生隱含樣式；覆蓋式捲軸不能被它換回 17 DIP。
            var native = new Style(typeof(ScrollBar));
            native.Setters.Add(new Setter(FrameworkElement.WidthProperty, 17d));
            native.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 17d));
            scroll.Resources[typeof(ScrollBar)] = native;
            SqlAssistChrome.ApplyOverlayScroll(scroll);
            scroll.Measure(new Size(120, 60)); scroll.Arrange(new Rect(0, 0, 120, 60)); scroll.UpdateLayout();

            Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            var bar = Assert.IsType<ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.Equal(3, bar.ActualWidth);
            Assert.Equal(120, content.ActualWidth);
            var thumb = Assert.IsAssignableFrom<Track>(bar.Template.FindName("PART_Track", bar)).Thumb;
            var grip = Assert.IsType<Border>(thumb.Template.FindName("grip", thumb));
            Assert.Equal(colors[ThemeBrush.ScrollThumb], ThemeResourceSetTests.ColorOf(grip.Background));
            Assert.True(thumb.ActualHeight >= 16);

            scroll.ScrollToBottom(); scroll.UpdateLayout();
            Assert.Equal(scroll.ScrollableHeight, bar.Value);
            scroll.Height = 400; scroll.Measure(new Size(120, 400)); scroll.Arrange(new Rect(0, 0, 120, 400)); scroll.UpdateLayout();
            Assert.NotEqual(Visibility.Visible, bar.Visibility);
        });
    }

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

    /// <summary>
    /// 去彈跳只有一張表，而且相對關係固定。
    /// </summary>
    /// <remarks>
    /// 打字驅動的搜尋最短，選取驅動的預覽次之，每一次都要查一輪的估算與比對 SQL 全文的
    /// Memory 搜尋最寬。散在四個檔時沒有人比得出這個順序，改壞了也看不出來。
    /// </remarks>
    /// <summary>
    /// 下拉的箭頭是一個<b>狀態</b>：收合朝右、展開朝下，而且轉過去而不是跳過去。
    /// </summary>
    /// <remarks>
    /// 兩處各寫一次角度的下場是其中一邊轉錯邊；只在按下時轉的那一版，面板被「按到外面」
    /// 關掉之後箭頭仍朝下，指著一個已經不在畫面上的面板。
    /// </remarks>
    [Fact]
    public void 箭頭收合朝右展開朝下且動畫關掉時直接寫角度()
    {
        WpfTest.Run(() =>
        {
            var chevron = SqlAssistChrome.CreateChevron(expanded: false);
            var rotation = Assert.IsType<RotateTransform>(chevron.RenderTransform);
            Assert.Equal(-90, rotation.Angle);
            Assert.Equal(new Point(0.5, 0.5), chevron.RenderTransformOrigin);

            SqlAssistChrome.SetChevronExpanded(chevron, expanded: true, motion: false);
            Assert.Equal(0, rotation.Angle);
            Assert.False(rotation.HasAnimatedProperties);

            SqlAssistChrome.SetChevronExpanded(chevron, expanded: false, motion: false);
            Assert.Equal(-90, rotation.Angle);

            // 開著動畫時走動畫；不寫 From，所以連按兩下是從轉到一半的位置反向。
            SqlAssistChrome.SetChevronExpanded(chevron, expanded: true, motion: true);
            Assert.True(rotation.HasAnimatedProperties);

            // 關掉動畫是真的關掉：先把跑著的那一份清掉，再寫最終角度。
            SqlAssistChrome.SetChevronExpanded(chevron, expanded: false, motion: false);
            Assert.False(rotation.HasAnimatedProperties);
            Assert.Equal(-90, rotation.Angle);

            // 揭露這一級短到可以中途反向。
            Assert.InRange(SqlAssistChrome.ChevronTurnDuration, System.TimeSpan.Zero, System.TimeSpan.FromMilliseconds(300));
        });
    }

    /// <summary>
    /// 篩選群組的分隔線只有一份，SQL Memory 與 SQL Search 都從它來。
    /// </summary>
    [Fact]
    public void 篩選群組分隔線是同一份而且群距大於群內距()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var divider = SqlAssistChrome.CreateFilterGroupDivider();
            var host = new Border { Child = divider };
            host.Resources.MergedDictionaries.Add(palette.Resources);
            palette.Update(ThemePaletteTests.ColorsFor("dark"));
            host.Measure(new Size(200, 60));
            host.Arrange(new Rect(0, 0, 200, 60));
            host.UpdateLayout();

            Assert.Equal(1, divider.Width);
            Assert.True(divider.SnapsToDevicePixels);
            Assert.False(divider.IsHitTestVisible);
            // 不表達狀態，所以用髮絲線而不是任何語意色。
            Assert.Same(palette.Resources[ThemeBrush.Hairline], divider.Background);
            // 左右對稱，而且比群內的 4 DIP 寬：兩個數字的差就是「這是兩群」。
            Assert.Equal(divider.Margin.Left, divider.Margin.Right);
            Assert.True(divider.Margin.Left > 4);
            // 比按鈕矮一截，讀起來是一條界線不是一個邊框。
            Assert.InRange(divider.Height, 12, 24);

            // SQL Memory 的狀態與期間是兩群，走的是同一份。
            var filters = SqlAssistChrome.CreateMemoryHistoryFilters(
                new SqlPillSelector("執行", "草稿"), new SqlPillSelector("七天", "全部"));
            var panel = Assert.IsType<WrapPanel>(filters);
            Assert.Equal(2, panel.Children.Count);
            var period = Assert.IsType<StackPanel>(panel.Children[1]);
            var rule = Assert.IsType<Border>(period.Children[0]);
            Assert.Equal(1, rule.Width);
            Assert.Equal(divider.Margin, rule.Margin);
        });
    }

    [Fact]
    public void 去彈跳常數維持由短到長的順序()
    {
        Assert.Equal(200, SqlAssistChrome.Debounce.Search.TotalMilliseconds);
        Assert.Equal(220, SqlAssistChrome.Debounce.Preview.TotalMilliseconds);
        Assert.Equal(250, SqlAssistChrome.Debounce.CleanupEstimate.TotalMilliseconds);
        Assert.Equal(300, SqlAssistChrome.Debounce.MemorySearch.TotalMilliseconds);

        Assert.True(SqlAssistChrome.Debounce.Search < SqlAssistChrome.Debounce.Preview);
        Assert.True(SqlAssistChrome.Debounce.Preview < SqlAssistChrome.Debounce.CleanupEstimate);
        Assert.True(SqlAssistChrome.Debounce.CleanupEstimate < SqlAssistChrome.Debounce.MemorySearch);
    }
}
