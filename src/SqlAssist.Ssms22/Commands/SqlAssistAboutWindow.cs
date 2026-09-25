using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Commands;

/// <summary>把產品資訊、目前設定與疑難排解分層呈現，而不是塞進一個訊息框。</summary>
internal sealed class SqlAssistAboutWindow : DialogWindow
{
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    private readonly SqlAssistDiagnosticSnapshot _snapshot;
    private readonly IReadOnlyList<SqlAssistHealthCheck> _health;
    private readonly SqlAssistHealthSummary _summary;
    private readonly Func<bool> _openSettings;
    private readonly Action _openLog;
    private readonly Action _checkForUpdates;
    private readonly TextBlock _statusText;
    private readonly ImageSource? _logoSource;

    public SqlAssistAboutWindow(
        SqlAssistDiagnosticSnapshot snapshot,
        Func<bool> openSettings,
        Action openLog,
        Action checkForUpdates)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _openLog = openLog ?? throw new ArgumentNullException(nameof(openLog));
        _checkForUpdates = checkForUpdates ?? throw new ArgumentNullException(nameof(checkForUpdates));

        // 健康檢查在整個視窗裡只評估這一次：抬頭徽章、概覽的結論與「診斷」分頁
        // 讀的都是同一份，否則三處各評估一次還可能各說各話。
        _health = SqlAssistDiagnosticReport.EvaluateHealth(snapshot);
        _summary = SqlAssistDiagnosticReport.Summarize(snapshot, _health);

        SqlAssistDialogs.Configure(this, AboutText.WindowTitle, 820, 680, minWidth: 680, minHeight: 540);

        // 原生標題列與內容標誌均使用 SqlAssist 產品圖示（高 DPI 下自動平滑渲染）。
        _logoSource = TryLoadLogo();
        Icon = _logoSource;
        _statusText = SqlAssistChrome.CreateStatusText(Metrics);
        Content = BuildLayout();
    }

    private Grid BuildLayout()
    {
        var root = new Grid { Margin = SqlAssistChrome.DialogPadding };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var tabs = BuildTabs();
        tabs.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private Border BuildHeader()
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mark = SqlAssistChrome.CreateBrandMark(_logoSource);
        Grid.SetColumn(mark, 0);
        layout.Children.Add(mark);

        var copy = new StackPanel
        {
            Margin = new Thickness(16, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        copy.Children.Add(new TextBlock
        {
            Text = _snapshot.ProductName,
            FontSize = Metrics.Title,
            FontWeight = FontWeights.SemiBold
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground));
        copy.Children.Add(new TextBlock
        {
            Text = AboutText.ProductDescription,
            FontSize = Metrics.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 8)
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground));

        var badges = new StackPanel { Orientation = Orientation.Horizontal };
        badges.Children.Add(SqlAssistChrome.CreateBadge(
            AboutText.VersionBadge(_snapshot.BuildVersion.DisplayVersion),
            Metrics));

        // 抬頭的徽章三個分頁都看得到，所以放最短的那一句；完整結論在「概覽」上方。
        var statusBadge = SqlAssistChrome.CreateBadge(
            $"{Glyph(_summary.Level)} {_summary.ShortStatus}",
            Metrics);
        statusBadge.Margin = new Thickness(8, 0, 0, 0);
        badges.Children.Add(statusBadge);
        copy.Children.Add(badges);

        Grid.SetColumn(copy, 1);
        layout.Children.Add(copy);

        // 品牌資訊留在原生標題列下的一列，不再額外包成大型展示卡。
        return new Border { Child = layout };
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl
        {
            BorderThickness = default,
            Padding = default,
            FontFamily = SqlAssistChrome.InterfaceFont,
            Template = SqlAssistChrome.CreateTabControlTemplate()
        }.WithTheme(TabControl.BackgroundProperty, ThemeBrush.WindowBackground);

        tabs.Items.Add(CreateTab(AboutText.TabOverview, BuildOverview()));
        tabs.Items.Add(CreateTab(AboutText.TabSettings, BuildSettings()));
        tabs.Items.Add(CreateTab(AboutText.TabDiagnostics, BuildDiagnostics()));
        tabs.Items.Add(CreateTab(AboutText.TabSessionStats, BuildNotificationDigest()));
        tabs.Items.Add(CreateTab(AboutText.TabNotificationFailures, BuildNotificationFailures()));
        return tabs;
    }

    private UIElement BuildOverview()
    {
        var content = CreateTabPanel();
        content.Children.Add(CreateHealthSummary());
        content.Children.Add(CreateSection(
            AboutText.AboutSection,
            null,
            CreateInfoRow(
                "Build",
                $"{_snapshot.BuildVersion.FullVersion} · commit {_snapshot.BuildVersion.ShortCommitId}"),
            CreateInfoRow(AboutText.Compatibility, "SSMS 22.x · Windows x64"),
            CreateInfoRow(AboutText.Author, _snapshot.Author),
            CreateInfoRow(AboutText.Contact, _snapshot.ContactEmail),
            CreateInfoRow(AboutText.License, $"{_snapshot.License} License · © 2026 {_snapshot.Author}")));

        var projectActions = new StackPanel { Orientation = Orientation.Horizontal };
        projectActions.Children.Add(CreateButton(
            AboutText.GitHubProject,
            (_, _) => OpenExternal(_snapshot.RepositoryUrl, AboutText.OpenGitHubProject)));
        projectActions.Children.Add(CreateButton(
            AboutText.ReportIssue,
            (_, _) => OpenExternal(_snapshot.IssuesUrl, AboutText.OpenIssuePage)));
        // 與「工具 → SqlAssist → 檢查更新…」同一份實作；結論是右下角通知島上的提醒。
        projectActions.Children.Add(CreateButton(AboutText.CheckForUpdates, (_, _) => CheckForUpdates()));

        content.Children.Add(CreateSection(
            AboutText.ProjectSection,
            AboutText.ProjectHint,
            projectActions));

        content.Children.Add(CreateSection(
            AboutText.PrivacySection,
            AboutText.PrivacyHint));
        return CreateScrollViewer(content);
    }

    private UIElement BuildSettings()
    {
        var content = CreateTabPanel();
        content.Children.Add(SqlAssistChrome.CreateHint(
            AboutText.SettingsHint,
            Metrics));

        foreach (var section in SqlAssistDiagnosticSections.DescribeSettings(_snapshot))
        {
            content.Children.Add(CreateSection(section));
        }

        return CreateScrollViewer(content);
    }

    private UIElement BuildDiagnostics()
    {
        var content = CreateTabPanel();
        content.Children.Add(CreateSection(
            AboutText.HealthSection,
            AboutText.HealthHint,
            _health.Select(CreateHealthRow).ToArray()));

        // 套件與設定服務的狀態不在這裡重複：上面的健康檢查已經各有一列，
        // 兩份文案分頭改動的結果會是同一頁裡自相矛盾。
        content.Children.Add(CreateSection(SqlAssistDiagnosticSections.DescribeRuntime(_snapshot)));
        content.Children.Add(CreateSection(
            AboutText.EnvironmentSection,
            null,
            CreateInfoRows(SqlAssistDiagnosticSections.DescribeVersion(_snapshot))
                .Concat(CreateInfoRows(SqlAssistDiagnosticSections.DescribeEnvironment(_snapshot)))
                .ToArray()));

        var logState = _snapshot.LogExists
            ? AboutText.LogExists(SqlAssistDiagnosticReport.FormatBytes(_snapshot.LogSizeBytes))
            : AboutText.LogNotCreated;
        var logUpdated = _snapshot.LogLastUpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

        content.Children.Add(CreateSection(
            AboutText.LogSection,
            _snapshot.Settings.VerboseLogging
                ? AboutText.VerboseLoggingOnHint
                : AboutText.VerboseLoggingOffHint,
            CreateInfoRow(
                AboutText.VerboseLogging,
                _snapshot.Settings.VerboseLogging ? AboutText.StateOn : AboutText.StateOff),
            CreateInfoRow(AboutText.LogFile, logState),
            CreateInfoRow(AboutText.LogUpdated, logUpdated),
            CreateInfoRow(AboutText.LogPath, _snapshot.LogPath, useCodeFont: true)));
        return CreateScrollViewer(content);
    }

    /// <summary>回答「哪些動作在重複」：統計不受通知可見度影響，隱藏的一樣計入。</summary>
    private UIElement BuildNotificationDigest()
    {
        var content = CreateTabPanel();
        content.Children.Add(SqlAssistChrome.CreateHint(
            AboutText.SessionStatsHint, Metrics));

        var order = SqlAssistChrome.CreateComboBox(Metrics);
        order.Items.Add(AboutText.OrderByCount);
        order.Items.Add(AboutText.OrderByElapsed);
        order.SelectedIndex = 0;
        order.Width = 132;
        AutomationProperties.SetName(order, AboutText.OrderAutomationName);

        var chooser = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        var label = SqlAssistChrome.CreateLabel(AboutText.OrderLabel, Metrics);
        label.Margin = new Thickness(0, 0, 8, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        chooser.Children.Add(label);
        chooser.Children.Add(order);
        content.Children.Add(chooser);

        var rows = new StackPanel();
        content.Children.Add(rows);
        // 切換排序順便重讀統計；這一頁沒有自己的計時器，數字停在最後一次互動。
        order.SelectionChanged += (_, _) => FillNotificationDigest(rows, order.SelectedIndex);
        FillNotificationDigest(rows, order.SelectedIndex);
        return CreateScrollViewer(content);
    }

    private void FillNotificationDigest(Panel rows, int selectedOrder)
    {
        try
        {
            rows.Children.Clear();
            var entries = NotificationCenter.Default.Digest.Snapshot(selectedOrder == 1
                ? NotificationDigestOrder.TotalElapsed
                : NotificationDigestOrder.Count);
            if (entries.Count == 0)
            {
                rows.Children.Add(SqlAssistChrome.CreateHint(AboutText.NoSessionStats, Metrics));
                return;
            }

            foreach (var entry in entries)
            {
                rows.Children.Add(CreateInfoRow(
                    entry.IsOther ? CommonText.Other : NotificationKindToggle.Label(entry.Kind),
                    DescribeDigestEntry(entry)));
            }
        }
        catch (Exception exception)
        {
            // 使用者剛切換的排序不能沒有反應；這裡不走安靜略過的平台探測。
            ReportActionFailure(AboutText.SortSessionStats, exception);
        }
    }

    private static string DescribeDigestEntry(NotificationDigestEntry entry)
    {
        var headline = entry.Subject.Length == 0 ? entry.Title : entry.Title + " · " + entry.Subject;
        var stats = AboutText.DigestStats(entry.Count, FormatMilliseconds(entry.Total),
            FormatMilliseconds(entry.Average), FormatMilliseconds(entry.Max));
        if (entry.Failed > 0) stats += " · " + AboutText.DigestFailed(entry.Failed);
        if (entry.Degraded > 0) stats += " · " + AboutText.DigestDegraded(entry.Degraded);
        return headline + Environment.NewLine + stats;
    }

    /// <summary>統一用毫秒，讓不同量級的兩列仍然可以直接比大小。</summary>
    private static string FormatMilliseconds(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds.ToString("N0", CultureInfo.CurrentCulture) + " ms";

    private UIElement BuildNotificationFailures()
    {
        var content = CreateTabPanel();
        content.Children.Add(BuildNotificationRehearsal());
        content.Children.Add(SqlAssistChrome.CreateHint(
            AboutText.FailuresHint, Metrics));
        var failures = NotificationCenter.Default.RecentFailures;
        if (failures.Count == 0)
            content.Children.Add(SqlAssistChrome.CreateHint(AboutText.NoFailures, Metrics));
        foreach (var item in failures.Reverse())
            content.Children.Add(CreateInfoRow(item.Finished?.ToLocalTime().ToString("HH:mm:ss") ?? "",
                $"{item.Title} · {NotificationCatalog.Provenance(item)}\n{NotificationKindToggle.Label(item.Kind)} / {item.Severity} · #{item.Id} · {(item.Finished - item.Started)?.TotalMilliseconds:0} ms"));
        return CreateScrollViewer(content);
    }

    /// <summary>「通知看不看得到、長什麼樣子」的自助測試；情境與措辭都在 <see cref="NotificationRehearsal"/>。</summary>
    private Border BuildNotificationRehearsal()
    {
        var buttons = new WrapPanel();
        foreach (var scenario in NotificationRehearsal.All)
        {
            var button = CreateButton(NotificationRehearsal.Label(scenario), (_, _) => Rehearse(scenario));
            button.Margin = new Thickness(0, 0, 6, 6);
            buttons.Children.Add(button);
        }

        return CreateSection(
            AboutText.RehearsalSection,
            AboutText.RehearsalHint,
            buttons);
    }

    private void Rehearse(NotificationRehearsalScenario scenario)
    {
        try
        {
            var running = NotificationRehearsal.RunAsync(scenario, NotificationCenter.Default, Task.Delay);
            // 活動的情境要幾秒後才完成，沒有人等它；之後才出的錯交給 Guard 記下。
            SqlAssistPlatformGuard.BeginProbe("通知測試", running);
            _statusText.Text = NotificationRehearsal.HiddenReason(scenario, SqlAssistSettingsStore.Current)
                ?? AboutText.RehearsalSent(NotificationRehearsal.Label(scenario));
        }
        catch (Exception exception)
        {
            ReportActionFailure(AboutText.RehearsalOperation, exception);
        }
    }

    /// <remarks>不重複抬頭已顯示的狀態徽章。</remarks>
    private Border CreateHealthSummary()
    {
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = _summary.Headline,
            FontSize = Metrics.Title + 1,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground));
        copy.Children.Add(new TextBlock
        {
            Text = _summary.Detail,
            FontSize = Metrics.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground));

        return new Border
        {
            Child = copy,
            Margin = new Thickness(0, 0, 0, 24)
        };
    }

    private DockPanel BuildFooter()
    {
        var utilities = new[]
        {
            CreateButton(AboutText.CopyDiagnostics, OnCopyDiagnostics),
            CreateButton(AboutText.OpenLog, OnOpenLog),
            CreateButton(AboutText.OpenSettings, OnOpenSettings)
        };
        var close = CreateButton(CommonText.Close, (_, _) => Close(), primary: true);
        close.IsDefault = true;
        close.IsCancel = true;
        return SqlAssistChrome.CreateDialogFooter(utilities, _statusText, close);
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs eventArgs) => _ = CopyDiagnosticsAsync();

    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var failure = await SqlClipboard.WriteTextAsync(SqlAssistDiagnosticReport.Create(_snapshot)).ConfigureAwait(true);
            _statusText.Text = failure ?? AboutText.DiagnosticsCopied;
        }
        catch (Exception exception)
        {
            ReportActionFailure(AboutText.CopyDiagnostics, exception);
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            _openLog();
            Close();
        }
        catch (Exception exception)
        {
            ReportActionFailure(AboutText.OpenDiagnosticsLog, exception);
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            if (_openSettings())
            {
                Close();
                return;
            }

            _statusText.Text = AboutText.SettingsUnavailable;
        }
        catch (Exception exception)
        {
            ReportActionFailure(AboutText.OpenSettings, exception);
        }
    }

    private void CheckForUpdates()
    {
        try
        {
            _checkForUpdates();
            _statusText.Text = AboutText.CheckingForUpdates;
        }
        catch (Exception exception)
        {
            ReportActionFailure(AboutText.CheckForUpdates, exception);
        }
    }

    private void OpenExternal(string target, string operation)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            _statusText.Text = AboutText.OpenedInBrowser(operation);
        }
        catch (Exception exception)
        {
            ReportActionFailure(operation, exception);
        }
    }

    private void ReportActionFailure(string operation, Exception exception)
    {
        // 這些都是使用者主動按下的動作；失敗時不能像平台探測一樣安靜略過。
        SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
        _statusText.Text = AboutText.ActionFailed(operation, exception.Message);
    }

    private static TabItem CreateTab(string header, UIElement content) =>
        SqlAssistChrome.CreateTab(new SqlTabHeader(header), SqlAssistChrome.CreateSurface(content));

    private static StackPanel CreateTabPanel()
    {
        return new StackPanel { Margin = new Thickness(16, 12, 16, 0) };
    }

    private static ScrollViewer CreateScrollViewer(UIElement content)
    {
        return new ScrollViewer
        {
            Content = content,
            Padding = new Thickness(0, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    /// <summary>把 <see cref="SqlAssistDiagnosticSections"/> 的一組列畫成一個區塊。</summary>
    private static Border CreateSection(SqlAssistDiagnosticSection section)
    {
        return CreateSection(section.Title, null, CreateInfoRows(section).ToArray());
    }

    private static IEnumerable<UIElement> CreateInfoRows(SqlAssistDiagnosticSection section)
    {
        return section.Rows.Select(row => (UIElement)CreateInfoRow(row.Label, row.Value));
    }

    private static Border CreateSection(
        string title,
        string? description,
        params UIElement[] children)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Metrics.Title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, string.IsNullOrWhiteSpace(description) ? 8 : 2)
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground));

        if (!string.IsNullOrWhiteSpace(description))
        {
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Metrics.Caption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, children.Length == 0 ? 0 : 9)
            }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground));
        }

        foreach (var child in children)
        {
            content.Children.Add(child);
        }

        // 區塊靠字重與留白分層；整頁不再重複套外框。
        return new Border
        {
            Child = content,
            Margin = new Thickness(0, 0, 0, 24)
        };
    }

    private static Grid CreateInfoRow(string label, string value, bool useCodeFont = false)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = Metrics.Caption,
            VerticalAlignment = VerticalAlignment.Top
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground));

        var valueText = new TextBlock
        {
            Text = value,
            FontFamily = useCodeFont ? SqlAssistChrome.CodeFont : SqlAssistChrome.InterfaceFont,
            FontSize = useCodeFont ? Metrics.Caption : Metrics.Body,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        return row;
    }

    private static Grid CreateHealthRow(SqlAssistHealthCheck check)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = Glyph(check.Level),
            FontWeight = FontWeights.SemiBold
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground));

        var state = new StackPanel();
        state.Children.Add(new TextBlock
        {
            Text = check.Name,
            FontSize = Metrics.Body,
            FontWeight = FontWeights.SemiBold
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground));
        state.Children.Add(new TextBlock
        {
            Text = check.Status,
            FontSize = Metrics.Caption
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground));
        Grid.SetColumn(state, 1);
        row.Children.Add(state);

        var detail = new TextBlock
        {
            Text = check.Detail,
            FontSize = Metrics.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 1, 0, 0)
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        Grid.SetColumn(detail, 2);
        row.Children.Add(detail);
        return row;
    }

    /// <summary>狀態層級的符號；佈景可能是高對比，所以永遠有文字並排，不只靠顏色。</summary>
    private static string Glyph(SqlAssistHealthLevel level)
    {
        return level switch
        {
            SqlAssistHealthLevel.Ready => "✓",
            SqlAssistHealthLevel.Warning => "!",
            _ => "i"
        };
    }

    private static Button CreateButton(
        string text,
        RoutedEventHandler handler,
        bool primary = false)
    {
        var button = SqlAssistChrome.CreateButton(text, Metrics, primary);
        button.Margin = new Thickness(0, 0, 6, 0);
        button.Click += handler;
        return button;
    }

    private static ImageSource? TryLoadLogo()
    {
        return SqlAssistPlatformGuard.Probe<ImageSource?>(
            "載入關於視窗圖示",
            () =>
            {
                var directory = Path.GetDirectoryName(typeof(SqlAssistAboutWindow).Assembly.Location);
                var path = Path.Combine(directory ?? string.Empty, "SqlAssist.Icon.512.png");

                if (!File.Exists(path))
                {
                    return null;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            },
            fallback: null);
    }
}
