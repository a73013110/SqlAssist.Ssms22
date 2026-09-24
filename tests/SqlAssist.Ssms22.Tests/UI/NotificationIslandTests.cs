using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Notifications;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class NotificationIslandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 膠囊依摘要定寬並限制在一百六到三百二()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            Render(island, Scenario.Running, motion: false);
            Assert.Equal(NotificationIslandShape.Compact, island.Shape);
            Assert.Equal(NotificationIsland.CapsuleHeight, island.Surface.Height);
            Assert.Equal(NotificationIsland.CapsuleRadius, island.Surface.CornerRadius.TopLeft);
            Assert.InRange(island.Surface.Width, NotificationIsland.CapsuleMinWidth, NotificationIsland.CapsuleMaxWidth);

            var content = new NotificationIslandContent(new[] { Activity(1, NotificationVisualStatus.Running) },
                new string('長', 80), Array.Empty<NotificationPromptItem>());
            island.Update(content, State(content), motion: false);
            Assert.Equal(NotificationIsland.CapsuleMaxWidth, island.Surface.Width);
        });
    }

    [Fact]
    public void 內容依目標尺寸排版而不跟著變形中的寬度換行()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            Render(island, Scenario.Running, motion: false);
            var (content, state) = Build(Scenario.Prompt);
            island.Update(content, state, motion: true);
            var prompt = island.ActivePrompt;
            // 彈簧才剛開始，表面還是膠囊寬；提醒已經以 320 的寬度排好。
            Assert.True(island.Springs.Width.IsActive);
            Assert.True(island.Surface.Width < NotificationIsland.PanelWidth);
            Assert.Equal(NotificationIsland.PanelWidth, prompt.Width + prompt.Margin.Left + prompt.Margin.Right);
            Assert.Same(prompt, island.CurrentContent);
            Assert.True(island.Springs.Height.Target > NotificationIsland.CapsuleHeight);
            island.StopMotion();
            Assert.Equal(NotificationIsland.PanelWidth, island.Surface.Width);
            Assert.Equal(NotificationIsland.PanelRadius, island.Surface.CornerRadius.TopLeft);
            Assert.False(island.Springs.Width.IsActive);
        });
    }

    [Fact]
    public void 動畫關閉時直接到位且沒有循環動畫()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            Render(island, Scenario.Running, motion: false);
            Assert.False(island.Springs.Width.IsActive);
            Assert.False(island.IsSpinning);
            Render(island, Scenario.Running, motion: true);
            Assert.True(island.IsSpinning);
            Render(island, Scenario.Expanded, motion: true);
            // 清單抬頭接手唯一那一個進度圈；膠囊的停掉。
            Assert.True(island.IsSpinning);
            island.StopMotion();
            Assert.False(island.IsSpinning);
            Render(island, Scenario.Hidden, motion: false);
            Assert.Equal(Visibility.Collapsed, island.Visibility);
        });
    }

    [Fact]
    public void 出現時從圓點長出換內容時舊的淡出新的延遲淡入()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            Render(island, Scenario.Running, motion: true);
            Assert.Equal(NotificationIsland.DotSize, island.Surface.Width);
            Assert.Equal(NotificationIsland.DotSize, island.Surface.Height);
            var capsule = island.CurrentContent!;
            var (content, state) = Build(Scenario.Prompt);
            island.Update(content, state, motion: true);
            Assert.NotSame(capsule, island.CurrentContent);
            Assert.True(capsule.HasAnimatedProperties);
            Assert.Equal(0, island.CurrentContent!.Opacity);
            island.StopMotion();
            Assert.Equal(Visibility.Collapsed, capsule.Visibility);
            Assert.Equal(1, island.CurrentContent.Opacity);
        });
    }

    [Fact]
    public void 提醒的按鈕次要在左主要在最右且叉號是稍後提醒()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var resolved = new List<(long, string?)>();
            island.PromptResolved += (id, action) => resolved.Add((id, action));
            var content = Content(prompts: new[] { Update(7, 1, 1) });
            island.Update(content, State(content), motion: false);
            var view = island.ActivePrompt;
            Assert.Equal(new[] { "略過此版本", "前往下載" }, view.ActionButtons.Select(x => (string)x.Content));
            Assert.Equal(NotificationCatalog.PromptLater, view.LaterButton.ToolTip);
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(view));

            // Tab 順序跟視覺順序：右上角的叉號在第一列，接著是按鈕列由左到右。
            var order = Focusables(view).ToList();
            Assert.Equal(new[] { view.LaterButton }.Concat(view.ActionButtons), order);

            view.ActionButtons[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            view.LaterButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(new[] { (7L, (string?)NotificationActionIds.UpdateDownload), (7L, (string?)null) }, resolved);
        });
    }

    [Fact]
    public void 訊息最多三行完整內容在工具提示()
    {
        WpfTest.Run(() =>
        {
            var view = new NotificationPromptView();
            var message = string.Concat(Enumerable.Repeat("SQL 只留在這台電腦。", 20));
            view.Update(new NotificationPromptItem(1, "標題", message, NotificationPromptSeverity.Info,
                new[] { new NotificationPromptAction("a", "好", true) }, 1, 1));
            view.Measure(new Size(320, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            var text = view.Children.OfType<TextBlock>().Single(x => x.Text == message);
            Assert.Equal(message, text.ToolTip);
            Assert.True(text.ActualHeight <= 16 * NotificationPromptView.MessageLines + 0.5);
        });
    }

    [Fact]
    public void 有提醒時活動收進卡片底部的附條且疊層露出後兩則()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var peeks = 0;
            island.PeekRequested += (_, _) => peeks++;
            Render(island, Scenario.PromptWithActivity, motion: false);
            var strip = island.ActivePrompt.ActivityStrip;
            Assert.Equal(Visibility.Visible, strip.Visibility);
            Assert.Equal(NotificationCatalog.ViewActivities, strip.Action);
            Assert.Equal(NotificationVisualStatus.Running, strip.Icon.Status);
            Assert.False(string.IsNullOrEmpty(strip.Summary.Text));
            Assert.True(island.StackLayers.All(x => x.Visibility == Visibility.Collapsed));
            // 附條屬於卡片本身：島嶼不再比卡片寬。
            Assert.Equal(NotificationIsland.PanelWidth, island.TargetSize.Width);
            // Tab 順序跟視覺順序：叉號、按鈕列，最後是底部的附條。
            Assert.Same(strip, Focusables(island.ActivePrompt).Last());
            strip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, peeks);

            Render(island, Scenario.PromptStack, motion: false);
            Assert.Equal(NotificationIslandShape.PromptStack, island.Shape);
            Assert.True(island.StackLayers.All(x => x.Visibility == Visibility.Visible));
            Assert.Contains(Descendants(island.ActivePrompt).OfType<TextBlock>(), x => x.Text == "1/3");
            Assert.Equal(Visibility.Visible, island.ActivePrompt.ActivityStrip.Visibility);

            Render(island, Scenario.Prompt, motion: false);
            Assert.Equal(Visibility.Collapsed, island.ActivePrompt.ActivityStrip.Visibility);
        });
    }

    [Fact]
    public void 暫看活動時清單底部有回到提醒的附條()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var peeks = 0;
            island.PeekRequested += (_, _) => peeks++;
            var (content, state) = Build(Scenario.PromptWithActivity);
            Assert.True(state.TogglePeek(Now));
            island.Update(content, state, motion: false);
            Assert.Equal(NotificationIslandShape.Expanded, island.Shape);
            Assert.Equal(Visibility.Visible, island.ListFooter.Visibility);
            Assert.Equal(NotificationCatalog.BackToPrompts, island.ListFooter.Action);
            Assert.Equal(NotificationCatalog.PendingPrompts(1), island.ListFooter.Summary.Text);
            island.ListFooter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, peeks);

            // 沒有提醒時的一般展開不帶附條。
            Render(island, Scenario.Expanded, motion: false);
            Assert.Equal(Visibility.Collapsed, island.ListFooter.Visibility);
        });
    }

    [Fact]
    public void 活動結束時附條先淡出收起後卡片才縮回()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var (content, state) = Build(Scenario.PromptWithActivity);
            island.Update(content, state, motion: false);
            var strip = island.ActivePrompt.ActivityStrip;
            var tall = island.Springs.Height.Target;
            // 同一則提醒，活動到期了。
            var bare = content with { Activities = Array.Empty<NotificationActivityItem>(), Summary = "" };
            island.Update(bare, State(bare), motion: true);
            // 淡出途中還佔著位置，外框先不動；淡完才收起並縮回。
            Assert.True(strip.IsLeaving);
            Assert.Equal(tall, island.Springs.Height.Target);
            island.StopMotion();
            Assert.Equal(Visibility.Collapsed, strip.Visibility);
            Assert.False(strip.IsLeaving);
        });
    }

    /// <summary>
    /// 抬頭、各列、提醒卡與附條共用同一條圖示中線與文字起點，右側按鈕外框收在同一條線。
    /// </summary>
    /// <remarks>各自決定內距的版本，圖示差 2 DIP、文字差 4 DIP，兩種形態互換時一眼就看得出參差。</remarks>
    [Fact]
    public void 所有內容對齊同一條圖示中線與文字起點()
    {
        WpfTest.Run(() =>
        {
            var width = NotificationIsland.PanelWidth;
            var island = new NotificationIsland();
            var (content, state) = Build(Scenario.PromptWithActivity);
            state.TogglePeek(Now);
            island.Update(content, state, motion: false);
            Arrange(island);
            var row = island.Rows.Children.OfType<NotificationRow>().First();
            AssertCenter(island, island.ListIcon, NotificationLayout.IconCenter);
            AssertCenter(island, row.Icon, NotificationLayout.IconCenter);
            AssertCenter(island, island.ListFooter.Icon, NotificationLayout.IconCenter);
            AssertLeft(island, island.ListSummary, NotificationLayout.TextStart);
            AssertLeft(island, row.TitleText, NotificationLayout.TextStart);
            AssertLeft(island, island.ListFooter.Summary, NotificationLayout.TextStart);
            AssertRight(island, island.DismissButton, width - NotificationLayout.Right);

            var prompt = new NotificationIsland();
            Render(prompt, Scenario.PromptWithActivity, motion: false);
            Arrange(prompt);
            var view = prompt.ActivePrompt;
            AssertCenter(prompt, view.Children.OfType<SqlIconImage>().Single(), NotificationLayout.IconCenter);
            AssertCenter(prompt, view.ActivityStrip.Icon, NotificationLayout.IconCenter);
            AssertLeft(prompt, view.ActivityStrip.Summary, NotificationLayout.TextStart);
            AssertLeft(prompt, view.Children.OfType<TextBlock>().First(x => x.Text == view.Item!.Title), NotificationLayout.TextStart);
            AssertRight(prompt, view.LaterButton, width - NotificationLayout.Right);
            // 叉號在抬頭列垂直置中，與清單的叉號同一個高度。
            Assert.Equal(Bounds(island, island.DismissButton).Top, Bounds(prompt, view.LaterButton).Top, 1);
        });
    }

    [Fact]
    public void 高對比退回實色並拿掉柔影與快取()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            Assert.NotNull(island.Surface.Effect);
            Assert.IsType<BitmapCache>(island.Surface.CacheMode);
            island.SetOptions(glass: true, highContrast: true);
            Assert.Null(island.Surface.Effect);
            Assert.Null(island.Surface.CacheMode);
        });
    }

    [Fact]
    public void 展開形態沿用列的徽章與覆蓋式捲軸()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            Render(island, Scenario.Expanded, motion: false);
            Assert.Equal(NotificationIslandShape.Expanded, island.Shape);
            Assert.Equal(NotificationIsland.DetailMaxHeight, island.Details.MaxHeight);
            Assert.Equal(ScrollBarVisibility.Disabled, island.Details.HorizontalScrollBarVisibility);
            var rows = ((StackPanel)island.Details.Content).Children.OfType<NotificationRow>().ToArray();
            Assert.Equal(4, rows.Length);
            Assert.Equal(NotificationCatalog.DismissActivities, island.DismissButton.ToolTip);
        });
    }

    [Fact]
    public void 通知島每一種形態多主題多DPI渲染()
    {
        WpfTest.Run(() =>
        {
            var probe = new DrawingVisual();
            using (var drawing = probe.RenderOpen()) drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1, 1));
            var sample = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
            sample.Render(probe);
            var pixel = new byte[4];
            sample.CopyPixels(pixel, 4, 0);
            Assert.SkipWhen(pixel[3] == 0, "目前工作階段無法渲染 WPF 像素；通知島仍需 SSMS 多 DPI 視覺驗收。");
            var directory = Path.Combine(ThemeVisualTests.FindOutputDirectory() ?? AppContext.BaseDirectory, "notification-qa");
            Directory.CreateDirectory(directory);
            var resources = new ThemeResourceSet();
            foreach (var scenario in Enum.GetValues(typeof(Scenario)).Cast<Scenario>().Where(x => x != Scenario.Hidden))
            {
                var island = new NotificationIsland();
                var frame = new Border { Padding = new Thickness(24), Width = 440, Height = 400, Child = island }
                    .WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground);
                frame.Resources.MergedDictionaries.Add(resources.Resources);
                foreach (var theme in new[] { "light", "dark", "high-contrast" })
                {
                    resources.Update(ThemePaletteTests.ColorsFor(theme));
                    island.SetOptions(glass: true, highContrast: theme == "high-contrast");
                    Render(island, scenario, motion: false);
                    frame.Measure(new Size(440, 400));
                    frame.Arrange(new Rect(0, 0, 440, 400));
                    frame.UpdateLayout();
                    Assert.True(island.ActualWidth > 0);
                    foreach (var dpi in new[] { 96, 144, 192 })
                    {
                        var bitmap = new RenderTargetBitmap(440 * dpi / 96, 400 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                        bitmap.Render(frame);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var name = $"island-{scenario.ToString().ToLowerInvariant()}-{theme}-{dpi}.png";
                        using var file = File.Create(Path.Combine(directory, name));
                        encoder.Save(file);
                    }
                }
            }
        });
    }

    /// <summary>QA 與測試共用的幾個場景；每一個對到一種形態。</summary>
    internal enum Scenario { Hidden, Running, Done, Failed, Expanded, Prompt, PromptWithActivity, PromptStack }

    private static void Render(NotificationIsland island, Scenario scenario, bool motion)
    {
        var (content, state) = Build(scenario);
        island.Update(content, state, motion);
    }

    private static (NotificationIslandContent, NotificationIslandState) Build(Scenario scenario)
    {
        var center = new NotificationCenter(() => Now);
        var settings = new SqlAssistSettings();
        var active = new List<NotificationScope>();
        void Activities(bool running, bool failed)
        {
            using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.User,
                       NotificationLevel.Info, "dbo.Lib_Reader", "Lib_Reader.sql", "LibArchive")) { }
            for (var i = 0; i < 2; i++)
                using (center.Begin(NotificationCatalog.LoadingIndexes, NotificationKind.Metadata, NotificationOrigin.User,
                           NotificationLevel.Info, "dbo.Loan", "Lib_Reader.sql", "LibArchive")) { }
            if (failed)
                using (var scope = center.Begin(NotificationCatalog.GeneratingObjectScript, NotificationKind.Preview,
                           NotificationOrigin.User, NotificationLevel.Info, "dbo.Cat_BookCopy", "Lib_Reader.sql")) scope.Fail();
            if (!running) return;
            var scopeRunning = center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Metadata,
                NotificationOrigin.User, NotificationLevel.Info, document: "Lib_Reader.sql");
            scopeRunning.Report("正在建立區塊結構");
            active.Add(scopeRunning);
        }

        void Prompts(int count)
        {
            if (count >= 1) center.Prompt(NotificationCatalog.UpdateAvailablePrompt("1.4.0", "https://example.invalid"),
                NotificationKind.Update, NotificationOrigin.Ambient, NotificationLevel.Notice);
            if (count >= 2) center.Prompt(NotificationCatalog.SqlMemoryFirstCapturePrompt(), NotificationKind.SqlMemory,
                NotificationOrigin.Ambient, NotificationLevel.Notice);
            if (count >= 3) center.Prompt(NotificationCatalog.SqlMemoryCapacityPrompt(""), NotificationKind.SqlMemory,
                NotificationOrigin.Ambient, NotificationLevel.Notice);
        }

        switch (scenario)
        {
            case Scenario.Running: Activities(running: true, failed: false); break;
            case Scenario.Done: Activities(running: false, failed: false); break;
            case Scenario.Failed: Activities(running: false, failed: true); break;
            case Scenario.Expanded: Activities(running: true, failed: true); break;
            case Scenario.Prompt: Prompts(1); break;
            case Scenario.PromptWithActivity: Activities(running: true, failed: false); Prompts(1); break;
            case Scenario.PromptStack: Activities(running: true, failed: false); Prompts(3); break;
        }

        var content = NotificationPresenter.ProjectIsland(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue), settings);
        var state = State(content);
        if (scenario == Scenario.Expanded) state.FocusChanged(true, Now);
        foreach (var scope in active) scope.Dispose();
        return (content, state);
    }

    private static NotificationIslandState State(NotificationIslandContent content)
    {
        var state = new NotificationIslandState();
        state.Update(new NotificationIslandInput(content.Activities.Count, content.Running, content.Failed, content.Prompts.Count), Now);
        return state;
    }

    private static NotificationIslandContent Content(IReadOnlyList<NotificationActivityItem>? activities = null,
        IReadOnlyList<NotificationPromptItem>? prompts = null) =>
        new(activities ?? Array.Empty<NotificationActivityItem>(), "", prompts ?? Array.Empty<NotificationPromptItem>());

    private static NotificationActivityItem Activity(long id, NotificationVisualStatus status) =>
        new(id, "載入欄位與定義", "dbo.Loan", "Loan.sql", "", "", status, "執行中", 1);

    private static NotificationPromptItem Update(long id, int position, int count) =>
        new(id, "SqlAssist 有新版", "1.4.0 已經發行；安裝前要先關掉所有 SSMS。", NotificationPromptSeverity.Info,
            new[]
            {
                new NotificationPromptAction(NotificationActionIds.UpdateDownload, "前往下載", true),
                new NotificationPromptAction(NotificationActionIds.UpdateSkip, "略過此版本", false),
            }, position, count);

    private static void Arrange(NotificationIsland island)
    {
        island.Measure(new Size(NotificationIsland.MaxExtent.Width, NotificationIsland.MaxExtent.Height));
        island.Arrange(new Rect(island.DesiredSize));
        island.UpdateLayout();
    }

    /// <summary>元素在表面座標裡的外框；表面寬度就是島嶼寬度，左緣是 0。</summary>
    private static Rect Bounds(NotificationIsland island, FrameworkElement element) =>
        element.TransformToVisual(island.Surface).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void AssertCenter(NotificationIsland island, FrameworkElement element, double expected)
    {
        var bounds = Bounds(island, element);
        Assert.Equal(expected, bounds.Left + bounds.Width / 2, 1);
    }

    private static void AssertLeft(NotificationIsland island, FrameworkElement element, double expected) =>
        Assert.Equal(expected, Bounds(island, element).Left, 1);

    private static void AssertRight(NotificationIsland island, FrameworkElement element, double expected) =>
        Assert.Equal(expected, Bounds(island, element).Right, 1);

    private static IEnumerable<Control> Focusables(DependencyObject root) =>
        Descendants(root).OfType<Control>().Where(x => x.Focusable && x.IsTabStop && x.Visibility == Visibility.Visible);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
