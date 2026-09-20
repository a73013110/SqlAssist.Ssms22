using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.SqlMemory;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

/// <summary>四種狀態只有一份實作：載入、空、讀不到與權限不足疊在同一塊內容上。</summary>
public sealed class SqlStateSurfaceTests
{
    static SqlStateSurfaceTests() => SqlIconImage.Factory = icon => new Border { Width = 16, Height = 16, Tag = icon };

    [Fact]
    public void 載入狀態不佔版面也不留文字並在收起後停轉()
    {
        WpfTest.Run(() =>
        {
            var (surface, content) = Host();
            Layout(surface);
            var size = content.RenderSize;

            surface.State = SqlSurfaceState.Loading;
            Layout(surface);
            Assert.Equal(size, content.RenderSize);
            Assert.Empty(Visible<TextBlock>(surface));

            var rotation = Assert.IsType<RotateTransform>(
                Descendants<System.Windows.Shapes.Path>(surface).Single().RenderTransform);
            surface.State = SqlSurfaceState.None;
            Assert.False(rotation.HasAnimatedProperties);
        });
    }

    [Fact]
    public void 空狀態說出抬頭與下一步而且不掛警示圖示()
    {
        WpfTest.Run(() =>
        {
            var (surface, _) = Host();
            surface.State = SqlSurfaceState.Empty("沒有相符項目", "換個關鍵字，或放寬分類與資料庫範圍。");
            Layout(surface);

            var texts = Visible<TextBlock>(surface).Select(text => text.Text).ToArray();
            Assert.Equal(new[] { "沒有相符項目", "換個關鍵字，或放寬分類與資料庫範圍。" }, texts);
            Assert.Empty(Visible<SqlIconImage>(surface));
        });
    }

    /// <summary>空字串不是一種狀態：沒有話要說時整塊讓開，不留一個看不見卻吃掉點擊的面板。</summary>
    [Fact]
    public void 沒有抬頭的空狀態等於什麼都不說()
    {
        Assert.Equal(SqlSurfaceKind.None, SqlSurfaceState.Empty("").Kind);
        Assert.Equal(SqlSurfaceState.None, SqlSurfaceState.Empty("", "說明"));
    }

    /// <summary>
    /// 帶得動下一步的狀態多一顆按鈕；沒有下一步的那幾種整塊讓開。
    /// </summary>
    /// <remarks>
    /// 把出口留在別的選單裡等於要使用者先猜出問題出在範圍上。整塊永遠可命中的反面症狀更隱形：
    /// 沒有話要說的時候，一塊看不見的面板壓在清單上，列的停駐與點擊全部失效。
    /// </remarks>
    [Fact]
    public void 帶動作的狀態畫出按鈕而且只有這時候才吃點擊()
    {
        WpfTest.Run(() =>
        {
            var (surface, _) = Host();
            var pressed = 0;
            surface.ActionRequested += (_, _) => pressed++;

            surface.State = SqlSurfaceState.Empty("沒有相符項目", "換個關鍵字。");
            Layout(surface);
            Assert.Empty(Visible<Button>(surface));
            Assert.False(Assert.Single(Visible<StackPanel>(surface)).IsHitTestVisible);

            surface.State = SqlSurfaceState.Empty("尚未連線", "在查詢視窗連上資料庫。", "從物件總管挑一台");
            Layout(surface);
            var action = Assert.Single(Visible<Button>(surface));
            Assert.Equal("從物件總管挑一台", action.Content);
            Assert.True(Assert.Single(Visible<StackPanel>(surface)).IsHitTestVisible);

            action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, pressed);

            // 只有標籤進得了狀態：同一種狀態改文字不重播淡入，帶委派的話每一次新建都不相等。
            Assert.Equal(
                SqlSurfaceState.Empty("尚未連線", "在查詢視窗連上資料庫。", "從物件總管挑一台"),
                surface.State);
            Assert.NotEqual(
                SqlSurfaceState.Empty("尚未連線", "在查詢視窗連上資料庫。"),
                surface.State);

            surface.State = SqlSurfaceState.Loading;
            Layout(surface);
            Assert.Empty(Visible<Button>(surface));
        });
    }

    /// <summary>
    /// 讀不到與權限不足走同一個出口，只有抬頭那一句不同。
    /// </summary>
    /// <remarks>
    /// 分成兩塊版面的話，兩句話會在窄工具窗裡各吃掉一段高度，而它們永遠不會同時出現。
    /// 抬頭不同是必要的：一個叫使用者重試，一個叫他去要權限。
    /// </remarks>
    [Fact]
    public void 讀不到與權限不足共用同一塊版面只換抬頭()
    {
        WpfTest.Run(() =>
        {
            var (surface, _) = Host();
            surface.State = SqlSurfaceState.Unreadable("搜尋失敗：連線中斷。");
            Layout(surface);
            var panel = Assert.Single(Visible<StackPanel>(surface));
            Assert.Equal(SqlIcon.Warning, Assert.Single(Visible<SqlIconImage>(surface)).Icon);
            Assert.Equal(
                new[] { SqlSurfaceState.UnreadableTitle, "搜尋失敗：連線中斷。" },
                Visible<TextBlock>(surface).Select(text => text.Text).ToArray());

            surface.State = SqlSurfaceState.Denied("作業讀不到（多半是這個登入對 msdb 沒有權限）。");
            Layout(surface);
            Assert.Same(panel, Assert.Single(Visible<StackPanel>(surface)));
            Assert.Equal(SqlIcon.Warning, Assert.Single(Visible<SqlIconImage>(surface)).Icon);
            Assert.Equal(
                new[] { SqlSurfaceState.DeniedTitle, "作業讀不到（多半是這個登入對 msdb 沒有權限）。" },
                Visible<TextBlock>(surface).Select(text => text.Text).ToArray());
        });
    }

    /// <summary>四種狀態互斥：轉圈的圖示不會壓在說明文字上。</summary>
    [Fact]
    public void 載入與訊息不會同時出現()
    {
        WpfTest.Run(() =>
        {
            var (surface, _) = Host();
            surface.State = SqlSurfaceState.Loading;
            Layout(surface);
            Assert.Empty(Visible<SqlIconImage>(surface));
            Assert.Single(Visible<System.Windows.Shapes.Path>(surface));

            surface.State = SqlSurfaceState.Unreadable("讀不到。");
            Layout(surface);
            Assert.Empty(Visible<System.Windows.Shapes.Path>(surface));
        });
    }

    /// <summary>沒有說明時不留一行空白；缺值直接 collapse，不留空槽。</summary>
    [Fact]
    public void 沒有說明時不留空行()
    {
        WpfTest.Run(() =>
        {
            var (surface, _) = Host();
            surface.State = SqlSurfaceState.Empty("尚未連線");
            Layout(surface);
            Assert.Equal("尚未連線", Assert.Single(Visible<TextBlock>(surface)).Text);
        });
    }

    [Fact]
    public void 四種狀態在三種主題下都畫得出來()
    {
        WpfTest.Run(() =>
        {
            var (surface, _) = Host();
            var palette = new ThemeResourceSet();
            var host = new Border { Child = surface }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            host.Resources.MergedDictionaries.Add(palette.Resources);
            var directory = ThemeVisualTests.FindOutputDirectory();
            var states = new (string Name, SqlSurfaceState State)[]
            {
                ("loading", SqlSurfaceState.Loading),
                ("empty", SqlSurfaceState.Empty("沒有相符項目", "換個關鍵字，或放寬分類與資料庫範圍。")),
                ("unreadable", SqlSurfaceState.Unreadable("搜尋失敗：連線中斷。")),
                ("denied", SqlSurfaceState.Denied("作業讀不到（多半是這個登入對 msdb 沒有權限）。")),
            };

            foreach (var mode in new[] { "light", "dark", "high-contrast" })
            {
                foreach (var (name, state) in states)
                {
                    palette.Update(ThemePaletteTests.ColorsFor(mode));
                    surface.State = state;
                    host.Measure(new Size(320, 180)); host.Arrange(new Rect(0, 0, 320, 180)); host.UpdateLayout();
                    foreach (var text in Visible<TextBlock>(surface))
                        Assert.InRange(text.TranslatePoint(new Point(text.ActualWidth, 0), host).X, 0, 320.5);
                    if (directory is null) continue;
                    var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(
                        System.IO.Path.Combine(directory, "sql-state-surface-" + name + "-" + mode + ".png"));
                    encoder.Save(file);
                }
            }
        });
    }

    /// <summary>SQL Memory 只在一列都沒有時遮住清單；續頁的進度與失敗留在頁尾與狀態列。</summary>
    [Fact]
    public void SqlMemory的表面只在清單空的時候遮住清單()
    {
        const string failure = "載入失敗：檔案被鎖住。";
        var cases = new (int RowCount, bool Loading, string Failure, SqlSurfaceKind Expected)[]
        {
            (0, true, "", SqlSurfaceKind.Loading),
            (12, true, "", SqlSurfaceKind.None),
            (0, false, failure, SqlSurfaceKind.Unreadable),
            (12, false, failure, SqlSurfaceKind.None),
        };

        foreach (var (rowCount, loading, message, expected) in cases)
        {
            var footer = new SqlMemoryBrowserModel().Footer(rowCount);
            Assert.Equal(expected, SqlMemorySurfaceState.For(footer, loading, rowCount, message).Kind);
        }
    }

    private static (SqlStateSurface Surface, Border Content) Host()
    {
        var content = new Border { Width = 320, Height = 180 }
            .WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
        return (new SqlStateSurface(content), content);
    }

    private static void Layout(FrameworkElement surface)
    {
        surface.Measure(new Size(320, 180));
        surface.Arrange(new Rect(0, 0, 320, 180));
        surface.UpdateLayout();
    }

    private static IEnumerable<T> Visible<T>(DependencyObject root) where T : UIElement =>
        Descendants<T>(root).Where(IsVisible);

    private static bool IsVisible(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }

        return true;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
