using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.SqlMemory;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlFavoriteRevisionVisualTests
{
    static SqlFavoriteRevisionVisualTests() => SqlIconImage.Factory = icon => new Border { Width = 16, Height = 16, Background = Brushes.Gray, Tag = icon };

    private static SqlFavoriteRevisionRow Row(int minute, bool current = false) => new(new SqlFavoriteRevisionItem(Guid.NewGuid(),
        "c" + minute, DateTimeOffset.Now.AddMinutes(-minute), current ? SqlRevisionReason.Favorite : SqlRevisionReason.BeforeExecute,
        current, "SELECT LoanId\nFROM LoanDetail;", 1234));

    [Fact]
    public void RevisionActionsComeFromTheSharedListAndRevertExplainsWhyItCannotRun()
    {
        WpfTest.Run(() =>
        {
            var template = SqlAssistChrome.CreateRevisionItemTemplate();
            Button[] Render(SqlFavoriteRevisionRow row)
            {
                var content = new ContentControl { ContentTemplate = template, Content = row };
                content.Measure(new Size(360, 200)); content.Arrange(new Rect(0, 0, 360, 200)); content.UpdateLayout();
                return Descendants<Button>(content).ToArray();
            }
            SqlFavoriteRevisionAction[] Shown(Button[] buttons) => buttons.Where(button => button.Visibility == Visibility.Visible)
                .Select(button => Assert.IsType<SqlFavoriteRevisionAction>(button.Tag)).ToArray();

            var current = Row(0, current: true);
            var buttons = Render(current);
            Assert.Equal(SqlFavoriteRevisionCommand.All.Select(command => command.Action), buttons.Select(button => (SqlFavoriteRevisionAction)button.Tag));
            // 目前版本不能回溯成自己：收起而不是停用佔位。
            Assert.Equal(new[] { SqlFavoriteRevisionAction.Preview, SqlFavoriteRevisionAction.Open, SqlFavoriteRevisionAction.Copy }, Shown(buttons));
            Assert.Equal("收藏版本 · 1,234 字元", current.Detail);

            var older = Row(5);
            var olderButtons = Render(older);
            Assert.Equal(SqlFavoriteRevisionCommand.All.Select(command => command.Action), Shown(olderButtons));
            var revert = olderButtons.Single(button => (SqlFavoriteRevisionAction)button.Tag == SqlFavoriteRevisionAction.Revert);
            Assert.False(revert.IsEnabled);
            Assert.Equal("內容與目前版本相同", AutomationProperties.GetName(revert));
            older.CanRevert = true;
            Assert.True(revert.IsEnabled);
            Assert.Equal("回溯為新版本", revert.ToolTip);
            older.CanRevert = false; older.ContentMissing = true;
            Assert.Equal("內容已清理，無法回溯", revert.ToolTip);
            Assert.Equal("引用自 History · 內容已清理", older.Detail);
            Assert.Equal(SqlActionTone.Favorite, SqlFavoriteRevisionCommand.For(SqlFavoriteRevisionAction.Revert).Tone);
            Assert.All(buttons.Concat(olderButtons), button => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button))));
        });
    }

    [Fact]
    public void TimelineRailMarksCurrentAndEndsWithoutChangingLayoutWhileNewRowsAnimate()
    {
        WpfTest.Run(() =>
        {
            var rows = new ObservableCollection<SqlFavoriteRevisionRow>(new[] { Row(0, true), Row(3), Row(9) });
            rows[0].IsFirst = true; rows[2].IsLast = true;
            var list = new SqlFavoriteRevisionList(motion: true);
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("dark"));
            list.Resources.MergedDictionaries.Add(palette.Resources);
            list.SetRowsSource(rows, new SqlMemoryPager());
            using var source = new HwndSource(new HwndSourceParameters("SQL Memory revision test") { Width = 360, Height = 400, WindowStyle = 0 });
            source.RootVisual = list;
            list.Measure(new Size(360, 400)); list.Arrange(new Rect(0, 0, 360, 400)); list.UpdateLayout();

            Border Part(int index, string name) =>
                (Border)((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).Template
                    .FindName(name, (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index));
            Assert.Equal(Visibility.Hidden, Part(0, "railTop").Visibility);
            Assert.Equal(Visibility.Visible, Part(0, "railBottom").Visibility);
            Assert.Equal(Visibility.Visible, Part(1, "railTop").Visibility);
            Assert.Equal(Visibility.Hidden, Part(2, "railBottom").Visibility);
            // 目前版本是實心圓點，其他是空心；形狀與顏色同時說明。
            Assert.Equal(palette.Get(ThemeBrush.AccentBorder), Part(0, "dot").Background);
            Assert.Equal(palette.Get(ThemeBrush.WindowBackground), Part(1, "dot").Background);

            // 軌道連續：相鄰兩列之間沒有空隙。
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            Assert.Equal(first.TranslatePoint(new Point(0, first.ActualHeight), list).Y, second.TranslatePoint(new Point(), list).Y, 1);

            var top = second.TranslatePoint(new Point(), list).Y;
            var added = Row(1);
            rows.Insert(1, added); added.IsNew = true; list.UpdateLayout();
            var card = (UIElement)VisualTreeHelper.GetChild(list.ItemContainerGenerator.ContainerFromIndex(1), 0);
            Assert.True(card.RenderTransform.HasAnimatedProperties);
            var moved = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
            // 進場只動 RenderTransform；被推下去的列位移等於新列的版面高度，不含動畫位移。
            Assert.Equal(top + ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)).ActualHeight, moved.TranslatePoint(new Point(), list).Y, 1);
        });
    }

    [Fact]
    public void DiffLinesUseMarkersAndSemanticTintsAndStayVirtualized()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            palette.Update(ThemePaletteTests.ColorsFor("light"));
            var view = new SqlTextDiffView { Width = 520, Height = 300 };
            view.Resources.MergedDictionaries.Add(palette.Resources);
            using var source = new HwndSource(new HwndSourceParameters("SQL diff test") { Width = 520, Height = 300, WindowStyle = 0 });
            source.RootVisual = view;
            var old = string.Join("\n", Enumerable.Range(0, 50_000).Select(i => "SELECT " + i + " FROM Loan;"));
            var changed = old.Replace("SELECT 40000 FROM Loan;", "SELECT 40000 FROM LoanDetail;");
            var result = SqlTextDiff.Compute(old, changed, TestContext.Current.CancellationToken);
            view.Show(result, motion: false);
            view.Measure(new Size(520, 300)); view.Arrange(new Rect(0, 0, 520, 300)); view.UpdateLayout();

            var list = Descendants<ListBox>(view).Single();
            var realized = Descendants<ListBoxItem>(list).ToArray();
            // 五萬行只實體化看得到的列，並已捲到第一處變更附近。
            Assert.InRange(realized.Length, 1, 60);
            var lines = realized.Select(item => (SqlTextDiffLine)item.Content).ToArray();
            Assert.Contains(lines, line => line.Kind == SqlTextDiffLineKind.Removed);
            Assert.Contains(lines, line => line.Kind == SqlTextDiffLineKind.Added);

            T Part<T>(ListBoxItem item, string name) where T : class
            {
                var presenter = Descendants<ContentPresenter>(item).First();
                return Assert.IsType<T>(presenter.ContentTemplate.FindName(name, presenter));
            }
            TextBlock Cell(ListBoxItem item, string name) => Part<TextBlock>(item, name);
            DockPanel Line(ListBoxItem item) => Part<DockPanel>(item, "line");
            var removed = realized.Single(item => ((SqlTextDiffLine)item.Content).Kind == SqlTextDiffLineKind.Removed);
            var added = realized.Single(item => ((SqlTextDiffLine)item.Content).Kind == SqlTextDiffLineKind.Added);
            var same = realized.First(item => ((SqlTextDiffLine)item.Content).Kind == SqlTextDiffLineKind.Unchanged);
            Assert.Equal("-", Cell(removed, "marker").Text);
            Assert.Equal("+", Cell(added, "marker").Text);
            Assert.Equal("", Cell(same, "marker").Text);
            Assert.Equal(palette.Get(ThemeBrush.DiffRemovedBackground), Line(removed).Background);
            Assert.Equal(palette.Get(ThemeBrush.DiffAddedBackground), Line(added).Background);
            Assert.Equal(palette.Get(ThemeBrush.DiffAddedForeground), Cell(added, "marker").Foreground);
            Assert.Equal("40001", Cell(removed, "oldNumber").Text);
            Assert.Equal("", Cell(removed, "newNumber").Text);
            Assert.Equal("刪除", AutomationProperties.GetName(Cell(removed, "marker")));

            // 換主題只換資源，不重建列。
            palette.Update(ThemePaletteTests.ColorsFor("high-contrast"));
            Assert.Equal(palette.Get(ThemeBrush.ListBackground).ToString(), Line(added).Background.ToString());
            Assert.Equal("+", Cell(added, "marker").Text);

            view.Clear();
            Assert.Null(view.Result);
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
}
