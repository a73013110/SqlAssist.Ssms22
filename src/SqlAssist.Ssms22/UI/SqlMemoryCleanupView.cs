using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 清除紀錄對話框的內容：清除對象、History 範圍、試算摘要與頁尾。試算與送出由宿主視窗接線。
/// </summary>
/// <remarks>
/// 版面照共用對話框的節奏：資訊列說明不可復原與受保護的範圍，對象是一組整列可點的選項，
/// 試算結果放在頁尾左側、緊鄰會用到它的按鈕。清除是語意色的主要動作，按鈕寫出筆數；
/// 取消才是預設與初始焦點，Enter 不會清除。
/// </remarks>
internal sealed class SqlMemoryCleanupView : DockPanel
{
    private static readonly (string Label, int? Days)[] Periods =
    {
        ("7 天以前", 7), ("30 天以前", 30), ("90 天以前", 90), ("1 年以前", 365), ("全部期間", null),
    };

    private static readonly int[] KeepOptions = { 1, 3, 5, 10, 20, 50 };

    private readonly CheckBox _executions = new() { IsChecked = true };
    private readonly CheckBox _drafts = new();
    private readonly CheckBox _recovery = new();
    private readonly CheckBox _favorites = new();
    private readonly ComboBox _keep = SqlAssistChrome.CreateComboBox(SqlAssistChrome.DefaultMetrics);
    private readonly FrameworkElement _keepRow;
    private readonly SqlPillSelector _period;
    private readonly FrameworkElement _historyScope;
    private readonly TextBox _server;
    private readonly TextBox _database;
    private readonly TextBlock _headline;
    private readonly TextBlock _breakdown = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly SqlUsageMeter _estimating = new(2) { IsIndeterminate = true, Visibility = Visibility.Hidden };
    private readonly TextBlock _status = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);

    public event EventHandler? Changed;

    /// <param name="serverBar">伺服器輸入列；名稱建議需要儲存服務，由宿主建立。</param>
    public SqlMemoryCleanupView(FrameworkElement serverBar, TextBox server, FrameworkElement databaseBar, TextBox database)
    {
        _server = server;
        _database = database;
        LastChildFill = true;

        var info = SqlAssistChrome.CreateInfoBar(SqlIcon.Warning,
            "清除後無法復原。收藏的目前版本、仍開著的查詢視窗，以及還被引用的版本會保留。");
        SetDock(info, Dock.Top); Children.Add(info);

        _headline = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont, FontSize = SqlAssistChrome.DefaultMetrics.Body, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        var summary = new StackPanel();
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        summary.Children.Add(_headline);
        // 四類都有筆數時窄窗放不下一行；明細換行而不省略，按下清除前要讀得到全部。
        _breakdown.TextTrimming = TextTrimming.None; _breakdown.TextWrapping = TextWrapping.Wrap;
        summary.Children.Add(_breakdown);
        _estimating.Margin = new Thickness(0, 4, 0, 0);
        summary.Children.Add(_estimating);
        Cancel = SqlAssistChrome.CreateButton("取消", SqlAssistChrome.DefaultMetrics);
        Cancel.IsCancel = true; Cancel.IsDefault = true;
        Submit = SqlAssistChrome.CreateDangerButton("清除");
        Submit.MinWidth = 120;
        var footer = SqlAssistChrome.CreateDialogFooter(summary, Cancel, Submit);
        SetDock(footer, Dock.Bottom); Children.Add(footer);

        _status.Margin = new Thickness(0, 12, 0, 0); _status.Visibility = Visibility.Collapsed;
        SetDock(_status, Dock.Bottom); Children.Add(_status);

        _keep.Width = 64;
        foreach (var option in KeepOptions) _keep.Items.Add(option.ToString(CultureInfo.InvariantCulture));
        _keep.SelectedIndex = Array.IndexOf(KeepOptions, 10);
        AutomationProperties.SetName(_keep, "每個收藏保留的版本數");
        var keepRow = new StackPanel { Orientation = Orientation.Horizontal };
        keepRow.Children.Add(Hint("保留最新"));
        _keep.Margin = new Thickness(6, 0, 6, 0);
        keepRow.Children.Add(_keep);
        keepRow.Children.Add(Hint("個"));
        _keepRow = keepRow;
        var targets = SqlAssistChrome.CreateOptionGroup(
            SqlAssistChrome.CreateOptionRow(_executions, "執行紀錄", "History 上的執行列，以及逐次保存的執行事件"),
            SqlAssistChrome.CreateOptionRow(_drafts, "草稿", "已有版本的草稿；每個工作階段的最新版本會保留"),
            SqlAssistChrome.CreateOptionRow(_recovery, "已關閉視窗的回復內容", "仍開著的查詢視窗有工作階段租約，一律不清"),
            SqlAssistChrome.CreateOptionRow(_favorites, "收藏的舊版本", "目前版本永遠保留", keepRow));

        _period = new SqlPillSelector(Array.ConvertAll(Periods, period => (period.Label, period.Days is null ? SqlIcon.AnyTime : SqlIcon.Calendar)));
        _period.SelectedIndex = 1;
        AutomationProperties.SetName(_period, "期間");
        var scope = new StackPanel();
        scope.Children.Add(_period);
        var connection = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        connection.ColumnDefinitions.Add(new ColumnDefinition());
        connection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        connection.ColumnDefinitions.Add(new ColumnDefinition());
        connection.Children.Add(SqlAssistChrome.CreateMemoryField("伺服器", serverBar, server));
        var databaseField = SqlAssistChrome.CreateMemoryField("資料庫", databaseBar, database);
        Grid.SetColumn(databaseField, 2); connection.Children.Add(databaseField);
        scope.Children.Add(connection);

        var form = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        form.Children.Add(SqlAssistChrome.CreateSection("清除對象", targets, first: true));
        _historyScope = SqlAssistChrome.CreateSection("History 範圍", scope);
        form.Children.Add(_historyScope);
        Children.Add(new ScrollViewer
        {
            Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false
        });

        foreach (var box in new[] { _executions, _drafts, _recovery, _favorites })
        {
            box.Checked += (_, _) => OnChanged();
            box.Unchecked += (_, _) => OnChanged();
        }
        _period.SelectionChanged += (_, _) => OnChanged();
        _keep.SelectionChanged += (_, _) => OnChanged();
        _server.TextChanged += (_, _) => OnChanged();
        _database.TextChanged += (_, _) => OnChanged();
        UpdateScope();
        Loaded += (_, _) => Cancel.Focus();
    }

    public Button Cancel { get; }

    public Button Submit { get; }

    /// <summary>目前控制項組成的請求；沒有勾任何對象時為 null。</summary>
    public SqlMemoryCleanupRequest? Compose(DateTimeOffset now)
    {
        var targets = SqlMemoryCleanupTargets.None;
        if (_executions.IsChecked == true) targets |= SqlMemoryCleanupTargets.Executions;
        if (_drafts.IsChecked == true) targets |= SqlMemoryCleanupTargets.Drafts;
        if (_recovery.IsChecked == true) targets |= SqlMemoryCleanupTargets.ClosedRecovery;
        if (_favorites.IsChecked == true) targets |= SqlMemoryCleanupTargets.FavoriteRevisions;
        if (targets == SqlMemoryCleanupTargets.None) return null;
        var days = Periods[Math.Max(0, _period.SelectedIndex)].Days;
        return new SqlMemoryCleanupRequest(targets, days is { } value ? now.AddDays(-value) : null,
            _server.Text, _database.Text, KeepOptions[Math.Max(0, _keep.SelectedIndex)]);
    }

    /// <summary>條件變了、試算還沒回來：清除停用，按鈕不留舊筆數。</summary>
    public void ShowEstimating(bool hasTargets)
    {
        _headline.Text = hasTargets ? "正在試算…" : "至少勾選一種清除對象";
        _breakdown.Text = "";
        _estimating.Visibility = hasTargets ? Visibility.Visible : Visibility.Hidden;
        SetSubmit(0);
    }

    public void ShowEstimate(SqlMemoryCleanupEstimate estimate)
    {
        _headline.Text = SqlMemoryUsageSummary.CleanupHeadline(estimate);
        _breakdown.Text = SqlMemoryUsageSummary.CleanupBreakdown(estimate);
        _estimating.Visibility = Visibility.Hidden;
        SetSubmit(estimate.Total);
    }

    public void ShowEstimateFailure(string message)
    {
        _headline.Text = "無法試算";
        _breakdown.Text = message;
        _estimating.Visibility = Visibility.Hidden;
        SetSubmit(0);
    }

    /// <summary>名稱建議這類附屬操作的失敗；空字串收起。</summary>
    public void Report(string message)
    {
        _status.Text = message;
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChanged()
    {
        UpdateScope();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateScope()
    {
        // 範圍只作用在 History 類對象；只清收藏版本時停用，免得誤以為期間也適用。
        var touchesHistory = _executions.IsChecked == true || _drafts.IsChecked == true || _recovery.IsChecked == true;
        _historyScope.IsEnabled = touchesHistory || _favorites.IsChecked != true;
        _historyScope.Opacity = _historyScope.IsEnabled ? 1 : 0.5;
        _keepRow.IsEnabled = _favorites.IsChecked == true;
        _keepRow.Opacity = _keepRow.IsEnabled ? 1 : 0.5;
    }

    private void SetSubmit(long total)
    {
        Submit.IsEnabled = total > 0;
        Submit.Content = total > 0 ? "清除 " + SqlMemoryUsageSummary.Count(total) + " 筆" : "清除";
    }

    private static TextBlock Hint(string text)
    {
        var hint = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
        hint.Text = text;
        return hint;
    }
}
