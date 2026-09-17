using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

internal enum SqlMemoryUsageAction { Maintain, Cleanup, Compact, Backup, OpenFolder, Settings }

/// <summary>
/// SQL Memory 的用量頁：容量量表、健康狀態、配額、各類筆數、伺服器分布、整理動作與最近的整理紀錄。
/// </summary>
/// <remarks>
/// 取代清單所在的主從區，不另開視窗：看用量與清理是同一件事的前後兩步，彈出視窗會擋住回清單確認結果的路。
/// 所有數字與文案來自 Core 的 <see cref="SqlMemoryUsageSummary"/>；這裡只排版、繫結主題與播放狀態動畫。
/// 量表控制項在重新整理之間沿用，長度才能從舊值滑到新值，看得出清理的效果。
/// </remarks>
internal sealed class SqlMemoryUsageView : DockPanel
{
    private const double CompactWidth = 440;

    private static readonly (SqlMemoryUsageAction Action, SqlIcon Icon, string Label, string ToolTip, SqlActionTone Tone)[] Actions =
    {
        (SqlMemoryUsageAction.Maintain, SqlIcon.Maintain, "立即維護", "不等排程，依目前的保留規則巡完一輪並截斷 WAL。", SqlActionTone.Neutral),
        (SqlMemoryUsageAction.Cleanup, SqlIcon.Cleanup, "清除紀錄…", "依期間、連線與種類清除 History、回復內容或收藏舊版本；送出前會試算。", SqlActionTone.Danger),
        (SqlMemoryUsageAction.Compact, SqlIcon.Compact, "壓縮資料庫", "重建資料庫檔案，把已刪除資料佔用的空間還給磁碟；不會刪除任何紀錄。", SqlActionTone.Neutral),
        (SqlMemoryUsageAction.Backup, SqlIcon.Backup, "備份…", "把目前的資料庫另存成一個檔案；擷取照常進行。", SqlActionTone.Neutral),
        (SqlMemoryUsageAction.OpenFolder, SqlIcon.Folder, "開啟資料夾", "在檔案總管中顯示 SQL Memory 資料庫。", SqlActionTone.Neutral),
        (SqlMemoryUsageAction.Settings, SqlIcon.Settings, "保留設定", "調整容量上限、保留期限與筆數配額。", SqlActionTone.Neutral),
    };

    private readonly SqlAssistChrome.Metrics _metrics = SqlAssistChrome.DefaultMetrics;
    private readonly Dictionary<SqlMemoryUsageAction, Button> _buttons = new();
    private readonly Button _refresh;
    private readonly Dictionary<string, SqlUsageMeter> _quotaMeters = new(StringComparer.Ordinal);
    private readonly StackPanel _content = new();
    private readonly Border _hero;
    private readonly Ellipse _healthDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _healthTitle;
    private readonly TextBlock _capacityValue;
    private readonly TextBlock _healthDetail;
    private readonly SqlUsageMeter _capacity = new(8);
    private readonly TextBlock _capacityDetail;
    private readonly TextBlock _disk;
    private readonly TextBlock _maintenance;
    private readonly Button _compactHint;
    private readonly StackPanel _quotas = new();
    private readonly UniformGrid _stats = new() { Columns = 2 };
    private readonly StackPanel _servers = new();
    private readonly TextBlock _range;
    private readonly WrapPanel _actions = new();
    private readonly StackPanel _activities = new();
    private readonly Border _busy;
    private readonly TextBlock _busyText;
    private readonly SqlUsageMeter _busyMeter = new(3) { IsIndeterminate = true };
    private readonly TextBlock _message;
    private readonly SqlLoadingSurface _loading;
    private bool _hasSummary;

    public event EventHandler? BackRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<SqlMemoryUsageAction>? ActionRequested;

    public SqlMemoryUsageView()
    {
        AutomationProperties.SetName(this, "SQL Memory 用量");
        LastChildFill = true;

        // 頁首：返回在左、標題緊接，重新整理靠右；和清單工具列同一條中心線與高度。
        var header = new DockPanel { MinHeight = 32, Margin = new Thickness(0, 0, 0, 6) };
        SetDock(header, Dock.Top); Children.Add(header);
        var back = SqlAssistChrome.CreateButton("", _metrics);
        back.Content = BackLabel();
        back.ToolTip = "回到清單（Esc）"; AutomationProperties.SetName(back, "回到清單");
        back.Padding = new Thickness(6, 3, 8, 3); back.Height = 28;
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        SetDock(back, Dock.Left); header.Children.Add(back);
        var refresh = SqlAssistChrome.CreateButton("", _metrics);
        refresh.Content = SqlAssistChrome.CreateMemoryLabel(SqlIcon.Refresh, "重新整理");
        refresh.ToolTip = "重新計算用量"; AutomationProperties.SetName(refresh, "重新計算用量");
        refresh.Padding = new Thickness(6, 3, 6, 3); refresh.Height = 28;
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _refresh = refresh;
        SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        var title = new TextBlock
        {
            Text = "用量", FontFamily = SqlAssistChrome.InterfaceFont, FontSize = _metrics.Title, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        header.Children.Add(title);

        // 長時間操作的進度貼在頁首下方；不遮內容，做完就收起。
        _busyText = SqlAssistChrome.CreateStatusText(_metrics);
        var busyContent = new StackPanel();
        busyContent.Children.Add(_busyText);
        _busyMeter.Margin = new Thickness(0, 4, 0, 0);
        busyContent.Children.Add(_busyMeter);
        _busy = new Border { Child = busyContent, Padding = new Thickness(2, 0, 2, 8), Visibility = Visibility.Collapsed };
        SetDock(_busy, Dock.Top); Children.Add(_busy);

        _message = SqlAssistChrome.CreateHint("", _metrics);
        _message.Margin = new Thickness(2, 0, 0, 8); _message.Visibility = Visibility.Collapsed;
        SetDock(_message, Dock.Top); Children.Add(_message);

        // 主卡片：健康狀態、容量量表與磁碟。整頁唯一一塊有底色的表面，其餘段落靠留白分層。
        _healthTitle = Text(_metrics.Body, FontWeights.SemiBold, ThemeBrush.ListForeground);
        _capacityValue = Text(_metrics.Title, FontWeights.SemiBold, ThemeBrush.ListForeground);
        _healthDetail = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, wrap: true);
        _capacityDetail = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground);
        _disk = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, wrap: true);
        _maintenance = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, wrap: true);
        _compactHint = SqlAssistChrome.CreateButton("", _metrics);
        _compactHint.Content = SqlAssistChrome.CreateMemoryLabel(SqlIcon.Compact, "壓縮以縮小檔案");
        _compactHint.Padding = new Thickness(6, 2, 6, 2);
        _compactHint.Click += (_, _) => ActionRequested?.Invoke(this, SqlMemoryUsageAction.Compact);
        _hero = BuildHero();
        _content.Children.Add(_hero);

        _content.Children.Add(Section("配額", _quotas));
        _stats.Margin = new Thickness(-4, 0, -4, 0);
        _content.Children.Add(Section("紀錄", _stats));
        _range = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, wrap: true);
        _range.Margin = new Thickness(0, 8, 0, 0);
        _content.Children.Add(_range);
        _content.Children.Add(Section("依伺服器", _servers));
        foreach (var entry in Actions)
        {
            var button = SqlAssistChrome.CreateButton("", _metrics, primary: entry.Action == SqlMemoryUsageAction.Maintain);
            if (entry.Tone != SqlActionTone.Neutral) button.Template = SqlAssistChrome.CreateGhostButtonTemplate(entry.Tone);
            button.Content = SqlAssistChrome.CreateMemoryLabel(entry.Icon, entry.Label);
            button.ToolTip = entry.ToolTip; AutomationProperties.SetName(button, entry.Label);
            button.Padding = new Thickness(8, 4, 10, 4); button.Margin = new Thickness(0, 0, 6, 6); button.MinHeight = 30;
            var action = entry.Action;
            button.Click += (_, _) => ActionRequested?.Invoke(this, action);
            _buttons[action] = button;
            _actions.Children.Add(button);
        }
        _content.Children.Add(Section("整理", _actions));
        _content.Children.Add(Section("最近整理", _activities));

        var scroll = new ScrollViewer
        {
            Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 4, 8)
        };
        _loading = new SqlLoadingSurface(scroll);
        Children.Add(_loading);
        _content.Visibility = Visibility.Collapsed;

        SizeChanged += (_, _) => _stats.Columns = ActualWidth >= CompactWidth ? 2 : 1;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || _busy.Visibility == Visibility.Visible) return;
            e.Handled = true;
            BackRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>頁面內容是否仍在等第一份資料；已有畫面時重新整理不再蓋上載入圖示。</summary>
    public bool IsLoading => _loading.IsLoading;

    public Button ActionButton(SqlMemoryUsageAction action) => _buttons[action];

    /// <summary>第一次載入顯示表面載入圖示；已有資料時保留舊畫面，量表在新資料到時直接滑到新值。</summary>
    public void BeginLoad()
    {
        SetMessage("");
        _loading.IsLoading = !_hasSummary;
    }

    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public void ShowSummary(SqlMemoryUsageSummary summary, bool? motion = null)
    {
        if (summary == null) throw new ArgumentNullException(nameof(summary));
        _loading.IsLoading = false;
        var first = !_hasSummary;
        _hasSummary = true;
        _content.Visibility = Visibility.Visible;

        _healthDot.SetResourceReference(Shape.FillProperty, SqlUsageMeter.Brush(summary.Health));
        _healthTitle.Text = summary.HealthTitle;
        _healthDetail.Text = summary.HealthDetail;
        _capacityValue.Text = summary.Capacity.Value;
        _capacityDetail.Text = summary.Capacity.Detail; _capacityDetail.ToolTip = summary.Capacity.Detail;
        _capacity.SetValue(summary.Capacity.Ratio, summary.Capacity.Severity, motion);
        _capacity.Visibility = summary.Capacity.Ratio is null ? Visibility.Collapsed : Visibility.Visible;
        _disk.Text = summary.Disk;
        _maintenance.Text = summary.Maintenance;
        _compactHint.Visibility = summary.CompactRecommended ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(_hero, summary.HealthTitle + "。" + summary.HealthDetail);

        _quotas.Children.Clear();
        foreach (var quota in summary.Quotas) _quotas.Children.Add(QuotaRow(quota, motion));

        _stats.Children.Clear();
        foreach (var stat in summary.Stats) _stats.Children.Add(StatTile(stat));
        _range.Text = summary.Range;

        _servers.Children.Clear();
        if (summary.Servers.Count == 0) _servers.Children.Add(Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, "還沒有帶連線的紀錄"));
        foreach (var share in summary.Servers) _servers.Children.Add(ShareRow(share, first ? motion : false));

        _activities.Children.Clear();
        if (summary.Activities.Count == 0)
            _activities.Children.Add(Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, "本次開啟 SSMS 後還沒有整理紀錄"));
        foreach (var activity in summary.Activities) _activities.Children.Add(ActivityRow(activity));

        // 內容表面只在第一次出現時淡入；重新整理時由量表的長度變化說明狀態。
        if (first) SqlAssistChrome.PlayAppear(_content, motion);
    }

    /// <summary>儲存無法讀取或已停用時的一行說明；保留已有的畫面，不以空白冒充零用量。</summary>
    public void ShowUnavailable(string message)
    {
        _loading.IsLoading = false;
        SetMessage(message);
    }

    public void SetMessage(string message)
    {
        _message.Text = message;
        _message.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>長時間操作進行中：顯示進度文字、停用所有動作；null 結束。</summary>
    public void SetBusy(string? text)
    {
        var busy = text != null;
        _busyText.Text = text ?? "";
        if (busy && _busy.Visibility != Visibility.Visible)
        {
            _busy.Visibility = Visibility.Visible;
            SqlAssistChrome.PlayAppear(_busy);
        }
        else if (!busy) _busy.Visibility = Visibility.Collapsed;
        foreach (var button in _buttons.Values) button.IsEnabled = !busy;
        _compactHint.IsEnabled = _refresh.IsEnabled = !busy;
    }

    private Border BuildHero()
    {
        var stack = new StackPanel();
        var top = new DockPanel();
        _capacityValue.VerticalAlignment = VerticalAlignment.Center;
        SetDock(_capacityValue, Dock.Right); top.Children.Add(_capacityValue);
        _healthDot.Margin = new Thickness(0, 0, 8, 0);
        SetDock(_healthDot, Dock.Left); top.Children.Add(_healthDot);
        _healthTitle.VerticalAlignment = VerticalAlignment.Center; _healthTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        top.Children.Add(_healthTitle);
        stack.Children.Add(top);
        _healthDetail.Margin = new Thickness(16, 2, 0, 12);
        stack.Children.Add(_healthDetail);
        stack.Children.Add(_capacity);
        _capacityDetail.Margin = new Thickness(0, 6, 0, 0); _capacityDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(_capacityDetail);
        var disk = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _compactHint.VerticalAlignment = VerticalAlignment.Center; _compactHint.Margin = new Thickness(8, 0, -4, 0);
        SetDock(_compactHint, Dock.Right); disk.Children.Add(_compactHint);
        _disk.VerticalAlignment = VerticalAlignment.Center;
        disk.Children.Add(_disk);
        stack.Children.Add(disk);
        _maintenance.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(_maintenance);
        var hero = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14)
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.BadgeBackground).WithTheme(Border.BorderBrushProperty, ThemeBrush.Hairline);
        AutomationProperties.SetName(hero, "容量");
        return hero;
    }

    private FrameworkElement Section(string title, UIElement body)
    {
        var section = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var label = SqlAssistChrome.CreateLabel(title, _metrics);
        label.Margin = new Thickness(0, 0, 0, 8);
        label.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        section.Children.Add(label);
        section.Children.Add(body);
        return section;
    }

    private FrameworkElement QuotaRow(SqlMemoryGauge quota, bool? motion)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10), ToolTip = quota.Detail };
        var line = new DockPanel();
        var value = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.ListForeground, quota.Value);
        SetDock(value, Dock.Right); line.Children.Add(value);
        line.Children.Add(Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.ListForeground, quota.Label));
        row.Children.Add(line);
        // 量表以標籤沿用：重新整理時從上一次的長度滑到新值，而不是每次從零長出來。
        if (!_quotaMeters.TryGetValue(quota.Label, out var meter))
            _quotaMeters[quota.Label] = meter = new SqlUsageMeter(4);
        (meter.Parent as Panel)?.Children.Remove(meter);
        meter.Margin = new Thickness(0, 4, 0, 0);
        meter.SetValue(quota.Ratio, quota.Severity, motion);
        meter.Visibility = quota.Ratio is null ? Visibility.Collapsed : Visibility.Visible;
        row.Children.Add(meter);
        AutomationProperties.SetName(row, quota.Label + " " + quota.Value);
        return row;
    }

    private FrameworkElement StatTile(SqlMemoryStat stat)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, stat.Label));
        stack.Children.Add(Text(_metrics.Title + 3, FontWeights.SemiBold, ThemeBrush.ListForeground, stat.Value));
        var detail = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, stat.Detail);
        detail.TextTrimming = TextTrimming.CharacterEllipsis; detail.ToolTip = stat.Detail;
        stack.Children.Add(detail);
        var tile = new Border { Child = stack, Padding = new Thickness(0, 2, 4, 10), Margin = new Thickness(4, 0, 4, 0) };
        AutomationProperties.SetName(tile, stat.Label + " " + stat.Value + "，" + stat.Detail);
        return tile;
    }

    private FrameworkElement ShareRow(SqlMemoryShareBar share, bool? motion)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6), ToolTip = share.Name + "：" + share.Value + " 筆" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 80 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 48 });
        var name = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.ListForeground, share.Name);
        name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        var meter = new SqlUsageMeter(4) { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(meter, 1); row.Children.Add(meter);
        meter.SetValue(share.Ratio, SqlMemoryUsageSeverity.Normal, motion);
        var value = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, share.Value);
        value.TextAlignment = TextAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2); row.Children.Add(value);
        AutomationProperties.SetName(row, share.Name + " " + share.Value);
        return row;
    }

    private FrameworkElement ActivityRow(SqlMemoryActivityLine activity)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var time = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, activity.Time);
        time.Margin = new Thickness(8, 0, 0, 0);
        SetDock(time, Dock.Right); row.Children.Add(time);
        var marker = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(1, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        marker.SetResourceReference(Shape.FillProperty, activity.Failed ? ThemeBrush.MeterCritical : ThemeBrush.MeterNormal);
        SetDock(marker, Dock.Left); row.Children.Add(marker);
        var text = new TextBlock { FontFamily = SqlAssistChrome.InterfaceFont, FontSize = _metrics.Caption, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new System.Windows.Documents.Run(activity.Title + (activity.Failed ? "失敗" : "")) { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run(" · " + activity.Detail));
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        text.ToolTip = activity.Title + " · " + activity.Detail;
        row.Children.Add(text);
        AutomationProperties.SetName(row, activity.Title + (activity.Failed ? "失敗，" : "，") + activity.Detail + "，" + activity.Time);
        return row;
    }

    private static FrameworkElement BackLabel()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // 返回箭頭是控制項外觀：沿用展開箭頭的向量，轉向左方。
        var chevron = SqlAssistChrome.CreateChevron();
        chevron.RenderTransform = new RotateTransform(90);
        content.Children.Add(chevron);
        var text = SqlAssistChrome.CreateMemoryButtonText("清單");
        text.Margin = new Thickness(2, 0, 0, 0);
        content.Children.Add(text);
        return content;
    }

    private TextBlock Text(double size, FontWeight weight, ThemeBrush brush, string text = "", bool wrap = false) =>
        new TextBlock
        {
            Text = text, FontFamily = SqlAssistChrome.InterfaceFont, FontSize = size, FontWeight = weight,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        }.WithTheme(TextBlock.ForegroundProperty, brush);
}
