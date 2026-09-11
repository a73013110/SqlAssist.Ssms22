using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class NotificationChromeTests
{
    [Theory]
    [InlineData(0, 746, 8)]
    [InlineData(1, 746, 452)]
    public void 定位使用內容區安全邊距(int value, double x, double y)
    {
        var position = (NotificationPosition)value;
        Assert.Equal(new Point(x, y), SqlAssistChrome.NotificationAnchor(new Size(1000, 700), new Size(250, 240), position));
        var small = SqlAssistChrome.NotificationAnchor(new Size(100, 80), new Size(100, 80), position);
        Assert.Equal(new Point(0, 0), small);
    }

    [Fact]
    public void 狀態更新保留列身分且收合停止循環動畫()
    {
        WpfTest.Run(() =>
        {
            var center = new NotificationCenter();
            var task = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, source: "LibArchive");
            var root = SqlAssistChrome.CreateNotificationCard();
            var details = root.DetailsPanel;
            root.Update(Cards(center), true, true);
            var row = Assert.IsType<NotificationRow>(details.Children[0]);
            Assert.Equal(NotificationVisualStatus.Running, row.Status);
            // 只有抬頭轉；列上的執行中是靜態光環。
            Assert.True(root.StatusRotation.HasAnimatedProperties);
            Assert.False(((TransformGroup)Icon(row).RenderTransform).HasAnimatedProperties);
            // 首次展開只動畫父區塊，不能把每列也歸零，否則父區塊量不到內容高度。
            Assert.True(double.IsPositiveInfinity(row.MaxHeight));
            root.Update(Cards(center), false, false);
            Assert.Same(row, details.Children[0]);
            Assert.Equal(Visibility.Collapsed, root.DetailScroll.Visibility);
            Assert.False(root.StatusRotation.HasAnimatedProperties);
            task.Fail(); task.Dispose();
            root.Update(Cards(center), true, false);
            Assert.Same(row, details.Children[0]);
            Assert.Equal(NotificationVisualStatus.Failed, row.Status);
            root.SetOptions(true, true);
            Assert.Null(root.Effect);
            Assert.Null(root.Body.Effect);
            Assert.Equal(Visibility.Collapsed, root.Sheen.Visibility);
            root.Transition(false, false, true, NotificationPosition.TopRight);
            root.Transition(true, false, true, NotificationPosition.TopRight);
            root.StopMotion();
            Assert.False(root.HasAnimatedProperties);
            Assert.Equal(1, root.Opacity);
            Assert.Equal(1, root.SurfaceScale.ScaleX);
            Assert.False(root.StatusRotation.HasAnimatedProperties);
        });
    }

    /// <summary>卡片只認得自己的 view-model，換一個通知來源不必改版面。</summary>
    [Fact]
    public void 卡片的介面不出現通知來源的型別()
    {
        foreach (var type in new[] { typeof(NotificationCard), typeof(NotificationRow) })
        {
            var signature = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)))
                .Select(parameter => parameter.IsGenericType ? parameter.GetGenericArguments()[0] : parameter);
            Assert.DoesNotContain(signature, parameter => parameter.Namespace == typeof(NotificationItem).Namespace);
        }
    }

    [Fact]
    public void 以自訂記錄直接渲染不經過通知來源()
    {
        WpfTest.Run(() =>
        {
            var root = new NotificationCard();
            var item = new NotificationCardItem(7, "已還原預設片段", "SELECT 範本", "Loan.sql", "", "已寫回使用者資料夾",
                NotificationVisualStatus.Completed, "已完成", 1);
            root.Update(new[] { item }, true, false);
            var row = Assert.IsType<NotificationRow>(root.DetailsPanel.Children[0]);
            var text = Assert.IsType<StackPanel>(Assert.IsType<Grid>(row.Child).Children[1]);
            Assert.Equal("已還原預設片段 · SELECT 範本", ((TextBlock)text.Children[0]).Text);
            Assert.Equal("Loan.sql", root.ContextLabel.Text);
            Assert.Equal("已寫回使用者資料夾", ((TextBlock)text.Children[2]).Text);
            Assert.Equal("已完成 (1/1)", ((TextBlock)root.SummaryButton.Content).Text);
            Assert.Equal(NotificationVisualStatus.Completed, row.Status);
            Assert.Equal(Visibility.Collapsed, Badge(row).Visibility);
        });
    }

    [Fact]
    public void 合併後的重複次數顯示為徽章()
    {
        WpfTest.Run(() =>
        {
            var root = new NotificationCard();
            var center = new NotificationCenter();
            for (var index = 0; index < 3; index++)
                using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
                           NotificationOrigin.Typing, NotificationLevel.Info, "dbo.Loan", "Loan.sql")) { }
            var merged = Assert.Single(Cards(center));
            Assert.Equal(3, merged.Repeat);
            root.Update(new[] { merged }, true, false);
            var row = Assert.IsType<NotificationRow>(Assert.Single(root.DetailsPanel.Children));
            var badge = Badge(row);
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("×3", ((TextBlock)badge.Child).Text);
            Assert.Contains("3 次", (string)badge.ToolTip);
            // 徽章是中性膠囊，不是狀態色；狀態仍然只由圖示與狀態文字表達。
            root.Measure(new Size(250, 500)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
            Assert.True(badge.ActualWidth > 0);
            Assert.Equal("已載入欄位與定義 · dbo.Loan",
                ((TextBlock)((StackPanel)((Grid)row.Child).Children[1]).Children[0]).Text);

            // 次數退回 1 時徽章要收掉，不能留著上一輪的數字；列的身分不變。
            root.Update(new[] { merged with { Repeat = 1 } }, true, false);
            Assert.Same(row, root.DetailsPanel.Children[0]);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
        });
    }

    [Fact]
    public void 活動提示多主題多DPI與窄編輯區渲染()
    {
        WpfTest.Run(() =>
        {
            var probe = new DrawingVisual();
            using (var drawing = probe.RenderOpen()) drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1, 1));
            var sample = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
            sample.Render(probe);
            var pixel = new byte[4];
            sample.CopyPixels(pixel, 4, 0);
            Assert.SkipWhen(pixel[3] == 0, "目前工作階段無法渲染 WPF 像素；通知提示仍需 SSMS 多 DPI 視覺驗收。");
            var directory = Path.Combine(ThemeVisualTests.FindOutputDirectory() ?? AppContext.BaseDirectory, "notification-qa");
            Directory.CreateDirectory(directory);
            var root = SqlAssistChrome.CreateNotificationCard();
            var center = new NotificationCenter();
            using var running = center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "LibArchive.sql");
            running.Report("正在建立區塊結構");
            using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "Loan.sql")) { }
            using (var failure = center.Begin(NotificationCatalog.LoadingIndexes, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "Copy.sql")) failure.Fail();
            for (var i = 0; i < 4; i++)
                using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info,
                           "dbo.Lib_Reader", "Lib_Reader_LongFileName.sql")) { }
            root.Update(Cards(center), true, false);
            var resources = new ThemeResourceSet();
            root.Resources.MergedDictionaries.Add(resources.Resources);
            var frame = new Border { Padding = new Thickness(24), Child = root }
                .WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
            frame.Resources.MergedDictionaries.Add(resources.Resources);
            var common = new NotificationCenter();
            using var analysis = common.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql");
            analysis.Report("正在建立區塊結構");
            using (common.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql")) { }
            using (common.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql")) { }
            using (var failure = common.Begin(NotificationCatalog.LoadingIndexes, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql")) failure.Fail();
            foreach (var shared in new[] { false, true })
            {
            // 不同測試來源的流水號會重複，須換表面以符合正式環境單一通知來源的契約。
            root = new NotificationCard();
            root.Resources.MergedDictionaries.Add(resources.Resources);
            frame.Child = root;
            foreach (var theme in new[] { "light", "dark", "mango", "plum", "high-contrast" })
            foreach (var width in new[] { 180, 250 })
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                root.Update(Cards(shared ? common : center), true, false);
                resources.Update(ThemePaletteTests.ColorsFor(theme));
                root.SetOptions(true, theme == "high-contrast");
                root.MaxWidth = width;
                frame.Measure(new Size(width + 48, 448));
                frame.Arrange(new Rect(frame.DesiredSize));
                frame.UpdateLayout();
                var bitmap = new RenderTargetBitmap((width + 48) * dpi / 96, (int)Math.Ceiling(frame.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(frame);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"center-{(shared ? "common-" : "")}{theme}-{width}-{dpi}.png"));
                encoder.Save(file);
            }
            }
        });
    }

    [Fact]
    public void 短編輯區僅壓縮明細捲動區且長文字不撐寬()
    {
        WpfTest.Run(() =>
        {
            var root = SqlAssistChrome.CreateNotificationCard();
            root.Update(new[]
            {
                new NotificationCardItem(1, new string('長', 200), new string('名', 60), new string('源', 200), "", "",
                    NotificationVisualStatus.Running, "執行中", 1)
            }, true, false);
            root.Constrain(new Size(260, 200));
            root.Measure(new Size(root.MaxWidth, root.MaxHeight));
            root.Arrange(new Rect(root.DesiredSize));
            Assert.True(root.ActualWidth <= 250);
            Assert.True(root.ActualHeight <= 184);
            Assert.True(root.DetailScroll.ActualHeight <= root.DetailScroll.MaxHeight);
            Assert.True(root.CloseButton.ActualWidth > 0);
        });
    }

    [Fact]
    public void 活動表面切換主題保留文字不透明且高對比取消材質()
    {
        WpfTest.Run(() =>
        {
            var root = SqlAssistChrome.CreateNotificationCard();
            var summary = root.SummaryButton;
            var resources = new ThemeResourceSet();
            root.Resources.MergedDictionaries.Add(resources.Resources);
            var body = Assert.IsType<Border>(root.Child);
            SqlAssistChrome.SetNotificationSummary(summary, "背景執行中 · 已完成 1 / 3");
            root.DetailsPanel.Children.Add(SqlAssistChrome.CreateHint("執行中 · 物件清單", SqlAssistChrome.DefaultMetrics));
            foreach (var theme in new[] { "mango", "plum", "high-contrast", "cool-breeze" })
            {
                var colors = ThemePaletteTests.ColorsFor(theme);
                resources.Update(colors);
                root.SetOptions(true, theme == "high-contrast");
                var expected = colors[ThemeBrush.ListBackground];
                if (theme != "high-contrast") expected.A = 224;
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(body.Background).Color);
                Assert.Equal(1, body.Opacity);
                root.MaxWidth = 320;
                root.Measure(new Size(320, 240));
                root.Arrange(new Rect(root.DesiredSize));
                Assert.True(root.ActualWidth <= 320);
                Assert.True(summary.Focusable);
            }
        });
    }

    /// <summary>柔影疊在編輯器的 adornment 層上，捲動時不能每一影格都重跑模糊。</summary>
    [Fact]
    public void 柔影以點陣快取且高對比不留快取()
    {
        WpfTest.Run(() =>
        {
            var root = new NotificationCard();
            var cache = Assert.IsType<BitmapCache>(root.CacheMode);
            Assert.True(cache.RenderAtScale >= 1);
            Assert.NotNull(root.Effect);
            root.SetOptions(true, highContrast: true);
            Assert.Null(root.Effect);
            Assert.Null(root.CacheMode);
        });
    }

    [Fact]
    public void 精簡列表進度與覆蓋捲軸且收合切換圖示()
    {
        WpfTest.Run(() =>
        {
            var root = SqlAssistChrome.CreateNotificationCard();
            var summary = root.SummaryButton;
            var details = root.DetailsPanel;
            var center = new NotificationCenter();
            using var running = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "Lib_Reader.sql");
            // 主體各不相同才會保持六列；合併只收攏完全相同的那幾次。
            for (var i = 0; i < 6; i++)
                using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info,
                           "dbo.Loan" + i, "Loan.sql")) { }
            var items = Cards(center);
            root.Update(items, true, false);
            root.Measure(new Size(250, 500)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
            Assert.Equal(250, root.ActualWidth);
            Assert.Equal(188, root.ActualHeight);
            Assert.Equal(140, root.DetailScroll.ActualHeight);
            Assert.Equal("已完成 (6/7)", Assert.IsType<TextBlock>(summary.Content).Text);
            Assert.Equal(6d / 7, root.Progress.Value);
            Assert.Equal(2, root.Progress.ActualHeight);
            foreach (NotificationRow row in details.Children)
            {
                Assert.True(row.ActualHeight >= 28);
                Assert.Null(row.Background);
                Assert.Equal(Cursors.Arrow, row.Cursor);
            }
            var bar = Assert.IsType<ScrollBar>(root.DetailScroll.Template.FindName("PART_VerticalScrollBar", root.DetailScroll));
            Assert.Equal(3, bar.ActualWidth);
            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.True(bar.TranslatePoint(new Point(), root).Y - root.Progress.TranslatePoint(new Point(0, 2), root).Y >= 8);
            Assert.True(root.DetailScroll.ScrollableHeight > 0);
            root.DetailScroll.ScrollToBottom(); root.UpdateLayout();
            Assert.True(root.DetailScroll.VerticalOffset > 0);
            Assert.Equal(16, root.ToggleButton.ActualWidth);
            Assert.Equal(16, root.CloseButton.ActualHeight);
            Assert.Equal(Cursors.Hand, root.ToggleButton.Cursor);
            var expandedIcon = ((System.Windows.Shapes.Path)root.ToggleButton.Content).Data.ToString();
            root.Update(items, false, false);
            Assert.Equal(expandedIcon, ((System.Windows.Shapes.Path)root.ToggleButton.Content).Data.ToString());
            Assert.Equal(0, ((RotateTransform)((System.Windows.Shapes.Path)root.ToggleButton.Content).RenderTransform).Angle);
            Assert.Equal("展開明細", root.ToggleButton.ToolTip);
            Assert.Equal(Visibility.Collapsed, root.DetailScroll.Visibility);
            root.Update(items, true, false);
            Assert.Equal(expandedIcon, ((System.Windows.Shapes.Path)root.ToggleButton.Content).Data.ToString());
            Assert.Equal(180, ((RotateTransform)((System.Windows.Shapes.Path)root.ToggleButton.Content).RenderTransform).Angle);
            Assert.Equal("收合明細", root.ToggleButton.ToolTip);
            root.Update(Array.Empty<NotificationCardItem>(), true, false);
            Assert.Equal(0, root.Progress.Value);
        });
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, false, true)]
    public void 強制動畫只覆寫系統偏好(bool enabled, bool force, bool system, bool contrast, bool expected)
    {
        Assert.Equal(expected, SqlAssistChrome.NotificationMotionEnabled(enabled, force, system, contrast));
    }

    [Fact]
    public void 共用來源只顯示一次且訊息更新不重建列()
    {
        WpfTest.Run(() =>
        {
            var root = new NotificationCard();
            var center = new NotificationCenter();
            using var first = center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql");
            using var second = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql");
            root.Update(Cards(center), true, false);
            Assert.Equal("SQLQuery1.sql", root.ContextLabel.Text);
            Assert.Equal(Visibility.Visible, root.ContextLabel.Visibility);
            var row = Assert.IsType<NotificationRow>(root.DetailsPanel.Children[0]);
            var text = Assert.IsType<StackPanel>(Assert.IsType<Grid>(row.Child).Children[1]);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)text.Children[1]).Visibility);
            first.Report("正在建立區塊結構");
            root.Update(Cards(center), true, false);
            Assert.Same(row, root.DetailsPanel.Children[0]);
            Assert.Equal("正在建立區塊結構", ((TextBlock)text.Children[2]).Text);
            root.Measure(new Size(250, 500)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
            Assert.True(((TextBlock)text.Children[0]).ActualWidth >= 200);
            using var third = center.Begin(NotificationCatalog.LoadingIndexes, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "Loan.sql");
            root.Update(Cards(center), true, false);
            Assert.Equal(Visibility.Collapsed, root.ContextLabel.Visibility);
            Assert.Equal(Visibility.Visible, ((TextBlock)text.Children[1]).Visibility);
            root.Transition(true, true, SqlAssistChrome.NotificationMotionEnabled(true, true, false, false), NotificationPosition.TopRight);
            Assert.True(root.HasAnimatedProperties);
            root.StopMotion();
        });
    }

    /// <summary>
    /// 抬頭那一行回答「這一批在哪一份文件上」，只看有文件的那幾列。
    /// </summary>
    /// <remarks>
    /// 文件與資料庫共用一格的版本，只要混進一列資料庫名或一列沒有出處的背景工作，
    /// 抬頭的檔名就整個收掉——同一次查詢看起來是檔名時有時無。
    /// </remarks>
    [Fact]
    public void 沒有文件的工作不收掉抬頭的檔名()
    {
        WpfTest.Run(() =>
        {
            var root = new NotificationCard();
            var suggestions = new NotificationCardItem(1, "已準備建議清單", "資料行", "SQLQuery1.sql", "", "",
                NotificationVisualStatus.Completed, "已完成", 1);
            var objects = new NotificationCardItem(2, "已載入物件清單", "", "", "LibArchive", "",
                NotificationVisualStatus.Completed, "已完成", 1);
            var startup = new NotificationCardItem(3, "已初始化 SqlAssist", "", "", "", "",
                NotificationVisualStatus.Completed, "已完成", 1);
            root.Update(new[] { suggestions, objects, startup }, true, false);
            Assert.Equal("SQLQuery1.sql", root.ContextLabel.Text);
            Assert.Equal(Visibility.Visible, root.ContextLabel.Visibility);
            // 抬頭已經寫了文件，列上只留資料庫；兩者都沒有的那一列不預留空白行。
            Assert.Equal(Visibility.Collapsed, SourceLine(root, 0).Visibility);
            Assert.Equal("LibArchive", SourceLine(root, 1).Text);
            Assert.Equal(Visibility.Collapsed, SourceLine(root, 2).Visibility);

            // 指向兩份文件才收掉抬頭，並把文件補回各列。
            root.Update(new[] { suggestions, objects with { Id = 4, Document = "Loan.sql" } }, true, false);
            Assert.Equal(Visibility.Collapsed, root.ContextLabel.Visibility);
            Assert.Equal("SQLQuery1.sql", SourceLine(root, 0).Text);
            Assert.Equal("Loan.sql · LibArchive", SourceLine(root, 1).Text);
        });
    }

    internal static IReadOnlyList<NotificationCardItem> Cards(NotificationCenter center, SqlAssistSettings? settings = null) =>
        NotificationHost.Project(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue), settings ?? new SqlAssistSettings()).Items;

    internal static System.Windows.Shapes.Path Icon(NotificationRow row) =>
        (System.Windows.Shapes.Path)((Grid)row.Child).Children[0];

    private static Border Badge(NotificationRow row) => (Border)((Grid)row.Child).Children[2];

    /// <summary>列上那一行出處：標題下方、訊息上方。</summary>
    private static TextBlock SourceLine(NotificationCard root, int index)
    {
        var row = Assert.IsType<NotificationRow>(root.DetailsPanel.Children[index]);
        var text = Assert.IsType<StackPanel>(Assert.IsType<Grid>(row.Child).Children[1]);
        return (TextBlock)text.Children[1];
    }
}
