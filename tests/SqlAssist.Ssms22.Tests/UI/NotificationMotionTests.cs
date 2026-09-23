using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Notifications;
using SqlAssist.Ssms22.UI;
using Xunit;
using Path = System.Windows.Shapes.Path;

namespace SqlAssist.Ssms22.Tests.UI;

/// <summary>通知島上的狀態回饋：短震、微彈出、進度與材質；彈簧本身見 <see cref="SpringMotionTests"/>。</summary>
public sealed class NotificationMotionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 失敗走獨立通道：種類隱藏的工作失敗時照樣算進失敗數，島嶼短震一次。
    /// </summary>
    /// <remarks>
    /// 只數看得見的列的版本，較早啟動、種類預設隱藏的工作後來失敗了也不會震，
    /// 使用者要等展開才發現。
    /// </remarks>
    [Fact]
    public void 較早啟動的隱藏工作稍後失敗也短震()
    {
        var center = new NotificationCenter();
        var hidden = center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Analysis, NotificationOrigin.Typing, NotificationLevel.Info);
        using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info)) { }
        var state = new NotificationIslandState();
        Feed(state, NotificationChromeTests.Content(center));
        Assert.Equal(0, state.ShakeCount);
        hidden.Fail(); hidden.Dispose();
        Feed(state, NotificationChromeTests.Content(center));
        Assert.Equal(1, state.ShakeCount);
        Assert.True(state.Warning);
        // 同一個失敗重畫不再震。
        Feed(state, NotificationChromeTests.Content(center));
        Assert.Equal(1, state.ShakeCount);
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
                    var island = new NotificationIsland();
                    island.Resources.MergedDictionaries.Add(resources.Resources);
                    island.SetOptions(glass, false);
                    NotificationChromeTests.Expand(island, NotificationChromeTests.Content(center), motion: false);
                    var backdrop = new Grid { Width = 380, Height = 210, Background = new SolidColorBrush(colors[ThemeBrush.WindowBackground]) };
                    backdrop.Children.Add(new TextBlock
                    {
                        Text = "SELECT CopyNo FROM Copy;\n\nSELECT * FROM Loan;\n\nSELECT CopyNo FROM Copy;\n\nSELECT * FROM Loan;",
                        FontFamily = new FontFamily("Consolas"), FontSize = 14, Margin = new Thickness(8),
                        Foreground = new SolidColorBrush(colors[ThemeBrush.ListForeground])
                    });
                    island.Margin = new Thickness(20, 0, 20, 24);
                    backdrop.Children.Add(island);
                    backdrop.Measure(new Size(380, 210)); backdrop.Arrange(new Rect(0, 0, 380, 210)); backdrop.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(380, 210, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(backdrop);
                    var pixels = new byte[380 * 210 * 4];
                    bitmap.CopyPixels(pixels, 380 * 4, 0);
                    Assert.SkipWhen(pixels[3] == 0, "目前工作階段無法渲染 WPF 像素。");
                    if (glass) glassPixels = pixels;
                    else Assert.NotEqual(glassPixels, pixels);
                    comparison.Children.Add(backdrop);
                }

                comparison.Measure(new Size(760, 210)); comparison.Arrange(new Rect(0, 0, 760, 210)); comparison.UpdateLayout();
                var output = new RenderTargetBitmap(760, 210, 96, 96, PixelFormats.Pbgra32);
                output.Render(comparison);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
                using var file = File.Create(System.IO.Path.Combine(directory, $"island-material-{theme}.png"));
                encoder.Save(file);
            }
        });
    }

    [Fact]
    public void 列完成時微彈出且不因別列更新重播()
    {
        WpfTest.Run(() =>
        {
            var center = new NotificationCenter();
            var first = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            var second = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            var island = new NotificationIsland();
            NotificationChromeTests.Expand(island, NotificationChromeTests.Content(center), motion: false);
            var firstRow = (NotificationRow)island.Rows.Children[0];
            first.Dispose();
            NotificationChromeTests.Expand(island, NotificationChromeTests.Content(center), motion: true);
            Assert.Same(firstRow, island.Rows.Children[0]);
            Assert.True(Scale(firstRow).HasAnimatedProperties);
            // 清掉第一列播過的回饋，下一輪有沒有重播就看得出來，不必靠計時。
            Scale(firstRow).BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Scale(firstRow).BeginAnimation(ScaleTransform.ScaleYProperty, null);
            second.Dispose();
            NotificationChromeTests.Expand(island, NotificationChromeTests.Content(center), motion: true);
            // 第二列完成才播第二列；第一列的狀態沒變，不跟著重播。
            Assert.False(Scale(firstRow).HasAnimatedProperties);
            Assert.True(Scale((NotificationRow)island.Rows.Children[1]).HasAnimatedProperties);
            foreach (NotificationRow row in island.Rows.Children) Assert.True(double.IsPositiveInfinity(row.MaxHeight));
            island.StopMotion();
            foreach (NotificationRow row in island.Rows.Children) Assert.False(Scale(row).HasAnimatedProperties);
        });
    }

    [Fact]
    public void 進度從目前值接續且動畫關閉時直接到位()
    {
        WpfTest.Run(() =>
        {
            var center = new NotificationCenter();
            var scope = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            using var next = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info);
            var island = new NotificationIsland();
            NotificationChromeTests.Expand(island, NotificationChromeTests.Content(center), motion: false);
            Assert.Equal(0, island.Progress.ScaleX);
            scope.Dispose();
            NotificationChromeTests.Expand(island, NotificationChromeTests.Content(center), motion: true);
            Assert.True(island.Progress.HasAnimatedProperties);
            Assert.Equal(0.5, (double)island.Progress.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
            Pump(450);
            Assert.Equal(0.5, island.Progress.ScaleX);
            next.Dispose();
            NotificationChromeTests.Expand(island, NotificationChromeTests.Content(center), motion: false);
            Assert.False(island.Progress.HasAnimatedProperties);
            Assert.Equal(1, island.Progress.ScaleX);
        });
    }

    [Fact]
    public void 漸層跟隨色系切換且各端點可辨認()
    {
        WpfTest.Run(() =>
        {
            var resources = new ThemeResourceSet();
            Brush? previous = null;
            foreach (var theme in new[] { "mango", "cool-breeze", "plum", "forest", "high-contrast" })
            {
                var colors = ThemePaletteTests.ColorsFor(theme);
                resources.Update(colors);
                var gradient = Assert.IsType<LinearGradientBrush>(resources.Resources[ThemeResourceSet.NotificationSpinnerKey]);
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

    private static void Feed(NotificationIslandState state, NotificationIslandContent content) =>
        state.Update(new NotificationIslandInput(content.Activities.Count, content.Running, content.Failed, content.Prompts.Count), Now);

    // 列上不再有循環旋轉，縮放是變換群組的第一個。
    private static ScaleTransform Scale(NotificationRow row) =>
        (ScaleTransform)((TransformGroup)((Path)((Grid)row.Child).Children[0]).RenderTransform).Children[0];

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
