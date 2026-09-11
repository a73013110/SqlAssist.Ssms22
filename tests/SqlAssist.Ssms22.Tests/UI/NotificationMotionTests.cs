using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using System.Collections.Generic;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class NotificationMotionTests
{
    [Fact]
    public void 較早啟動的隱藏工作稍後失敗也回饋整體動畫()
    {
        WpfTest.Run(() =>
        {
            var center = new NotificationCenter();
            var hidden = center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Analysis, NotificationOrigin.Typing, NotificationLevel.Info);
            using (var later = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info)) later.Fail();
            using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info)) { }
            var settings = new SqlAssistSettings();
            IReadOnlyList<NotificationCardItem> Read() =>
                NotificationHost.Project(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue), settings).Items;
            var root = new NotificationCard();
            root.Update(Read(), true, false);
            Assert.False(root.StatusShake.HasAnimatedProperties);
            hidden.Fail(); hidden.Dispose();
            root.Update(Read(), true, true);
            Assert.True(root.StatusShake.HasAnimatedProperties);
            root.StopMotion();
        });
    }

    [Fact]
    public void 玻璃與實色在編輯器底紋上有可見差異()
    {
        WpfTest.Run(() =>
        {
            var directory = System.IO.Path.Combine(ThemeVisualTests.FindOutputDirectory() ?? AppContext.BaseDirectory, "notification-qa");
            Directory.CreateDirectory(directory);
            foreach (var theme in new[] { "light", "dark" })
            {
                var colors = ThemePaletteTests.ColorsFor(theme);
                var resources = new ThemeResourceSet();
                resources.Update(colors);
                var center = new NotificationCenter();
                using var scope = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "LibArchive.sql");
                scope.Report("正在整理欄位資訊");
                var comparison = new StackPanel { Orientation = Orientation.Horizontal };
                byte[]? glassPixels = null;
                foreach (var glass in new[] { true, false })
                {
                    var root = new NotificationCard { VerticalAlignment = VerticalAlignment.Top };
                    root.Resources.MergedDictionaries.Add(resources.Resources);
                    root.SetOptions(glass, false);
                    root.Update(NotificationChromeTests.Cards(center), true, false);
                    var backdrop = new Grid { Width = 290, Height = 210, Background = new SolidColorBrush(colors[ThemeBrush.WindowBackground]) };
                    var sql = new TextBlock
                    {
                        Text = "SELECT CopyNo FROM Copy;\n\nSELECT * FROM Loan;\n\nSELECT CopyNo FROM Copy;\n\nSELECT * FROM Loan;",
                        FontFamily = new FontFamily("Consolas"), FontSize = 14, Margin = new Thickness(8),
                        Foreground = new SolidColorBrush(colors[ThemeBrush.ListForeground])
                    };
                    backdrop.Children.Add(sql);
                    root.Margin = new Thickness(20, 24, 20, 0);
                    backdrop.Children.Add(root);
                    backdrop.Measure(new Size(290, 210)); backdrop.Arrange(new Rect(0, 0, 290, 210)); backdrop.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(290, 210, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(backdrop);
                    var pixels = new byte[290 * 210 * 4];
                    bitmap.CopyPixels(pixels, 290 * 4, 0);
                    Assert.SkipWhen(pixels[3] == 0, "目前工作階段無法渲染 WPF 像素。");
                    if (glass) glassPixels = pixels;
                    else Assert.NotEqual(glassPixels, pixels);
                    comparison.Children.Add(backdrop);
                }
                comparison.Measure(new Size(580, 210)); comparison.Arrange(new Rect(0, 0, 580, 210)); comparison.UpdateLayout();
                var output = new RenderTargetBitmap(580, 210, 96, 96, PixelFormats.Pbgra32);
                output.Render(comparison);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
                using var file = File.Create(System.IO.Path.Combine(directory, $"center-material-{theme}.png"));
                encoder.Save(file);
            }
        });
    }

    [Fact]
    public void 所有列完成與總結都播放動畫且短工作不被高度遮住()
    {
        WpfTest.Run(() =>
        {
            var center = new NotificationCenter();
            var first = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            var second = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            var root = new NotificationCard();
            root.Update(NotificationChromeTests.Cards(center), true, false);
            var firstRow = (NotificationRow)root.DetailsPanel.Children[0];
            first.Dispose();
            root.Update(NotificationChromeTests.Cards(center), true, true);
            Assert.Same(firstRow, root.DetailsPanel.Children[0]);
            Assert.True(Scale(firstRow).HasAnimatedProperties);
            second.Dispose();
            using (center.Begin(NotificationCatalog.LoadingSystemObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info)) { }
            root.Update(NotificationChromeTests.Cards(center), true, true);
            foreach (NotificationRow row in root.DetailsPanel.Children)
            {
                Assert.True(Scale(row).HasAnimatedProperties);
                Assert.True(double.IsPositiveInfinity(row.MaxHeight));
            }
            Assert.True(root.StatusScale.HasAnimatedProperties);
            Assert.False(root.StatusRotation.HasAnimatedProperties);
            Pump(450);
            Assert.Equal(1, root.StatusScale.ScaleX);
            root.StopMotion();
            Assert.False(root.StatusScale.HasAnimatedProperties);
            using (center.Begin(NotificationCatalog.LoadingCollations, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info)) { }
            root.Update(NotificationChromeTests.Cards(center), true, true);
            Assert.True(root.StatusScale.HasAnimatedProperties);
            root.Update(NotificationChromeTests.Cards(center), true, false);
            Assert.False(root.StatusScale.HasAnimatedProperties);
            foreach (NotificationRow row in root.DetailsPanel.Children) Assert.False(Scale(row).HasAnimatedProperties);
            root.StopMotion();
        });
    }

    [Fact]
    public void 進度增減平滑且收合可反向並完成後停止()
    {
        WpfTest.Run(() =>
        {
            var center = new NotificationCenter();
            var scope = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            var root = new NotificationCard();
            root.Update(NotificationChromeTests.Cards(center), true, false);
            Layout(root);
            scope.Dispose();
            root.Update(NotificationChromeTests.Cards(center), true, true);
            Assert.True(root.Progress.HasAnimatedProperties);
            Assert.Equal(1d, (double)root.Progress.GetAnimationBaseValue(RangeBase.ValueProperty));
            Pump(450);
            Assert.Equal(1, root.Progress.Value);
            using var next = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            var items = NotificationChromeTests.Cards(center);
            root.Update(items, false, true);
            Assert.Equal(0.5, (double)root.Progress.GetAnimationBaseValue(RangeBase.ValueProperty));
            Assert.Equal(Visibility.Visible, root.DetailScroll.Visibility);
            Assert.True(root.DetailScroll.HasAnimatedProperties);
            Pump(80);
            root.Update(items, true, true);
            Pump(450);
            Layout(root);
            Assert.Equal(Visibility.Visible, root.DetailScroll.Visibility);
            Assert.True(double.IsNaN(root.DetailScroll.Height));
            Assert.True(root.DetailScroll.ActualHeight > 0);
            Assert.Equal(0.5, root.Progress.Value);
            root.Update(items, false, true);
            Pump(400);
            Assert.Equal(Visibility.Collapsed, root.DetailScroll.Visibility);
            root.StopMotion();
            Assert.False(root.Progress.HasAnimatedProperties);
        });
    }

    [Fact]
    public void 首行與總結圖示共用固定畫布與垂直軸線()
    {
        WpfTest.Run(() =>
        {
            var root = new NotificationCard();
            var center = new NotificationCenter();
            using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info)) { }
            root.Update(NotificationChromeTests.Cards(center), true, false);
            Layout(root);
            var row = (NotificationRow)root.DetailsPanel.Children[0];
            var grid = (Grid)row.Child;
            var icon = (Path)grid.Children[0];
            var title = (TextBlock)((StackPanel)grid.Children[1]).Children[0];
            Assert.Equal(root.StatusIcon.TranslatePoint(new Point(6, 6), root).X, icon.TranslatePoint(new Point(6, 6), root).X);
            Assert.Equal(title.TranslatePoint(new Point(0, 8), row).Y, icon.TranslatePoint(new Point(6, 6), row).Y);
            Assert.Equal(Stretch.None, icon.Stretch);
            Assert.Equal(root.StatusIcon.Data.Bounds, icon.Data.Bounds);
            var chevron = (Path)root.ToggleButton.Content;
            Assert.Equal(Stretch.None, chevron.Stretch);
            Assert.Equal(new Point(5, 5), new Point(chevron.Data.Bounds.X + chevron.Data.Bounds.Width / 2,
                chevron.Data.Bounds.Y + chevron.Data.Bounds.Height / 2));
        });
    }

    [Fact]
    public void 漸層跟隨色系切換且各端點可辨認()
    {
        WpfTest.Run(() =>
        {
            var root = new NotificationCard();
            var resources = new ThemeResourceSet();
            root.Resources.MergedDictionaries.Add(resources.Resources);
            Brush? previous = null;
            foreach (var theme in new[] { "mango", "cool-breeze", "plum", "forest", "high-contrast" })
            {
                var colors = ThemePaletteTests.ColorsFor(theme);
                resources.Update(colors);
                var gradient = Assert.IsType<LinearGradientBrush>(root.Progress.Foreground);
                if (theme == "high-contrast")
                    Assert.NotEqual(gradient.GradientStops[0].Color, Assert.IsType<SolidColorBrush>(root.Progress.Background).Color);
                Assert.NotSame(previous, gradient);
                foreach (var stop in gradient.GradientStops)
                    Assert.True(ThemeColorMath.Contrast(stop.Color, colors[ThemeBrush.ListBackground]) >= 3);
                var glass = (SolidColorBrush)resources.Resources[ThemeResourceSet.NotificationGlassKey];
                var dim = (SolidColorBrush)resources.Resources[ThemeResourceSet.NotificationDimKey];
                var sheen = (LinearGradientBrush)resources.Resources[ThemeResourceSet.NotificationSheenKey];
                foreach (var backdrop in new[] { Colors.Black, Colors.White })
                {
                    var background = ThemeColorMath.Composite(sheen.GradientStops[0].Color, ThemeColorMath.Composite(glass.Color, backdrop));
                    Assert.True(ThemeColorMath.Contrast(colors[ThemeBrush.ListForeground], background) >= 4.5);
                    Assert.True(ThemeColorMath.Contrast(dim.Color, background) >= 4.5);
                }
                previous = gradient;
            }
        });
    }

    // 列上不再有循環旋轉，縮放是變換群組的第一個。
    private static ScaleTransform Scale(NotificationRow row) =>
        (ScaleTransform)((TransformGroup)((Path)((Grid)row.Child).Children[0]).RenderTransform).Children[0];

    private static void Layout(NotificationCard root)
    {
        root.Measure(new Size(250, 500));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
