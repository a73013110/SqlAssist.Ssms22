using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

/// <summary>主從區的轉向、收合把手與兩個方向的比例；外觀與主題另在各 feature 的視覺測試。</summary>
public sealed class MasterDetailViewTests
{
    private const double Threshold = MasterDetailView.DefaultSideBySideWidth;

    [Fact]
    public void 臨界寬度不因為量測反覆換向()
    {
        WpfTest.Run(() =>
        {
            var view = Build(out _, out _);
            var turns = 0; view.OrientationChanged += (_, _) => turns++;

            Layout(view, Threshold - 1);
            Assert.False(view.IsSideBySide);
            Assert.Equal(0, turns);

            Layout(view, Threshold);
            Assert.True(view.IsSideBySide);
            Assert.Equal(1, turns);

            // 門檻底下還在 hysteresis 區間內：拖回來一點不算拖回上下分割。
            foreach (var width in new[] { Threshold - 1, Threshold - 16, Threshold - 24, Threshold + 4 })
            {
                Layout(view, width);
                Assert.True(view.IsSideBySide, $"{width} 不該轉回上下");
            }
            Assert.Equal(1, turns);

            // 真的窄回去才換，而且換完之後同一個寬度不會又被門檻拉回左右。
            Layout(view, Threshold - 40);
            Assert.False(view.IsSideBySide);
            Assert.Equal(2, turns);

            foreach (var width in new[] { Threshold - 40, Threshold - 24, Threshold - 1 })
            {
                Layout(view, width);
                Assert.False(view.IsSideBySide, $"{width} 不該轉去左右");
            }
            Assert.Equal(2, turns);

            // 在門檻上來回抖動（拖視窗邊框就是這樣）：只換一次，之後停在左右分割不再跳。
            for (var i = 0; i < 8; i++)
            {
                Layout(view, Threshold + 1);
                Layout(view, Threshold - 1);
            }
            Assert.True(view.IsSideBySide);
            Assert.Equal(3, turns);
        });
    }

    [Fact]
    public void 上下分割收合後把手留在清單下緣而且展得開()
    {
        WpfTest.Run(() =>
        {
            var view = Build(out var master, out var detail);
            Layout(view, Threshold - 40);
            Assert.False(view.IsSideBySide);

            view.SetDetailExpanded(false);
            Layout(view, Threshold - 40);

            Assert.Equal(Visibility.Collapsed, detail.Visibility);
            Assert.Equal(Visibility.Collapsed, Splitter(view).Visibility);

            var handle = Toggle(view, "展開預覽");
            Assert.True(handle.ActualWidth > 0 && handle.ActualHeight > 0);
            var top = handle.TranslatePoint(new Point(), view).Y;
            // 單列把手貼在清單下緣：整條都在視窗內，而且在清單底下而不是被 Preview 那一列帶走。
            Assert.InRange(top + handle.ActualHeight, 0, view.ActualHeight + 0.1);
            Assert.True(top >= master.TranslatePoint(new Point(0, master.ActualHeight), view).Y - 0.1);

            handle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Layout(view, Threshold - 40);
            Assert.True(view.IsDetailExpanded);
            Assert.Equal(Visibility.Visible, detail.Visibility);
            Assert.True(detail.ActualHeight > 0);
        });
    }

    [Fact]
    public void 左右分割展開時預覽開關屬於右邊那一欄()
    {
        WpfTest.Run(() =>
        {
            var view = Build(out var master, out var detail);
            Layout(view, Threshold + 80);
            Assert.True(view.IsSideBySide);
            Assert.True(view.IsDetailExpanded);

            var toggle = Toggle(view, "收合預覽");
            var left = toggle.TranslatePoint(new Point(), view).X;
            // 開關管的是右邊那一塊，就不能出現在清單上方：橫跨整列又靠左，等於把它擺在清單左上角。
            Assert.True(left >= master.TranslatePoint(new Point(master.ActualWidth, 0), view).X - 0.1,
                "預覽開關不得落在清單上方");
            Assert.InRange(left - detail.TranslatePoint(new Point(), view).X, -0.1, 12);
            // 抬頭在 Preview 上緣，清單則從最上面開始：左邊不留一條屬於別人的空白。
            Assert.True(toggle.TranslatePoint(new Point(), view).Y <= detail.TranslatePoint(new Point(), view).Y + 0.1);
            Assert.InRange(master.TranslatePoint(new Point(), view).Y, 0, 0.1);
            Assert.True(master.ActualHeight >= detail.ActualHeight);

            // 上下分割時抬頭仍然橫跨整列並貼在 Preview 上緣；那裡它本來就在清單與 Preview 之間。
            Layout(view, Threshold - 40);
            Assert.False(view.IsSideBySide);
            var stacked = Toggle(view, "收合預覽");
            Assert.InRange(stacked.TranslatePoint(new Point(), view).X, 0, 12);
            Assert.True(stacked.TranslatePoint(new Point(), view).Y >=
                master.TranslatePoint(new Point(0, master.ActualHeight), view).Y - 0.1);
        });
    }

    [Fact]
    public void 左右分割收合後把手留在清單右緣而且展得開()
    {
        WpfTest.Run(() =>
        {
            var view = Build(out var master, out var detail);
            Layout(view, Threshold + 80);
            Assert.True(view.IsSideBySide);

            view.SetDetailExpanded(false);
            Layout(view, Threshold + 80);

            Assert.Equal(Visibility.Collapsed, detail.Visibility);
            Assert.Equal(Visibility.Collapsed, Splitter(view).Visibility);

            var handle = Toggle(view, "展開預覽");
            Assert.True(handle.ActualWidth > 0 && handle.ActualHeight > 0);
            var left = handle.TranslatePoint(new Point(), view).X;
            // 把手退化成貼在清單右緣的一條；右緣仍在視窗內，清單拿走其餘寬度。
            Assert.InRange(left + handle.ActualWidth, 0, view.ActualWidth + 0.1);
            Assert.True(left >= master.TranslatePoint(new Point(master.ActualWidth, 0), view).X - 0.1);
            Assert.True(master.ActualWidth > view.ActualWidth / 2);
            // 那一條只有一列高，不隨主從區長高。
            Assert.True(handle.ActualHeight < view.ActualHeight / 2);

            handle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Layout(view, Threshold + 80);
            Assert.True(view.IsDetailExpanded);
            Assert.Equal(Visibility.Visible, detail.Visibility);
            Assert.True(detail.ActualWidth > 0);
        });
    }

    [Fact]
    public void 兩個方向的拖曳比例分開保存()
    {
        WpfTest.Run(() =>
        {
            var view = Build(out _, out _);
            Layout(view, Threshold - 40);

            // 使用者在上下分割拖過的比例。
            view.RowDefinitions[0].Height = new GridLength(5, GridUnitType.Star);
            view.RowDefinitions[2].Height = new GridLength(3, GridUnitType.Star);

            Layout(view, Threshold + 80);
            Assert.True(view.IsSideBySide);
            // 左右那一份仍是預設值，沒有被上下的拖曳結果蓋掉；而它的預設與上下相反——
            // 清單的寬度有上界，SQL 沒有，所以左右分割時 Preview 分得比較多。
            Assert.Equal(new GridLength(2, GridUnitType.Star), view.ColumnDefinitions[0].Width);
            Assert.Equal(new GridLength(3, GridUnitType.Star), view.ColumnDefinitions[2].Width);

            view.ColumnDefinitions[0].Width = new GridLength(7, GridUnitType.Star);
            view.ColumnDefinitions[2].Width = new GridLength(2, GridUnitType.Star);

            Layout(view, Threshold - 40);
            Assert.False(view.IsSideBySide);
            Assert.Equal(new GridLength(5, GridUnitType.Star), view.RowDefinitions[0].Height);
            Assert.Equal(new GridLength(3, GridUnitType.Star), view.RowDefinitions[2].Height);

            Layout(view, Threshold + 80);
            Assert.Equal(new GridLength(7, GridUnitType.Star), view.ColumnDefinitions[0].Width);
            Assert.Equal(new GridLength(2, GridUnitType.Star), view.ColumnDefinitions[2].Width);

            // 收合再展開也各自回到原來那一份。
            view.SetDetailExpanded(false);
            view.SetDetailExpanded(true);
            Layout(view, Threshold + 80);
            Assert.Equal(new GridLength(7, GridUnitType.Star), view.ColumnDefinitions[0].Width);

            Layout(view, Threshold - 40);
            view.SetDetailExpanded(false);
            view.SetDetailExpanded(true);
            Layout(view, Threshold - 40);
            Assert.Equal(new GridLength(5, GridUnitType.Star), view.RowDefinitions[0].Height);
        });
    }

    private static MasterDetailView Build(out FrameworkElement master, out FrameworkElement detail)
    {
        master = new Border { MinWidth = 40, MinHeight = 40 };
        detail = new Border { MinWidth = 40, MinHeight = 40 };
        var summary = new TextBlock { Text = "Lib_Reader.sql" };
        return new MasterDetailView(master, detail, summary, Threshold);
    }

    private static void Layout(MasterDetailView view, double width)
    {
        view.Measure(new Size(width, 400));
        view.Arrange(new Rect(0, 0, width, 400));
        view.UpdateLayout();
    }

    private static GridSplitter Splitter(MasterDetailView view) => Descendants<GridSplitter>(view).Single();

    private static Button Toggle(MasterDetailView view, string automationName) =>
        Descendants<Button>(view).Single(button => AutomationProperties.GetName(button) == automationName);

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
