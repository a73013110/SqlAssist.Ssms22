using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlMemoryUsageViewTests
{
    private const long Megabyte = 1024L * 1024;
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    static SqlMemoryUsageViewTests() => SqlIconImage.Factory = icon => new Border { Width = 16, Height = 16, Tag = icon };

    private static SqlMemoryUsageSummary Summary(long contentBytes, long executionEvents = 4000, int serverCount = 3,
        params SqlMemoryActivity[] activities)
    {
        var servers = Enumerable.Range(0, serverCount)
            .Select(index => new SqlMemoryUsageShare(index == 0 ? "LibraryServer-with-a-very-long-name-for-trimming" : "Branch" + index, 400 - index * 90))
            .ToArray();
        var report = new SqlMemoryUsageReport(new SqlMemoryUsage(contentBytes, contentBytes * 2, 3 * Megabyte), 12 * Megabyte,
            new SqlMemoryUsageCounts(1200, executionEvents, 300, 4, 1, 90, 12, 60, 8, 1500), servers,
            serverCount == 0 ? null : Now.AddDays(-30), serverCount == 0 ? null : Now);
        var plan = new SqlRetentionSettings(TimeSpan.FromDays(30), TimeSpan.FromDays(30), TimeSpan.FromDays(7),
            100 * Megabyte, 10000, 200, 50);
        return SqlMemoryUsageSummary.Create(new SqlMemoryUsageSnapshot(report, null, plan,
            new SqlMemoryMaintenanceOverview(Now.AddMinutes(20), Now.AddMinutes(-40), 0, false, true), activities), Now);
    }

    private static (Border Host, SqlMemoryUsageView View, ThemeResourceSet Palette) Host()
    {
        var palette = new ThemeResourceSet();
        var view = new SqlMemoryUsageView();
        var host = new Border { Child = view, Padding = new Thickness(8) };
        host.Resources.MergedDictionaries.Add(palette.Resources);
        host.SetResourceReference(Border.BackgroundProperty, ThemeBrush.WindowBackground);
        return (host, view, palette);
    }

    private static void Layout(FrameworkElement element, double width, double height = 900)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    [Fact]
    public void MeterLandsOnTheClampedValueAndReleasesTheAnimatedProperty()
    {
        WpfTest.Run(() =>
        {
            var meter = new SqlUsageMeter();
            meter.SetValue(0.4, SqlMemoryUsageSeverity.Normal, motion: false);
            Assert.Equal(0.4, meter.Value, 3);

            // 動畫播放中基底值已經是終值；關掉動畫再設定時不會被上一段動畫壓住。
            meter.SetValue(1.8, SqlMemoryUsageSeverity.Critical, motion: true);
            meter.SetValue(1.8, SqlMemoryUsageSeverity.Critical, motion: false);
            Assert.Equal(1, meter.Value, 3);
            Assert.Equal(SqlMemoryUsageSeverity.Critical, meter.Severity);

            meter.SetValue(null, SqlMemoryUsageSeverity.Normal, motion: false);
            Assert.Equal(0, meter.Value, 3);
            Assert.Equal("不限", System.Windows.Automation.AutomationProperties.GetItemStatus(meter));

            meter.IsIndeterminate = true;
            meter.SetValue(0.2, SqlMemoryUsageSeverity.Warning, motion: false);
            Assert.False(meter.IsIndeterminate);
        });
    }

    [Fact]
    public void SeverityMapsToDedicatedMeterRolesThatStayVisibleOnTheSurface()
    {
        Assert.Equal(ThemeBrush.MeterNormal, SqlUsageMeter.Brush(SqlMemoryUsageSeverity.Normal));
        Assert.Equal(ThemeBrush.MeterWarning, SqlUsageMeter.Brush(SqlMemoryUsageSeverity.Warning));
        Assert.Equal(ThemeBrush.MeterCritical, SqlUsageMeter.Brush(SqlMemoryUsageSeverity.Critical));
        foreach (var mode in new[] { "light", "dark", "mango", "forest" })
        {
            var colors = ThemePaletteTests.ColorsFor(mode);
            foreach (var role in new[] { ThemeBrush.MeterNormal, ThemeBrush.MeterWarning, ThemeBrush.MeterCritical })
                Assert.True(ThemeColorMath.Contrast(colors[role], colors[ThemeBrush.ListBackground]) >= 3, mode + "/" + role);
        }
    }

    [Fact]
    public void SummaryReachesTheControlsAndRefreshReusesQuotaMeters()
    {
        WpfTest.Run(() =>
        {
            var (host, view, _) = Host();
            Assert.True(view.ActionButton(SqlMemoryUsageAction.Maintain).IsEnabled);
            view.BeginLoad();
            Assert.True(view.IsLoading);

            view.ShowSummary(Summary(75 * Megabyte), motion: false);
            Layout(host, 740);
            Assert.False(view.IsLoading);
            var texts = Descendants<TextBlock>(view).Select(text => text.Text).ToList();
            Assert.Contains("75 MB / 100 MB", texts);
            Assert.Contains("容量偏高", texts);
            Assert.Contains("1,200", texts);
            var meters = Descendants<SqlUsageMeter>(view).ToList();
            var firstQuota = meters.First(meter => meter.Height == 4);

            view.ShowSummary(Summary(20 * Megabyte, executionEvents: 9500), motion: false);
            Layout(host, 740);
            // 同一個配額沿用同一個量表，重新整理時才能從舊值滑到新值。
            Assert.Same(firstQuota, Descendants<SqlUsageMeter>(view).First(meter => meter.Height == 4));
            Assert.Equal(0.95, firstQuota.Value, 2);
            Assert.Equal(SqlMemoryUsageSeverity.Critical, firstQuota.Severity);
            Assert.Contains("狀態良好", Descendants<TextBlock>(view).Select(text => text.Text));
        });
    }

    [Fact]
    public void BusyDisablesEveryActionAndEscapeReturnsOnlyWhenIdle()
    {
        WpfTest.Run(() =>
        {
            var (host, view, _) = Host();
            view.ShowSummary(Summary(10 * Megabyte), motion: false);
            Layout(host, 440);
            var backs = 0;
            view.BackRequested += (_, _) => backs++;
            var requested = SqlMemoryUsageAction.Settings;
            view.ActionRequested += (_, action) => requested = action;

            view.ActionButton(SqlMemoryUsageAction.Cleanup).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(SqlMemoryUsageAction.Cleanup, requested);

            view.SetBusy("正在清除紀錄…");
            Assert.All(Enum.GetValues(typeof(SqlMemoryUsageAction)).Cast<SqlMemoryUsageAction>(),
                action => Assert.False(view.ActionButton(action).IsEnabled));
            Escape(host, view);
            Assert.Equal(0, backs);

            view.SetBusy(null);
            Assert.All(Enum.GetValues(typeof(SqlMemoryUsageAction)).Cast<SqlMemoryUsageAction>(),
                action => Assert.True(view.ActionButton(action).IsEnabled));
            Escape(host, view);
            Assert.Equal(1, backs);
        });
    }

    [Fact]
    public void UsagePageRendersAcrossThemesWidthsAndDpiWithoutHorizontalOverflow()
    {
        WpfTest.Run(() =>
        {
            var (host, view, palette) = Host();
            var directory = ThemeVisualTests.FindOutputDirectory();
            foreach (var mode in new[] { "light", "dark", "high-contrast" })
            foreach (var (summary, name) in new[]
            {
                (Summary(95 * Megabyte, activities: new SqlMemoryActivity(Now, SqlMemoryActivityKind.Cleanup, 1234, 3 * Megabyte)), "critical"),
                (Summary(Megabyte, serverCount: 0), "empty"),
            })
            foreach (var width in new[] { 300, 460, 740 })
            {
                palette.Update(ThemePaletteTests.ColorsFor(mode));
                view.ShowSummary(summary, motion: false);
                view.SetMessage(name == "empty" ? "SQL Memory 尚未就緒；可由設定啟用或重新啟用。" : "");
                Layout(host, width);
                // 窄窗的統計改成單欄，操作按鈕換行而不是撐出橫向捲動。
                var stats = Descendants<UniformGrid>(view).Single();
                Assert.Equal(width >= 460 ? 2 : 1, stats.Columns);
                foreach (var button in Descendants<Button>(view).Where(button => button.IsVisible))
                    Assert.InRange(button.TranslatePoint(new Point(button.ActualWidth, 0), host).X, 0, width + 0.5);
                foreach (var dpi in new[] { 96, 144 })
                {
                    var bitmap = new RenderTargetBitmap(width * dpi / 96, 900 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    if (directory is null) continue;
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(directory, $"sql-memory-usage-{name}-{mode}-{width}-{dpi}.png"));
                    encoder.Save(file);
                }
            }
        });
    }

    [Fact]
    public void ToolbarBadgeAppearsOnlyAboveNormalAndDescribesTheSeverity()
    {
        WpfTest.Run(() =>
        {
            var usage = SqlAssistChrome.CreateButton("用量", SqlAssistChrome.DefaultMetrics);
            var tabs = new TabControl();
            SqlAssistChrome.CreateMemoryToolbar(tabs, SqlAssistChrome.CreateMemoryConnectionButton(), usage,
                SqlAssistChrome.CreateButton("重新整理", SqlAssistChrome.DefaultMetrics), SqlAssistChrome.CreateButton("設定", SqlAssistChrome.DefaultMetrics));
            var badge = SqlAssistChrome.UsageBadge(usage);
            Assert.NotNull(badge);
            Assert.Equal(Visibility.Collapsed, badge!.Visibility);

            SqlAssistChrome.SetUsageBadge(usage, SqlMemoryUsageSeverity.Critical, motion: false);
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("用量：容量接近或超過上限", usage.ToolTip);
            Assert.Equal("用量：容量接近或超過上限", System.Windows.Automation.AutomationProperties.GetHelpText(usage));

            SqlAssistChrome.SetUsageBadge(usage, SqlMemoryUsageSeverity.Normal, motion: false);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.Equal("用量", usage.ToolTip);
        });
    }

    private static void Escape(Border host, UIElement target)
    {
        // 隱藏的 presentation source 供 WPF 路由鍵盤事件，不開使用者可見視窗。
        using var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("SQL Memory usage keyboard test") { Width = 440, Height = 300, WindowStyle = 0 });
        source.RootVisual = host;
        target.RaiseEvent(new KeyEventArgs(new TestKeyboardDevice(), source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        source.RootVisual = null;
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
