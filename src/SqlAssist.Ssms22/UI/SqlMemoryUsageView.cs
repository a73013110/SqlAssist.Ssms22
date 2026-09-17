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

internal enum SqlMemoryUsageAction { Maintain, Cleanup, Compact, Backup, OpenFolder }

/// <summary>
/// SQL Memory 的用量分頁：容量量表、健康狀態、配額、各類筆數、伺服器分布、整理動作與最近的整理紀錄。
/// </summary>
/// <remarks>
/// 與 History／Favorites 同一組分頁，不另開視窗也不另立頁首：分頁本身就是抬頭，重新整理與設定沿用工具列。
/// 每個區塊是一張與清單卡片同底色、細線與圓角的卡片，小標在卡片外；卡片內文字共用
/// <see cref="SqlAssistChrome.CardPadding"/> 的左軸線，內容不貼著工具窗邊緣。
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
    };

    private readonly SqlAssistChrome.Metrics _metrics = SqlAssistChrome.DefaultMetrics;
    private readonly Dictionary<SqlMemoryUsageAction, Button> _buttons = new();
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

    public event EventHandler<SqlMemoryUsageAction>? ActionRequested;

    public SqlMemoryUsageView()
    {
        AutomationProperties.SetName(this, "SQL Memory 用量");
        LastChildFill = true;

        // 長時間操作的進度貼在內容上方；不遮內容，做完就收起。
        _busyText = SqlAssistChrome.CreateStatusText(_metrics);
        var busyContent = new StackPanel();
        busyContent.Children.Add(_busyText);
        _busyMeter.Margin = new Thickness(0, 4, 0, 0);
        busyContent.Children.Add(_busyMeter);
        _busy = new Border { Child = busyContent, Padding = new Thickness(0, 0, 2, 8), Visibility = Visibility.Collapsed };
        SetDock(_busy, Dock.Top); Children.Add(_busy);

        _message = SqlAssistChrome.CreateHint("", _metrics);
        _message.Margin = new Thickness(0, 0, 0, 8); _message.Visibility = Visibility.Collapsed;
        SetDock(_message, Dock.Top); Children.Add(_message);

        // 主卡片：健康狀態、容量量表與磁碟；沒有小標，其餘區塊是帶小標的分段卡片。
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

        _content.Children.Add(SqlAssistChrome.CreateCardSection("配額", _quotas));
        _stats.Margin = new Thickness(-4, 0, -4, 0);
        _range = Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, wrap: true);
        var records = new StackPanel();
        records.Children.Add(_stats); records.Children.Add(_range);
        _content.Children.Add(SqlAssistChrome.CreateCardSection("紀錄", records));
        _content.Children.Add(SqlAssistChrome.CreateCardSection("依伺服器", _servers));
        foreach (var entry in Actions)
        {
            var button = SqlAssistChrome.CreateButton("", _metrics, primary: entry.Action == SqlMemoryUsageAction.Maintain);
            if (entry.Tone != SqlActionTone.Neutral) button.Template = SqlAssistChrome.CreateGhostButtonTemplate(entry.Tone);
            button.Content = SqlAssistChrome.CreateMemoryLabel(entry.Icon, entry.Label);
            button.ToolTip = entry.ToolTip; AutomationProperties.SetName(button, entry.Label);
            // 與工具列按鈕同一個高度與內距；換行時列距 4，和篩選膠囊的節奏一致。
            button.Height = 28; button.Padding = new Thickness(6, 3, 8, 3); button.Margin = new Thickness(0, 0, 4, 4);
            var action = entry.Action;
            button.Click += (_, _) => ActionRequested?.Invoke(this, action);
            _buttons[action] = button;
            _actions.Children.Add(button);
        }
        // 按鈕自帶右與下的間距；容器抵銷最後一欄與最後一列，卡片四邊內距才一致。
        _actions.Margin = new Thickness(0, 0, -4, -4);
        _content.Children.Add(SqlAssistChrome.CreateCardSection("整理", _actions));
        _content.Children.Add(SqlAssistChrome.CreateCardSection("最近整理", _activities));

        var scroll = new ScrollViewer
        {
            Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 2, 8)
        };
        _loading = new SqlLoadingSurface(scroll);
        Children.Add(_loading);
        _content.Visibility = Visibility.Collapsed;

        SizeChanged += (_, _) => _stats.Columns = ActualWidth >= CompactWidth ? 2 : 1;
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
        foreach (var quota in summary.Quotas) AddRow(_quotas, QuotaRow(quota, motion), 10);

        _stats.Children.Clear();
        foreach (var stat in summary.Stats) _stats.Children.Add(StatTile(stat));
        _range.Text = summary.Range;

        _servers.Children.Clear();
        if (summary.Servers.Count == 0) _servers.Children.Add(Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, "還沒有帶連線的紀錄"));
        foreach (var share in summary.Servers) AddRow(_servers, ShareRow(share, first ? motion : false), 6);

        _activities.Children.Clear();
        if (summary.Activities.Count == 0)
            _activities.Children.Add(Text(_metrics.Caption, FontWeights.Normal, ThemeBrush.DimForeground, "本次開啟 SSMS 後還沒有整理紀錄"));
        foreach (var activity in summary.Activities) AddRow(_activities, ActivityRow(activity), 6);

        // 內容表面只在第一次出現時淡入；重新整理時由量表的長度變化說明狀態。
        if (first) SqlAssistChrome.PlayAppear(_content, motion);
    }

    /// <summary>讀取失敗：保留已有的畫面並說明原因，不以空白冒充零用量。</summary>
    public void ShowFailure(string message)
    {
        _loading.IsLoading = false;
        SetMessage(message);
    }

    /// <summary>儲存已停用或換了一份：舊數字不屬於現在的資料庫，整頁收起；原因由工具窗的狀態列說明。</summary>
    public void Clear()
    {
        _loading.IsLoading = false;
        _hasSummary = false;
        _content.Visibility = Visibility.Collapsed;
        SetMessage("");
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
        _compactHint.IsEnabled = !busy;
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
        var hero = SqlAssistChrome.CreateSurface(stack);
        hero.Padding = SqlAssistChrome.CardPadding;
        AutomationProperties.SetName(hero, "容量");
        return hero;
    }

    private FrameworkElement QuotaRow(SqlMemoryGauge quota, bool? motion)
    {
        var row = new StackPanel { ToolTip = quota.Detail };
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
        var row = new Grid { ToolTip = share.Name + "：" + share.Value + " 筆" };
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
        var row = new DockPanel();
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

    /// <summary>列距只加在列與列之間；最後一列不帶下緣，卡片四邊內距才一致。</summary>
    private static void AddRow(Panel panel, FrameworkElement row, double gap)
    {
        row.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : gap, 0, 0);
        panel.Children.Add(row);
    }

    private TextBlock Text(double size, FontWeight weight, ThemeBrush brush, string text = "", bool wrap = false) =>
        new TextBlock
        {
            Text = text, FontFamily = SqlAssistChrome.InterfaceFont, FontSize = size, FontWeight = weight,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        }.WithTheme(TextBlock.ForegroundProperty, brush);
}
