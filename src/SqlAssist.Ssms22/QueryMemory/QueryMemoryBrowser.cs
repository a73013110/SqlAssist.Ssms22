using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.QueryMemory;

internal sealed class QueryMemoryBrowser : UserControl, IDisposable
{
    private readonly SqlAssistPackage _package;
    private readonly QueryMemoryBrowserState<QueryMemoryRow> _state = new();
    private readonly ObservableCollection<QueryMemoryRow> _rows = new();
    private readonly ListBox _list = new();
    private readonly TabControl _tabs = new();
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _server = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _database = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly ComboBox _kind = Combo("全部類型", "執行", "草稿");
    private readonly ComboBox _period = Combo("今天", "最近 7 天", "最近 30 天", "全部期間");
    private readonly ComboBox _scope = Combo("全域", "指定伺服器", "指定資料庫");
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _hostStatus = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _count = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly Button _more;
    private readonly Button _previewButton;
    private readonly Button _openButton;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _hostTimer;
    private CancellationTokenSource _request = new();
    private QueryMemoryPreviewWindow? _preview;
    private bool _ready;
    private bool _available;
    private bool _disposed;
    private DateTimeOffset? _since;
    private Guid? _restoreId;
    private long _hostGeneration;
    private bool _opening;

    public QueryMemoryBrowser(SqlAssistPackage package)
    {
        _package = package;
        VsThemeBrushes.Apply(this);
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = SqlAssistChrome.DefaultMetrics.Body;
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        SetResourceReference(ForegroundProperty, ThemeBrush.WindowForeground);
        MinWidth = 280;
        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var toolbar = new DockPanel();
        var utilities = new StackPanel { Orientation = Orientation.Horizontal };
        utilities.Children.Add(Button("重新整理", Refresh));
        utilities.Children.Add(Button("設定", () => QueryMemoryActions.OpenSettings(_package)));
        DockPanel.SetDock(utilities, Dock.Right); toolbar.Children.Add(utilities);
        _tabs.Template = SqlAssistChrome.CreateTabControlTemplate();
        foreach (var name in new[] { "歷史", "收藏" })
            _tabs.Items.Add(new TabItem { Header = name, Template = SqlAssistChrome.CreateTabItemTemplate() });
        _tabs.SelectedIndex = 0;
        toolbar.Children.Add(_tabs); header.Children.Add(toolbar);
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = Button("清除", () => _search.Clear());
        DockPanel.SetDock(clear, Dock.Right); searchRow.Children.Add(clear);
        _search.ToolTip = "區分大小寫的字面搜尋；歷史搜尋 SQL，收藏搜尋名稱、說明與 SQL。";
        System.Windows.Automation.AutomationProperties.SetName(_search, "搜尋 SQL 或收藏");
        searchRow.Children.Add(_search); header.Children.Add(searchRow);
        header.Children.Add(SqlAssistChrome.CreateHint("搜尋 SQL／收藏 · 區分大小寫的字面搜尋", SqlAssistChrome.DefaultMetrics));
        var filters = new WrapPanel();
        _period.SelectedIndex = 1;
        foreach (var control in new Control[] { _kind, _period, _scope })
        {
            control.Margin = new Thickness(0, 0, 8, 8);
            filters.Children.Add(control);
        }
        _scope.Visibility = Visibility.Collapsed;
        header.Children.Add(filters);
        var connection = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        connection.ColumnDefinitions.Add(new ColumnDefinition());
        connection.ColumnDefinitions.Add(new ColumnDefinition());
        var serverField = Field("伺服器（精確名稱）", _server);
        serverField.Margin = new Thickness(0, 0, 8, 0);
        connection.Children.Add(serverField);
        var databaseField = Field("資料庫（精確名稱）", _database);
        Grid.SetColumn(databaseField, 1); connection.Children.Add(databaseField);
        header.Children.Add(connection);
        _hostStatus.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_hostStatus);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var pagination = new DockPanel();
        _more = Button("載入更多", () => Load());
        DockPanel.SetDock(_more, Dock.Right); pagination.Children.Add(_more); pagination.Children.Add(_count);
        footer.Children.Add(pagination);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _previewButton = Button("預覽", ShowPreview);
        _openButton = Button("開啟至新查詢", OpenSelected, true);
        _openButton.ToolTip = "沿用目前 SSMS 連線；不依歷史切換連線，也不執行 SQL。";
        actions.Children.Add(_previewButton); actions.Children.Add(_openButton);
        footer.Children.Add(actions);
        _status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_status);

        _list.ItemsSource = _rows;
        _list.ItemContainerStyle = SqlAssistChrome.CreateListItemStyle(SqlAssistChrome.DefaultMetrics);
        _list.ItemTemplate = SqlAssistChrome.CreateSqlSummaryTemplate();
        _list.BorderThickness = new Thickness(0);
        _list.SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(_list, true);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        root.Children.Add(_list); Content = root;
        var menu = new ContextMenu();
        foreach (var pair in new[] { ("預覽", (Action)ShowPreview), ("開啟至新查詢", (Action)OpenSelected) })
        {
            var item = new MenuItem { Header = pair.Item1 };
            item.Click += (_, _) => QueryMemoryActions.Run(pair.Item2, Report);
            menu.Items.Add(item);
        }
        _list.ContextMenu = menu;
        _list.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換查詢記憶選取", () =>
        {
            UpdateActions();
            if (_preview is not null) _preview.Select(_list.SelectedItem as QueryMemoryRow);
        });
        _list.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) is ListBoxItem)
            { e.Handled = true; ShowPreview(); }
        };
        PreviewKeyDown += (_, e) => QueryMemoryActions.Run(() =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && _list.IsKeyboardFocusWithin) { ShowPreview(); e.Handled = true; }
        }, Report);
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Load(); };
        _hostTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _hostTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("更新查詢記憶狀態", CheckHost);
        _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, _tabs)) Changed(); };
        foreach (var combo in new[] { _kind, _period, _scope }) combo.SelectionChanged += (_, _) => Changed();
        foreach (var box in new[] { _search, _server, _database }) box.TextChanged += (_, _) => Changed();
        IsVisibleChanged += (_, _) => SqlAssistPlatformGuard.Run("切換查詢記憶可見度", () =>
        {
            if (IsVisible) { _hostTimer.Start(); CheckHost(); if (_available && !_state.Loading) Refresh(); }
            else { _hostTimer.Stop(); Invalidate(); _preview?.Close(); }
        });
        _ready = true;
        UpdateActions();
    }

    private bool Saved => _tabs.SelectedIndex == 1;
    private void Changed()
    {
        if (!_ready || _disposed) return;
        QueryMemoryActions.Run(() =>
        {
            _scope.Visibility = Saved ? Visibility.Visible : Visibility.Collapsed;
            _kind.Visibility = _period.Visibility = Saved ? Visibility.Collapsed : Visibility.Visible;
            _server.IsEnabled = !Saved || _scope.SelectedIndex > 0;
            _database.IsEnabled = !Saved || _scope.SelectedIndex == 2;
            Invalidate();
            _searchTimer.Start();
        }, Report);
    }

    private void Invalidate()
    {
        _searchTimer.Stop();
        _request.Cancel(); _request.Dispose(); _request = new CancellationTokenSource();
        _state.Reset(); _rows.Clear(); _preview?.Select(null); UpdateActions();
        // 時間也是游標指紋的一部分；同一次分頁不能每頁重新計算「最近七天」。
        _since = _period.SelectedIndex switch
        {
            0 => new DateTimeOffset(DateTime.Today), 1 => DateTimeOffset.Now.AddDays(-7),
            2 => DateTimeOffset.Now.AddDays(-30), _ => null
        };
    }

    private void CheckHost()
    {
        var settings = SqlAssistSettingsStore.Current;
        var available = settings.Enabled && settings.QueryMemoryEnabled && QueryMemoryHost.IsCapturing;
        _hostStatus.Text = QueryMemoryHost.Status;
        _hostStatus.Visibility = string.IsNullOrEmpty(_hostStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (_available != available || _hostGeneration != QueryMemoryHost.Generation)
        {
            _hostGeneration = QueryMemoryHost.Generation;
            _available = available;
            Invalidate();
            if (available) Load();
            else { _preview?.Close(); Report("查詢記憶尚未就緒；可由設定啟用或重新啟用。"); }
        }
        else if (available && _rows.Count == 0 && !_state.Loading && _state.Generation == 0) Refresh();
        UpdateActions();
    }

    private void Refresh()
    {
        _restoreId = (_list.SelectedItem as QueryMemoryRow)?.Id;
        Invalidate(); Load();
    }
    private void Load() => _ = QueryMemoryActions.RunAsync(LoadAsync, Report);
    private async Task LoadAsync()
    {
        if (!_available || _disposed || !IsVisible) return;
        var generation = _state.Generation;
        if (!_state.Begin(generation)) return;
        var token = _request.Token;
        var hostGeneration = QueryMemoryHost.Generation;
        Report("正在載入…"); UpdateActions();
        try
        {
            var search = _search.Text;
            var server = string.IsNullOrEmpty(_server.Text) ? null : _server.Text;
            var database = string.IsNullOrEmpty(_database.Text) ? null : _database.Text;
            QueryMemoryRow[] rows;
            string? cursor;
            if (Saved)
            {
                var scope = (SavedQueryScope)_scope.SelectedIndex;
                var request = new SavedQueryRequest(50, scope, scope == SavedQueryScope.Global ? null : server,
                    scope == SavedQueryScope.Database ? database : null, search, _state.Cursor);
                var page = await QueryMemoryHost.ReadSavedQueriesAsync(request, token);
                rows = page.Items.Select(item => new QueryMemoryRow(item)).ToArray(); cursor = page.NextCursor;
            }
            else
            {
                var request = new QueryHistoryRequest(50, (QueryHistoryKind)_kind.SelectedIndex, search,
                    server, database, _since, cursor: _state.Cursor);
                var page = await QueryMemoryHost.ReadHistoryAsync(request, token);
                rows = page.Items.Select(item => new QueryMemoryRow(item)).ToArray(); cursor = page.NextCursor;
            }
            if (token.IsCancellationRequested || !QueryMemoryHost.IsAvailable || hostGeneration != QueryMemoryHost.Generation ||
                !_state.Accept(generation, rows, cursor)) return;
            foreach (var row in rows) _rows.Add(row);
            if (_restoreId is { } restore && _rows.FirstOrDefault(row => row.Id == restore) is { } selected)
            { _list.SelectedItem = selected; _restoreId = null; }
            Report(_rows.Count == 0 ? "沒有符合條件的項目。可清除搜尋或放寬期間與範圍。" : "");
        }
        catch (Exception error)
        {
            // 回應失敗只更新同一世代；舊查詢不得蓋掉新的狀態訊息。
            if (generation == _state.Generation && !token.IsCancellationRequested) Report("載入失敗：" + error.Message);
        }
        finally { _state.Fail(generation); UpdateActions(); }
    }

    private void ShowPreview() => QueryMemoryActions.Run(() =>
    {
        if (!_available || _list.SelectedItem is not QueryMemoryRow row) return;
        if (_preview is null)
        {
            _preview = new QueryMemoryPreviewWindow(_package, Refresh);
            _preview.Closed += (_, _) => _preview = null;
            _preview.Select(row); _preview.Show();
        }
        else { _preview.Select(row); _preview.Activate(); }
    }, Report);

    private void OpenSelected()
    {
        if (_opening || _list.SelectedItem is not QueryMemoryRow row) return;
        var token = _request.Token;
        var generation = QueryMemoryHost.Generation;
        _opening = true; UpdateActions();
        _ = QueryMemoryActions.RunAsync(async () =>
        {
            try
            {
                var content = await QueryMemoryHost.ReadContentAsync(row.ContentId, token);
                token.ThrowIfCancellationRequested();
                if (!QueryMemoryHost.IsAvailable || generation != QueryMemoryHost.Generation) return;
                if (content is null) throw new InvalidOperationException("內容已不存在，請重新整理。");
                QueryMemoryActions.OpenQuery(_package, content.SqlText);
                Report("已開啟新查詢；未執行 SQL。");
            }
            finally { _opening = false; if (!_disposed) UpdateActions(); }
        }, Report);
    }

    private void UpdateActions()
    {
        _more.IsEnabled = _available && !_state.Loading && _state.Cursor is not null;
        _previewButton.IsEnabled = _openButton.IsEnabled = _available && _list.SelectedItem is not null;
        if (_opening) _openButton.IsEnabled = false;
        _count.Text = $"已載入 {_rows.Count} 筆";
    }
    private void Report(string message) { _status.Text = message; _status.ToolTip = message; }
    private Button Button(string label, Action action, bool primary = false)
    {
        var button = SqlAssistChrome.CreateButton(label, SqlAssistChrome.DefaultMetrics, primary);
        button.Click += (_, _) => QueryMemoryActions.Run(action, Report);
        return button;
    }
    internal static ComboBox Combo(params string[] labels)
    {
        var combo = SqlAssistChrome.CreateComboBox(SqlAssistChrome.DefaultMetrics);
        foreach (var label in labels) combo.Items.Add(label);
        combo.SelectedIndex = 0; return combo;
    }
    internal static StackPanel Field(string label, Control control)
    {
        var panel = new StackPanel();
        panel.Children.Add(SqlAssistChrome.CreateLabel(label, SqlAssistChrome.DefaultMetrics));
        control.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        return panel;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _hostTimer.Stop(); _searchTimer.Stop();
        _request.Cancel(); _request.Dispose(); _preview?.Close();
    }
}
