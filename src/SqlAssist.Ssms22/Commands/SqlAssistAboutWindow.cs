using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.Diagnostics;
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
    private readonly TextBlock _statusText;

    public SqlAssistAboutWindow(
        SqlAssistDiagnosticSnapshot snapshot,
        Func<bool> openSettings,
        Action openLog)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _openLog = openLog ?? throw new ArgumentNullException(nameof(openLog));

        // 健康檢查在整個視窗裡只評估這一次：抬頭徽章、概覽的結論與「診斷」分頁
        // 讀的都是同一份，否則三處各評估一次還可能各說各話。
        _health = SqlAssistDiagnosticReport.EvaluateHealth(snapshot);
        _summary = SqlAssistDiagnosticReport.Summarize(snapshot, _health);

        Title = "SqlAssist — 關於與診斷";
        Width = 820;
        Height = 680;
        MinWidth = 680;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = VsThemeBrushes.WindowBackground;
        Foreground = VsThemeBrushes.WindowForeground;
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = Metrics.Body;

        var logo = TryLoadLogo();
        Icon = logo;
        _statusText = SqlAssistChrome.CreateStatusText(Metrics);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Content = BuildLayout(logo);
    }

    private Grid BuildLayout(ImageSource? logo)
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader(logo);
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

    private Border BuildHeader(ImageSource? logo)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement mark;

        if (logo is not null)
        {
            mark = new Image
            {
                Source = logo,
                Width = 72,
                Height = 72,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
        }
        else
        {
            mark = new Border
            {
                Width = 72,
                Height = 72,
                Background = VsThemeBrushes.AccentBackground,
                BorderBrush = VsThemeBrushes.AccentBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Child = new TextBlock
                {
                    Text = "SA",
                    FontFamily = SqlAssistChrome.InterfaceFont,
                    FontSize = 24,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = VsThemeBrushes.ListForeground,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

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
            FontSize = Metrics.Title + 4,
            FontWeight = FontWeights.SemiBold,
            Foreground = VsThemeBrushes.ListForeground
        });
        copy.Children.Add(new TextBlock
        {
            Text = _snapshot.Description,
            FontSize = Metrics.Caption,
            Foreground = VsThemeBrushes.DimForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 8)
        });

        var badges = new StackPanel { Orientation = Orientation.Horizontal };
        badges.Children.Add(SqlAssistChrome.CreateBadge(
            $"版本 {_snapshot.BuildVersion.DisplayVersion}",
            Metrics,
            accent: true));

        // 抬頭的徽章三個分頁都看得到，所以放最短的那一句；完整結論在「概覽」上方。
        var statusBadge = SqlAssistChrome.CreateBadge(
            $"{Glyph(_summary.Level)} {_summary.ShortStatus}",
            Metrics);
        statusBadge.Margin = new Thickness(8, 0, 0, 0);
        badges.Children.Add(statusBadge);
        copy.Children.Add(badges);

        Grid.SetColumn(copy, 1);
        layout.Children.Add(copy);

        var surface = SqlAssistChrome.CreateSurface(layout);
        surface.Padding = new Thickness(18);
        return surface;
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl
        {
            Background = VsThemeBrushes.WindowBackground,
            BorderThickness = default,
            Padding = default,
            FontFamily = SqlAssistChrome.InterfaceFont,
            Template = SqlAssistChrome.CreateTabControlTemplate()
        };

        tabs.Items.Add(CreateTab("概覽", BuildOverview()));
        tabs.Items.Add(CreateTab("設定摘要", BuildSettings()));
        tabs.Items.Add(CreateTab("診斷", BuildDiagnostics()));
        return tabs;
    }

    private UIElement BuildOverview()
    {
        var content = CreateTabPanel();
        content.Children.Add(CreateHealthSummary());
        content.Children.Add(CreateSection(
            "關於 SqlAssist",
            null,
            CreateInfoRow("版本", _snapshot.BuildVersion.DisplayVersion),
            CreateInfoRow(
                "Build",
                $"{_snapshot.BuildVersion.FullVersion} · commit {_snapshot.BuildVersion.ShortCommitId}"),
            CreateInfoRow("相容環境", "SSMS 22.x · Windows x64"),
            CreateInfoRow("作者", _snapshot.Author),
            CreateInfoRow("聯絡方式", _snapshot.ContactEmail),
            CreateInfoRow("授權", $"{_snapshot.License} License · © 2026 {_snapshot.Author}")));

        var projectActions = new StackPanel { Orientation = Orientation.Horizontal };
        projectActions.Children.Add(CreateButton(
            "GitHub 專案",
            (_, _) => OpenExternal(_snapshot.RepositoryUrl, "開啟 GitHub 專案")));
        projectActions.Children.Add(CreateButton(
            "回報問題",
            (_, _) => OpenExternal(_snapshot.IssuesUrl, "開啟問題回報頁")));

        content.Children.Add(CreateSection(
            "專案與支援",
            "這是公開原始碼專案。回報問題前可先按下方的「複製診斷資訊」，再貼到 GitHub Issue。",
            projectActions));

        content.Children.Add(CreateSection(
            "隱私與資料",
            "建議與中繼資料處理都在本機完成，只查詢目前已連線的 SQL Server；" +
            "不會把 SQL 傳到雲端，也沒有 AI 模型參與。複製的診斷摘要不含 SQL、" +
            "伺服器名稱、資料庫名稱或 Windows 使用者名稱。"));
        return CreateScrollViewer(content);
    }

    private UIElement BuildSettings()
    {
        var content = CreateTabPanel();
        content.Children.Add(SqlAssistChrome.CreateHint(
            "以下是目前真正生效的值；這裡只做摘要，修改請使用「開啟設定」。",
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
            "健康檢查",
            "這裡顯示『設定想要的狀態』與『SSMS 實際狀態』是否一致。",
            _health.Select(CreateHealthRow).ToArray()));

        // 套件與設定服務的狀態不在這裡重複：上面的健康檢查已經各有一列，
        // 兩份文案分頭改動的結果會是同一頁裡自相矛盾。
        content.Children.Add(CreateSection(SqlAssistDiagnosticSections.DescribeRuntime(_snapshot)));
        content.Children.Add(CreateSection(
            "環境",
            null,
            CreateInfoRows(SqlAssistDiagnosticSections.DescribeVersion(_snapshot))
                .Concat(CreateInfoRows(SqlAssistDiagnosticSections.DescribeEnvironment(_snapshot)))
                .ToArray()));

        var logState = _snapshot.LogExists
            ? $"存在 · {SqlAssistDiagnosticReport.FormatBytes(_snapshot.LogSizeBytes)}"
            : "尚未建立";
        var logUpdated = _snapshot.LogLastUpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

        content.Children.Add(CreateSection(
            "診斷紀錄",
            _snapshot.Settings.VerboseLogging
                ? "詳細紀錄目前已開啟；問題重現完成後，建議關閉以免持續增加檔案。"
                : "平常保持停用即可；只有重現難查問題時才需要開啟詳細紀錄。",
            CreateInfoRow(
                "詳細紀錄",
                SqlAssistDiagnosticReport.FormatState(_snapshot.Settings.VerboseLogging)),
            CreateInfoRow("檔案", logState),
            CreateInfoRow("最後更新", logUpdated),
            CreateInfoRow("路徑", _snapshot.LogPath, useCodeFont: true)));
        return CreateScrollViewer(content);
    }

    /// <remarks>
    /// 這裡刻意不再放一次狀態徽章：抬頭已經有一個，而且三個分頁都看得到。
    /// </remarks>
    private Border CreateHealthSummary()
    {
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = _summary.Headline,
            FontSize = Metrics.Title + 1,
            FontWeight = FontWeights.SemiBold,
            Foreground = VsThemeBrushes.ListForeground,
            TextWrapping = TextWrapping.Wrap
        });
        copy.Children.Add(new TextBlock
        {
            Text = _summary.Detail,
            FontSize = Metrics.Caption,
            Foreground = VsThemeBrushes.DimForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var surface = SqlAssistChrome.CreateSurface(copy);
        surface.Padding = new Thickness(16);
        surface.Margin = new Thickness(0, 0, 0, 12);
        return surface;
    }

    private DockPanel BuildFooter()
    {
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(CreateButton("複製診斷資訊", OnCopyDiagnostics));
        actions.Children.Add(CreateButton("開啟紀錄檔", OnOpenLog));
        actions.Children.Add(CreateButton("開啟設定", OnOpenSettings));

        var close = CreateButton("關閉", (_, _) => Close(), primary: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Margin = default;

        DockPanel.SetDock(actions, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(actions);
        footer.Children.Add(close);

        _statusText.Margin = new Thickness(12, 0, 12, 0);
        footer.Children.Add(_statusText);
        return footer;
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Clipboard.SetText(SqlAssistDiagnosticReport.Create(_snapshot));
            _statusText.Text = "已複製隱私安全的診斷摘要。";
        }
        catch (Exception exception)
        {
            ReportActionFailure("複製診斷資訊", exception);
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
            ReportActionFailure("開啟診斷紀錄檔", exception);
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

            _statusText.Text = "無法開啟設定，請改用 Ctrl+, 並搜尋 SqlAssist。";
        }
        catch (Exception exception)
        {
            ReportActionFailure("開啟設定", exception);
        }
    }

    private void OpenExternal(string target, string operation)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            _statusText.Text = $"已交給預設瀏覽器：{operation}";
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
        _statusText.Text = $"{operation}失敗：{exception.Message}";
    }

    private static TabItem CreateTab(string header, UIElement content)
    {
        return new TabItem
        {
            Header = header,
            Content = content,
            Template = SqlAssistChrome.CreateTabItemTemplate()
        };
    }

    private static StackPanel CreateTabPanel()
    {
        return new StackPanel { Margin = new Thickness(14, 2, 14, 0) };
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
            Foreground = VsThemeBrushes.ListForeground,
            Margin = new Thickness(0, 0, 0, string.IsNullOrWhiteSpace(description) ? 8 : 2)
        });

        if (!string.IsNullOrWhiteSpace(description))
        {
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Metrics.Caption,
                Foreground = VsThemeBrushes.DimForeground,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, children.Length == 0 ? 0 : 9)
            });
        }

        foreach (var child in children)
        {
            content.Children.Add(child);
        }

        var surface = SqlAssistChrome.CreateSurface(content);
        surface.Padding = new Thickness(16, 13, 16, 14);
        surface.Margin = new Thickness(0, 0, 0, 12);
        return surface;
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
            Foreground = VsThemeBrushes.DimForeground,
            VerticalAlignment = VerticalAlignment.Top
        });

        var valueText = new TextBlock
        {
            Text = value,
            FontFamily = useCodeFont ? SqlAssistChrome.CodeFont : SqlAssistChrome.InterfaceFont,
            FontSize = useCodeFont ? Metrics.Caption : Metrics.Body,
            Foreground = VsThemeBrushes.ListForeground,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
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
            FontWeight = FontWeights.SemiBold,
            Foreground = VsThemeBrushes.ListForeground
        });

        var state = new StackPanel();
        state.Children.Add(new TextBlock
        {
            Text = check.Name,
            FontSize = Metrics.Body,
            FontWeight = FontWeights.SemiBold,
            Foreground = VsThemeBrushes.ListForeground
        });
        state.Children.Add(new TextBlock
        {
            Text = check.Status,
            FontSize = Metrics.Caption,
            Foreground = VsThemeBrushes.DimForeground
        });
        Grid.SetColumn(state, 1);
        row.Children.Add(state);

        var detail = new TextBlock
        {
            Text = check.Detail,
            FontSize = Metrics.Caption,
            Foreground = VsThemeBrushes.DimForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 1, 0, 0)
        };
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
                var path = Path.Combine(directory ?? string.Empty, "logo.png");

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
