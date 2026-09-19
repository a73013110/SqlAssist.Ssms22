using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    public void 結果列與分組標頭在每一種主題都讀所屬控制項的動態筆刷()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var rows = Rows();
            var list = new SqlSearchList();
            list.SetRowsSource(rows, nameof(SqlSearchRow.GroupLabel));
            list.SelectedIndex = 0;

            var surface = new Border { Child = list }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            surface.Resources.MergedDictionaries.Add(palette.Resources);

            foreach (var mode in new[] { "light", "dark", "high-contrast", "mango", "forest", "light-again" })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                surface.Measure(new Size(440, 400));
                surface.Arrange(new Rect(0, 0, 440, 400));
                surface.UpdateLayout();

                // 名稱與本文兩組各自成一組，順序就是 SearchMatchTargets.GroupOrder 的順序。
                var headers = Descendants<TextBlock>(list)
                    .Where(text => text.Text is "名稱" or "定義本文").ToArray();
                Assert.Equal(new[] { "名稱", "定義本文" }, headers.Select(text => text.Text).ToArray());
                Assert.All(headers, text => Assert.Same(palette.Resources[ThemeBrush.DimForeground], text.Foreground));

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
    public void 結果卡片與搜尋選項的互動狀態只換筆刷不改版面尺寸()
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
            var option = SqlAssistChrome.CreateSearchOption("大小寫", "只取大小寫完全相同的本文命中。").Template;

            foreach (var template in new[] { card, option })
            foreach (var trigger in template.Triggers.OfType<Trigger>())
            {
                Assert.DoesNotContain(trigger.Setters.OfType<Setter>(), setter => layoutProperties.Contains(setter.Property));
            }

            // 結果沒有刪除動作；留著 IsRemoving 的繫結只會在每一列上找一個不存在的屬性。
            Assert.DoesNotContain(card.Triggers.OfType<DataTrigger>(),
                trigger => (trigger.Binding as Binding)?.Path.Path == "IsRemoving");
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
            Hit(SearchMatchTarget.Name, "[dbo].[Cat_BookCopy].[CopyNo]", "CopyNo", new MatchSpan(0, 6)), "Column");
        Assert.Equal(new[] { new MatchSpan(22, 6) }, column.TitleSpans.ToArray());

        // 本文命中的片段來自定義本文，與標題沒有關係。
        var body = new SqlSearchRow(Hit(SearchMatchTarget.Text, "[dbo].[Loan]", "  JOIN Loan l", new MatchSpan(7, 4)), "Table");
        Assert.Empty(body.TitleSpans);
        Assert.Equal("JOIN Loan l", body.Snippet);
        Assert.Equal(new[] { new MatchSpan(5, 4) }, body.SnippetSpans.ToArray());
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
            path, snippet, new[] { span });
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
