using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 手動清除紀錄的條件與試算；按下清除並再次確認後回傳請求，實際刪除由用量頁執行並顯示進度。
/// </summary>
/// <remarks>
/// 每次改條件就在背景重新試算，按鈕寫出筆數：使用者按下去之前就知道會刪多少。
/// 試算是上限——共用內容與保護根在刪除交易內才逐筆重查——所以文案說「最多」。
/// 取消是預設與初始焦點；清除是語意色的幽靈按鈕，不是 Enter 會觸發的預設動作。
/// </remarks>
internal sealed class SqlMemoryCleanupWindow : DialogWindow
{
    private static readonly (string Label, int? Days)[] Periods =
    {
        ("7 天以前", 7), ("30 天以前", 30), ("90 天以前", 90), ("1 年以前", 365), ("全部期間", null),
    };

    private static readonly int[] KeepOptions = { 1, 3, 5, 10, 20, 50 };

    private readonly SqlAssistPackage _package;
    private readonly CheckBox _executions = Check("執行紀錄", "History 上的執行列與逐次保存的執行事件。");
    private readonly CheckBox _drafts = Check("草稿", "History 上已有版本的草稿；Session 的最新版本受保護，會留著。");
    private readonly CheckBox _recovery = Check("已關閉視窗的回復內容", "仍開著的查詢視窗有工作階段租約，一律不清。");
    private readonly CheckBox _favorites = Check("收藏的舊版本", "每個收藏只留最新幾個版本；目前版本永遠保留。");
    private readonly SqlPillSelector _period;
    private readonly ComboBox _keep = SqlAssistChrome.CreateComboBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _server = SqlConnectionTagInput.CreateInput();
    private readonly TextBox _database = SqlConnectionTagInput.CreateInput();
    private readonly FrameworkElement _historyScope;
    private readonly TextBlock _estimate = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly SqlUsageMeter _estimating = new(2) { IsIndeterminate = true, Visibility = Visibility.Hidden };
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly Button _submit;
    private readonly Button _cancel;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource _pending = new();
    private SqlMemoryCleanupEstimate? _latest;
    private int _generation;

    private SqlMemoryCleanupWindow(SqlAssistPackage package)
    {
        _package = package;
        SqlMemoryActions.ConfigureWindow(this, package, "清除 SQL Memory 紀錄", 560, 600);
        MinWidth = 480; MinHeight = 520;
        SizeToContent = SizeToContent.Height;

        var root = new DockPanel { Margin = new Thickness(16) };
        var context = SqlAssistChrome.CreateMetadataText("清除後無法復原。收藏的目前版本、仍開著的視窗與被引用的版本鏈受保護，不會被清掉。",
            SqlAssistChrome.DefaultMetrics);
        context.TextWrapping = TextWrapping.Wrap; context.TextTrimming = TextTrimming.None; context.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(context, Dock.Top); root.Children.Add(context);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        _cancel = SqlAssistChrome.CreateButton("取消", SqlAssistChrome.DefaultMetrics);
        _cancel.IsCancel = true; _cancel.IsDefault = true; _cancel.Margin = new Thickness(0, 0, 8, 0);
        _submit = SqlAssistChrome.CreateButton("清除", SqlAssistChrome.DefaultMetrics);
        _submit.Template = SqlAssistChrome.CreateGhostButtonTemplate(SqlActionTone.Danger);
        _submit.MinWidth = 96;
        actions.Children.Add(_cancel); actions.Children.Add(_submit);
        footer.Children.Add(_status);

        var form = new StackPanel();
        form.Children.Add(SqlAssistChrome.CreateLabel("清除對象", SqlAssistChrome.DefaultMetrics));
        foreach (var box in new[] { _executions, _drafts, _recovery }) form.Children.Add(box);
        var favoriteRow = new DockPanel();
        _keep.Width = 72; _keep.Margin = new Thickness(8, 0, 0, 0); _keep.VerticalAlignment = VerticalAlignment.Center;
        foreach (var option in KeepOptions) _keep.Items.Add(option.ToString(CultureInfo.InvariantCulture));
        _keep.SelectedIndex = Array.IndexOf(KeepOptions, 10);
        System.Windows.Automation.AutomationProperties.SetName(_keep, "每個收藏保留的版本數");
        var keepSuffix = SqlAssistChrome.CreateHint("個版本", SqlAssistChrome.DefaultMetrics);
        keepSuffix.Margin = new Thickness(6, 0, 0, 0); keepSuffix.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(keepSuffix, Dock.Right);
        DockPanel.SetDock(_keep, Dock.Right);
        favoriteRow.Children.Add(keepSuffix); favoriteRow.Children.Add(_keep);
        var keepPrefix = SqlAssistChrome.CreateHint("每個保留最新", SqlAssistChrome.DefaultMetrics);
        keepPrefix.Margin = new Thickness(8, 0, 0, 0); keepPrefix.VerticalAlignment = VerticalAlignment.Center;
        keepPrefix.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(keepPrefix, Dock.Right); favoriteRow.Children.Add(keepPrefix);
        favoriteRow.Children.Add(_favorites);
        form.Children.Add(favoriteRow);

        var scope = new StackPanel();
        scope.Children.Add(SqlAssistChrome.CreateLabel("History 範圍", SqlAssistChrome.DefaultMetrics));
        _period = new SqlPillSelector(Array.ConvertAll(Periods, period =>
            (period.Label, period.Days is null ? SqlIcon.AnyTime : SqlIcon.Calendar)));
        _period.SelectedIndex = 1;
        scope.Children.Add(_period);
        var connection = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        connection.ColumnDefinitions.Add(new ColumnDefinition());
        connection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        connection.ColumnDefinitions.Add(new ColumnDefinition());
        var serverField = SqlAssistChrome.CreateMemoryField("伺服器", SqlConnectionTagInput.CreateBar(package, SqlIcon.Server, _server, "伺服器",
            databases: false, () => null, includeFavorites: false, Report), _server);
        var databaseField = SqlAssistChrome.CreateMemoryField("資料庫", SqlConnectionTagInput.CreateBar(package, SqlIcon.Database, _database, "資料庫",
            databases: true, () => _server.Text.Trim() is { Length: > 0 } text ? text : null, includeFavorites: false, Report), _database);
        Grid.SetColumn(databaseField, 2);
        connection.Children.Add(serverField); connection.Children.Add(databaseField);
        scope.Children.Add(connection);
        _historyScope = scope;
        form.Children.Add(scope);

        var estimate = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        _estimate.Margin = new Thickness(0); _estimate.TextWrapping = TextWrapping.Wrap;
        estimate.Children.Add(_estimate);
        _estimating.Margin = new Thickness(0, 6, 0, 0);
        estimate.Children.Add(_estimating);
        form.Children.Add(estimate);
        root.Children.Add(form);
        Content = root;

        _executions.IsChecked = true;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SqlMemoryActions.RunAsync(EstimateAsync, Report); };
        foreach (var box in new[] { _executions, _drafts, _recovery, _favorites })
        {
            box.Checked += (_, _) => Changed();
            box.Unchecked += (_, _) => Changed();
        }
        _period.SelectionChanged += (_, _) => Changed();
        _keep.SelectionChanged += (_, _) => Changed();
        _server.TextChanged += (_, _) => Changed();
        _database.TextChanged += (_, _) => Changed();
        _submit.Click += (_, _) => SqlMemoryActions.Run(Submit, Report);
        Loaded += (_, _) => _cancel.Focus();
        Closed += (_, _) => { _debounce.Stop(); _pending.Cancel(); _pending.Dispose(); };
        Changed();
    }

    public SqlMemoryCleanupRequest? Request { get; private set; }

    /// <returns>使用者確認的請求；取消為 null。</returns>
    public static SqlMemoryCleanupRequest? Show(SqlAssistPackage package)
    {
        var window = new SqlMemoryCleanupWindow(package);
        return window.ShowModal() == true ? window.Request : null;
    }

    /// <summary>目前控制項組成的請求；沒有勾任何對象時為 null。</summary>
    private SqlMemoryCleanupRequest? Compose()
    {
        var targets = SqlMemoryCleanupTargets.None;
        if (_executions.IsChecked == true) targets |= SqlMemoryCleanupTargets.Executions;
        if (_drafts.IsChecked == true) targets |= SqlMemoryCleanupTargets.Drafts;
        if (_recovery.IsChecked == true) targets |= SqlMemoryCleanupTargets.ClosedRecovery;
        if (_favorites.IsChecked == true) targets |= SqlMemoryCleanupTargets.FavoriteRevisions;
        if (targets == SqlMemoryCleanupTargets.None) return null;
        var days = Periods[Math.Max(0, _period.SelectedIndex)].Days;
        return new SqlMemoryCleanupRequest(targets, days is { } value ? DateTimeOffset.UtcNow.AddDays(-value) : null,
            _server.Text, _database.Text, KeepOptions[Math.Max(0, _keep.SelectedIndex)]);
    }

    private void Changed()
    {
        _latest = null;
        _generation++;
        _pending.Cancel(); _pending.Dispose(); _pending = new CancellationTokenSource();
        var request = Compose();
        // 範圍只作用在 History 類對象；只清收藏版本時把它收起來，免得誤以為期間也適用。
        _historyScope.IsEnabled = request?.TouchesHistory ?? true;
        _historyScope.Opacity = _historyScope.IsEnabled ? 1 : 0.5;
        _keep.IsEnabled = _favorites.IsChecked == true;
        UpdateSubmit();
        if (request == null)
        {
            _estimate.Text = "至少勾選一種清除對象。";
            _estimating.Visibility = Visibility.Hidden;
            _debounce.Stop();
            return;
        }
        _estimate.Text = "正在試算符合條件的紀錄…";
        _estimating.Visibility = Visibility.Visible;
        _debounce.Stop(); _debounce.Start();
    }

    private async Task EstimateAsync()
    {
        if (Compose() is not { } request) return;
        var generation = _generation;
        var token = _pending.Token;
        try
        {
            var estimate = await SqlMemoryHost.Runtime.EstimateCleanupAsync(request, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            _latest = estimate;
            _estimate.Text = Describe(estimate);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (generation != _generation) return;
            _estimate.Text = SqlMemoryTimeText.Failure("試算", error);
        }
        finally
        {
            if (generation == _generation) { _estimating.Visibility = Visibility.Hidden; UpdateSubmit(); }
        }
    }

    internal static string Describe(SqlMemoryCleanupEstimate estimate)
    {
        if (estimate.Total == 0) return "沒有符合條件的紀錄。";
        var parts = new System.Collections.Generic.List<string>();
        void Add(long count, string label) { if (count > 0) parts.Add(label + " " + SqlMemoryUsageSummary.Count(count)); }
        Add(estimate.ExecutionEntries, "執行紀錄");
        Add(estimate.Drafts, "草稿");
        Add(estimate.RecoveryItems, "回復內容");
        Add(estimate.FavoriteRevisions, "收藏版本");
        return "最多清除 " + string.Join("、", parts) + "；受保護的版本與共用內容會留著。";
    }

    private void UpdateSubmit()
    {
        var total = _latest?.Total ?? 0;
        _submit.IsEnabled = total > 0;
        _submit.Content = total > 0 ? "清除 " + SqlMemoryUsageSummary.Count(total) + " 筆" : "清除";
    }

    private void Submit()
    {
        if (_latest is not { Total: > 0 } estimate || Compose() is not { } request) return;
        if (!SqlAssistConfirmationWindow.Confirm(this, "清除 SQL Memory 紀錄",
            "清除最多 " + SqlMemoryUsageSummary.Count(estimate.Total) + " 筆紀錄？",
            Describe(estimate) + " 清除後無法復原；需要保留可以先回用量頁備份。", "清除")) return;
        Request = request;
        DialogResult = true;
    }

    private void Report(string message) => _status.Text = message;

    private static CheckBox Check(string label, string tip)
    {
        var box = new CheckBox
        {
            Content = label, ToolTip = tip, Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center,
            Template = SqlAssistChrome.CreateCheckBoxTemplate(), FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = SqlAssistChrome.DefaultMetrics.Body
        };
        box.SetResourceReference(ForegroundProperty, ThemeBrush.ListForeground);
        return box;
    }
}
