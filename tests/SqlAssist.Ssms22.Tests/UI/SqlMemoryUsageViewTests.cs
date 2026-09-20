using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private static (Border Host, SqlMemoryUsageView View, ThemeResourceSet Palette) Host(bool diagnostics = true)
    {
        var palette = new ThemeResourceSet();
        var view = new SqlMemoryUsageView(diagnostics);
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
    public void BusyDisablesEveryActionAndUnavailableHidesStaleNumbers()
    {
        WpfTest.Run(() =>
        {
            var (host, view, _) = Host();
            view.ShowSummary(Summary(10 * Megabyte), motion: false);
            Layout(host, 440);
            SqlMemoryUsageAction? requested = null;
            view.ActionRequested += (_, action) => requested = action;

            view.ActionButton(SqlMemoryUsageAction.Cleanup).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(SqlMemoryUsageAction.Cleanup, requested);

            // 自我測試也要停用：它與維護、清除搶同一份儲存，不能同時跑。
            view.SetBusy("正在清除紀錄…");
            Assert.All(Actions(view), action => Assert.False(view.ActionButton(action).IsEnabled));
            view.SetBusy(null);
            Assert.All(Actions(view), action => Assert.True(view.ActionButton(action).IsEnabled));

            // 讀取失敗保留舊畫面並說明；儲存停用則整頁收起，下一次讀取重新走第一次載入。
            view.ShowFailure("讀取用量失敗");
            Assert.True(Shown(Descendants<TextBlock>(view).Single(text => text.Text == "讀取用量失敗"), view));
            Assert.True(Shown(Descendants<TextBlock>(view).Single(text => text.Text == "狀態良好"), view));
            view.Clear();
            Assert.False(Shown(Descendants<TextBlock>(view).Single(text => text.Text == "狀態良好"), view));
            Assert.DoesNotContain(Descendants<TextBlock>(view), text => text.Text == "讀取用量失敗");
            view.BeginLoad();
            Assert.True(view.IsLoading);
        });
    }

    /// <summary>
    /// 自我測試在自己的「診斷」卡片上，而且只有詳細紀錄打開時才建立。
    /// </summary>
    /// <remarks>
    /// 併進「整理」那一排會讓人以為它會刪資料或改檔案大小；它兩者都不做。
    /// </remarks>
    [Fact]
    public void SelfTestLivesInItsOwnDiagnosticsCardAndOnlyWithVerboseLogging()
    {
        WpfTest.Run(() =>
        {
            var (host, view, _) = Host();
            view.ShowSummary(Summary(10 * Megabyte), motion: false);
            Layout(host, 740);
            Assert.True(view.HasAction(SqlMemoryUsageAction.SelfTest));
            Assert.Contains("診斷", Descendants<TextBlock>(view).Select(text => text.Text));

            SqlMemoryUsageAction? requested = null;
            view.ActionRequested += (_, action) => requested = action;
            view.ActionButton(SqlMemoryUsageAction.SelfTest).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(SqlMemoryUsageAction.SelfTest, requested);

            var (quiet, quietView, _) = Host(diagnostics: false);
            quietView.ShowSummary(Summary(10 * Megabyte), motion: false);
            Layout(quiet, 740);
            Assert.False(quietView.HasAction(SqlMemoryUsageAction.SelfTest));
            Assert.DoesNotContain("診斷", Descendants<TextBlock>(quietView).Select(text => text.Text));
        });
    }

    [Fact]
    public void UsagePageRendersAcrossThemesWidthsAndDpiWithoutHorizontalOverflow()
    {
        WpfTest.Run(() =>
        {
            var (host, view, palette) = Host();
            var directory = ThemeVisualTests.FindOutputDirectory();
            // 沒有自己的頁首：分頁與工具列已經是抬頭，第一個元素就是內容。
            Assert.DoesNotContain(Descendants<TextBlock>(view), text => text.Text is "Usage" or "清單" or "重新整理" or "保留設定");
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
                view.SetMessage(name == "empty" ? "讀取用量失敗：資料庫忙碌中。" : "");
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
    public void UsageTabBadgeAppearsOnlyAboveNormalAndDescribesTheSeverity()
    {
        WpfTest.Run(() =>
        {
            var usage = SqlAssistChrome.CreateMemoryUsageTab();
            var badge = SqlAssistChrome.UsageBadge(usage);
            Assert.NotNull(badge);
            Assert.Equal(Visibility.Collapsed, badge!.Visibility);

            SqlAssistChrome.SetUsageBadge(usage, SqlMemoryUsageSeverity.Critical, motion: false);
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("Usage：容量接近或超過上限", usage.ToolTip);
            Assert.Equal("Usage：容量接近或超過上限", System.Windows.Automation.AutomationProperties.GetHelpText(usage));

            SqlAssistChrome.SetUsageBadge(usage, SqlMemoryUsageSeverity.Normal, motion: false);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.Equal("Usage", usage.ToolTip);
        });
    }

    [Fact]
    public void NarrowToolbarCollapsesTabLabelsInsteadOfWrappingTheTabs()
    {
        WpfTest.Run(() =>
        {
            var palette = new ThemeResourceSet();
            var tabs = new TabControl();
            tabs.Items.Add(SqlAssistChrome.CreateIconTab(SqlIcon.History, "History"));
            tabs.Items.Add(SqlAssistChrome.CreateIconTab(SqlIcon.Favorite, "Favorites"));
            tabs.Items.Add(SqlAssistChrome.CreateMemoryUsageTab());
            tabs.SelectedIndex = 2;
            var settings = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
            var toolbar = SqlAssistChrome.CreateMemoryToolbar(tabs, settings);
            var host = new Border { Child = toolbar };
            host.Resources.MergedDictionaries.Add(palette.Resources);
            TextBlock Label(int index) => (TextBlock)((DockPanel)((TabItem)tabs.Items[index]).Header).Children[1];
            var settingsLabel = (TextBlock)((Panel)settings.Content).Children[1];

            foreach (var (width, labels) in new[] { (740, true), (220, false), (740, true) })
            {
                Layout(host, width, 40); Layout(host, width, 40);
                Assert.Equal(labels, Label(0).Visibility == Visibility.Visible);
                Assert.Equal(labels, Label(2).Visibility == Visibility.Visible);
                Assert.InRange(tabs.ActualHeight, 1, 32);
                Assert.InRange(toolbar.Children.OfType<StackPanel>().Single().TranslatePoint(new Point(), host).X, tabs.ActualWidth, width);
            }

            // 先收設定的文字，再收分頁文字；兩者不同時消失，窄窗仍看得出現在在哪一個分頁。
            Layout(host, 360, 40); Layout(host, 360, 40);
            Assert.Equal(Visibility.Collapsed, settingsLabel.Visibility);
            Assert.Equal(Visibility.Visible, Label(1).Visibility);
        });
    }

    /// <summary>這個畫面上真的有按鈕的動作；詳細紀錄關著時不含自我測試。</summary>
    private static System.Collections.Generic.IEnumerable<SqlMemoryUsageAction> Actions(SqlMemoryUsageView view) =>
        Enum.GetValues(typeof(SqlMemoryUsageAction)).Cast<SqlMemoryUsageAction>().Where(view.HasAction);

    private static bool Shown(DependencyObject element, DependencyObject root)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
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
