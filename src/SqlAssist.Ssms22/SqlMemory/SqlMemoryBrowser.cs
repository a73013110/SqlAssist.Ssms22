using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// SQL Memory 工具窗的畫面與繫結。篩選轉請求、分頁世代、頁尾狀態與選取還原在 <see cref="SqlMemoryBrowserModel"/>；
/// 這裡只把控制項的值交給模型、把回應交回模型決定要不要採用。列操作一律交給 <see cref="SqlMemoryItemCommands"/>。
/// </summary>
internal sealed class SqlMemoryBrowser : UserControl, IDisposable
{
    /// <summary>新列的進場旗標保留多久；比進場動畫長一點，之後捲動重用容器不會重播。</summary>
    private static readonly TimeSpan NewRowSettle = TimeSpan.FromMilliseconds(400);

    private readonly SqlAssistPackage _package;
    private readonly SqlMemoryBrowserModel _model = new();
    private readonly SqlMemoryItemCommands _commands;
    private readonly ObservableCollection<SqlMemoryRow> _rows = new();
    private readonly SqlMemoryList _list = new();
    private readonly TabControl _tabs = new();
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SqlConnectionFilter _server = new("伺服器");
    private readonly SqlConnectionFilter _database = new("資料庫", SqlIcon.Database);
    private readonly SqlPillSelector _kind = Pills(SqlMemoryBrowserModel.KindOptions);
    private readonly SqlPillSelector _period = Pills(SqlMemoryBrowserModel.PeriodOptions);
    private readonly SqlPillSelector _scope = Pills(SqlMemoryBrowserModel.ScopeOptions);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _hostStatus = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly SqlMemoryPager _pager = new();
    private readonly SqlLoadingSurface _loading;
    private readonly Button _connection;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _settleTimer;
    private readonly SqlMemoryPreview _detail;
    private readonly SqlMemorySplitView _splitView;
    private readonly FrameworkElement _historyFilters;
    private CancellationTokenSource _facets = new();
    private CancellationTokenSource _request = new();
    private bool _batchFilters;
    private bool _ready;
    private bool _disposed;

    public SqlMemoryBrowser(SqlAssistPackage package)
    {
        _package = package;
        _commands = new SqlMemoryItemCommands(package);
        _commands.Removed += row => SqlAssistPlatformGuard.Run("移除 SQL Memory 列", () => RemoveRow(row));
        _commands.Replaced += (row, updated) => SqlAssistPlatformGuard.Run("更新 SQL Memory 列", () => ReplaceRow(row, updated));
        VsThemeBrushes.Apply(this);
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = SqlAssistChrome.DefaultMetrics.Body;
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        SetResourceReference(ForegroundProperty, ThemeBrush.WindowForeground);
        MinWidth = 300;
        var root = new DockPanel { Margin = new Thickness(8) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        foreach (var name in new[] { "History", "Favorites" })
            _tabs.Items.Add(SqlAssistChrome.CreateMemoryTab(name == "History" ? SqlIcon.History : SqlIcon.Favorite, name));
        _tabs.SelectedIndex = 0;
        _connection = SqlAssistChrome.CreateMemoryConnectionButton();
        _connection.Click += (_, _) => SqlMemoryActions.Run(UseCurrentConnection, Report);
        header.Children.Add(SqlAssistChrome.CreateMemoryToolbar(_tabs, _connection, Button("重新整理", Refresh),
            Button("設定", () => SqlMemoryActions.OpenSettings(_package))));
        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋");
        clear.Click += (_, _) => SqlMemoryActions.Run(() => { _search.Clear(); _search.Focus(); }, Report);
        _search.ToolTip = "區分大小寫的字面搜尋；歷史搜尋 SQL，收藏搜尋名稱、說明與 SQL。";
        System.Windows.Automation.AutomationProperties.SetName(_search, "搜尋 SQL 或收藏");
        header.Children.Add(SqlAssistChrome.CreateSearchBar(_search, clear));
        var filters = new StackPanel();
        Select(_period, SqlMemoryBrowserModel.PeriodOptions, _model.Period);
        _historyFilters = SqlAssistChrome.CreateMemoryHistoryFilters(_kind, _period);
        filters.Children.Add(_historyFilters); filters.Children.Add(_scope);
        _scope.Visibility = Visibility.Collapsed;
        header.Children.Add(filters);
        header.Children.Add(_server); header.Children.Add(_database);
        VsThemeBrushes.Apply(_server.SortMenu); VsThemeBrushes.Apply(_database.SortMenu);
        _hostStatus.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_hostStatus);

        _status.TextWrapping = TextWrapping.Wrap; _status.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);

        _list.SetRowsSource(_rows, _pager);
        _list.LoadMoreRequested += (_, _) => Load();
        _pager.LoadMoreRequested += (_, _) => SqlMemoryActions.Run(Load, Report);
        _loading = new SqlLoadingSurface(_list);
        _detail = new SqlMemoryPreview(_commands, Report);
        _splitView = new SqlMemorySplitView(_loading, _detail, _detail.Summary);
        _splitView.DetailExpandedChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL 預覽", UpdatePreview);
        root.Children.Add(_splitView); Content = root;
        _list.ContextMenu = CreateContextMenu();
        _list.RowActionRequested += action => SqlMemoryActions.Run(() => RunCommand(action), Report);
        _list.OpenRequested += (_, _) => SqlMemoryActions.Run(() => RunCommand(SqlMemoryRowAction.Open), Report);
        _list.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Memory 選取", () =>
        {
            UpdateActions();
            UpdatePreview();
        });
        PreviewKeyDown += (_, e) => SqlMemoryActions.Run(() =>
        {
            if (e.Key == Key.F && e.KeyboardDevice.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
        }, Report);
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Load(); };
        // 只刷新相對時間；宿主狀態由事件推過來，不輪詢。
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _clockTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("更新 SQL Memory 時間", () => { foreach (var row in _rows) row.RefreshTime(); });
        _settleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = NewRowSettle };
        _settleTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("結束 SQL Memory 進場", () =>
        {
            _settleTimer.Stop();
            foreach (var row in _rows) row.IsNew = false;
        });
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _tabs)) return;
            _model.Tab = _tabs.SelectedIndex == 1 ? SqlMemoryBrowserTab.Favorites : SqlMemoryBrowserTab.History;
            Changed(); ReloadFacets();
        };
        _kind.SelectionChanged += (_, _) => { _model.Kind = SqlMemoryBrowserModel.KindOptions[_kind.SelectedIndex].Value; Changed(); };
        _period.SelectionChanged += (_, _) => { _model.Period = SqlMemoryBrowserModel.PeriodOptions[_period.SelectedIndex].Value; Changed(); };
        _scope.SelectionChanged += (_, _) =>
        {
            _model.Scope = SqlMemoryBrowserModel.ScopeOptions[_scope.SelectedIndex].Value;
            Changed(); ReloadFacets();
        };
        _search.TextChanged += (_, _) => { clear.IsEnabled = _search.Text.Length > 0; _model.Search = _search.Text; Changed(); };
        clear.IsEnabled = false;
        _server.SelectionChanged += (_, _) =>
        {
            if (_batchFilters) return;
            _model.Server = _server.Value;
            _model.Database = null; _database.Value = null;
            ReloadFacets(false); Changed();
        };
        _database.SelectionChanged += (_, _) => { if (_batchFilters) return; _model.Database = _database.Value; Changed(); };
        _server.OptionsRequested += (_, _) => LoadFacets(_server, false);
        _database.OptionsRequested += (_, _) => LoadFacets(_database, true);
        IsVisibleChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Memory 可見度", () =>
        {
            if (IsVisible) { _clockTimer.Start(); foreach (var row in _rows) row.RefreshTime(); ObserveHost(forceReload: !_model.IsLoading); }
            else { _clockTimer.Stop(); _facets.Cancel(); Invalidate(); _detail.Select(null); }
        });
        SqlMemoryHost.Runtime.StatusChanged += OnRuntimeStatusChanged;
        _ready = true;
        ObserveHost(forceReload: false);
    }

    public void ShowPage(bool favorites)
    {
        _tabs.SelectedIndex = favorites ? 1 : 0;
        _search.Focus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SqlMemoryHost.Runtime.StatusChanged -= OnRuntimeStatusChanged;
        _clockTimer.Stop(); _searchTimer.Stop(); _settleTimer.Stop();
        _request.Cancel(); _request.Dispose(); _facets.Cancel(); _facets.Dispose(); _detail.Dispose();
    }

    private static SqlPillSelector Pills<T>(IReadOnlyList<SqlMemoryOption<T>> options) where T : struct =>
        new(options.Select(option => (option.Label, SqlAssistChrome.MemoryOptionIcon(option.Value))).ToArray());

    private static void Select<T>(SqlPillSelector selector, IReadOnlyList<SqlMemoryOption<T>> options, T value)
    {
        for (var i = 0; i < options.Count; i++)
            if (EqualityComparer<T>.Default.Equals(options[i].Value, value)) { selector.SelectedIndex = i; return; }
    }

    /// <summary>快捷選單與卡片共用同一份操作清單；不適用於目前列的項目收起，而不是停用佔位。</summary>
    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        var entries = new List<(MenuItem Item, SqlMemoryRowCommand Command)>();
        foreach (var command in SqlMemoryRowCommand.All)
        {
            if (command.IsSeparated) menu.Items.Add(new Separator());
            var item = new MenuItem { Header = command.Label, Icon = SqlAssistChrome.CreateIcon(command.Icon) };
            item.Click += (_, _) => SqlMemoryActions.Run(() => RunCommand(command.Action), Report);
            menu.Items.Add(item); entries.Add((item, command));
        }
        VsThemeBrushes.Apply(menu);
        menu.Opened += (_, _) => SqlAssistPlatformGuard.Run("更新 SQL Memory 快捷選單", () =>
        {
            var row = _list.SelectedItem as SqlMemoryRow;
            foreach (var (item, command) in entries)
            {
                item.Visibility = row is not null && command.AppliesTo(row.IsFavorite) ? Visibility.Visible : Visibility.Collapsed;
                item.IsEnabled = SqlMemoryItemCommands.CanRun(command.Action, row);
                if (command.Action == SqlMemoryRowAction.Delete) item.Header = row?.DeleteLabel ?? command.Label;
            }
        });
        return menu;
    }

    private void RunCommand(SqlMemoryRowAction action)
    {
        if (!_model.IsAvailable || _list.SelectedItem is not SqlMemoryRow row) return;
        _ = SqlMemoryActions.RunAsync(() => _commands.RunAsync(action, row, this, Report, _request.Token), Report);
    }

    /// <summary>宿主狀態可能在背景執行緒改變；排回 UI 執行緒再比對。</summary>
    private void OnRuntimeStatusChanged(object? sender, SqlMemoryRuntimeStatus status) =>
        SqlAssistPlatformGuard.Probe("排入 SQL Memory 狀態更新", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                SqlAssistPlatformGuard.Run("更新 SQL Memory 狀態", () => ObserveHost(forceReload: false)))));

    private void ObserveHost(bool forceReload)
    {
        if (_disposed) return;
        var runtime = SqlMemoryHost.Runtime;
        var status = runtime.Status;
        _hostStatus.Text = status.Message;
        _hostStatus.Visibility = string.IsNullOrEmpty(_hostStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (_model.ObserveHost(runtime.IsAvailable, status.Generation))
        {
            // 清單、facets 與預覽都屬於舊儲存；換世代就整份作廢。
            _facets.Cancel(); _server.ResetOptions(); _database.ResetOptions();
            Invalidate();
            if (_model.IsAvailable) { Load(); ReloadFacets(); }
            else { _detail.Select(null); Report("SQL Memory 尚未就緒；可由設定啟用或重新啟用。"); }
        }
        else if (forceReload && _model.IsAvailable && IsVisible) Refresh();
        UpdateActions();
    }

    private void UseCurrentConnection()
    {
        var failure = _model.UseConnection(SqlWindowConnections.ReadActive(_package));
        if (failure is not null) { Report(failure); return; }
        // 一次更新兩個條件，不能在 Server 事件裡把剛指定的 Database 清掉。
        _batchFilters = true;
        try
        {
            Select(_scope, SqlMemoryBrowserModel.ScopeOptions, _model.Scope);
            _server.Value = _model.Server; _database.Value = _model.Database;
        }
        finally { _batchFilters = false; }
        Changed(); ReloadFacets();
    }

    private void ReloadFacets(bool servers = true)
    {
        if (!_ready || _disposed || _batchFilters) return;
        if (servers)
        {
            _facets.Cancel(); _facets.Dispose(); _facets = new CancellationTokenSource();
            _server.ResetOptions(); LoadFacets(_server, false);
        }
        _database.ResetOptions(); LoadFacets(_database, true);
    }

    private void LoadFacets(SqlConnectionFilter filter, bool databases)
    {
        if (!_model.IsAvailable || _disposed || !IsVisible) return;
        var requestId = _model.BeginFacet(databases);
        var host = _model.HostGeneration;
        var token = _facets.Token;
        var request = _model.FacetRequest(databases, filter.Sort, filter.Offset);
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            try
            {
                var names = await SqlMemoryHost.Runtime.ReadConnectionFacetsAsync(request, token);
                if (!_disposed && !token.IsCancellationRequested && _model.IsCurrentFacet(databases, requestId, host))
                    filter.SetOptions(names);
            }
            catch (Exception error)
            {
                // 名稱載入同樣有世代檢查，舊範圍的失敗不能蓋掉新頁面。
                if (!_disposed && !token.IsCancellationRequested && _model.IsCurrentFacet(databases, requestId, host))
                    Report(SqlMemoryTimeText.Failure("連線篩選載入", error));
            }
        }, Report);
    }

    private void Changed()
    {
        if (!_ready || _disposed || _batchFilters) return;
        SqlMemoryActions.Run(() =>
        {
            _scope.Visibility = _model.IsFavorites ? Visibility.Visible : Visibility.Collapsed;
            _historyFilters.Visibility = _model.IsFavorites ? Visibility.Collapsed : Visibility.Visible;
            _server.Visibility = _model.ShowsServerFilter ? Visibility.Visible : Visibility.Collapsed;
            _database.Visibility = _model.ShowsDatabaseFilter ? Visibility.Visible : Visibility.Collapsed;
            _server.IsEnabled = _model.ShowsServerFilter;
            _database.IsEnabled = _model.ShowsDatabaseFilter;
            _server.EmptyLabel = _database.EmptyLabel = _model.EmptyConnectionLabel;
            Invalidate();
            _searchTimer.Start();
        }, Report);
    }

    private void Invalidate()
    {
        _searchTimer.Stop(); _settleTimer.Stop();
        _request.Cancel(); _request.Dispose(); _request = new CancellationTokenSource();
        _model.Invalidate(DateTimeOffset.Now);
        Report("");
        _rows.Clear(); _detail.Select(null); UpdateActions();
    }

    private void Refresh()
    {
        _model.RememberSelection((_list.SelectedItem as SqlMemoryRow)?.Id);
        Invalidate(); Load(); ReloadFacets();
    }

    private void Load() => _ = SqlMemoryActions.RunAsync(LoadAsync, Report);

    private async Task LoadAsync()
    {
        if (_disposed || !IsVisible || _model.BeginLoad() is not { } load) return;
        if (load.BlockedMessage is not null) { Report(load.BlockedMessage); return; }
        var token = _request.Token;
        Report(""); UpdateActions();
        try
        {
            SqlMemoryRow[] rows;
            bool accepted;
            if (load.Favorites is { } favorites)
            {
                var page = await SqlMemoryHost.Runtime.ReadFavoritesAsync(favorites, token);
                rows = page.Items.Select(item => new SqlMemoryRow(item)).ToArray();
                accepted = !token.IsCancellationRequested && _model.Accept(load, page);
            }
            else
            {
                var page = await SqlMemoryHost.Runtime.ReadHistoryAsync(load.History!, token);
                rows = page.Items.Select(item => new SqlMemoryRow(item)).ToArray();
                accepted = !token.IsCancellationRequested && _model.Accept(load, page);
            }
            if (!accepted) return;
            var motion = SqlAssistChrome.MotionEnabled;
            foreach (var row in rows) { row.IsNew = motion; _rows.Add(row); }
            if (motion && rows.Length > 0) { _settleTimer.Stop(); _settleTimer.Start(); }
            if (_model.ResolveSelection(_rows.Select(row => row.Id).ToArray(), _list.SelectedItem is not null) is { } index)
                _list.SelectedIndex = index;
        }
        catch (Exception error)
        {
            // 回應失敗只更新同一世代；舊查詢不得蓋掉新的狀態訊息。
            if (!token.IsCancellationRequested && _model.IsCurrent(load)) Report(SqlMemoryTimeText.Failure("載入", error));
        }
        finally { _model.End(load); UpdateActions(); }
    }

    /// <summary>刪除成功：先讓卡片淡出，再真正移出集合；選取留在原位置（原本的下一列），鍵盤焦點跟著走。</summary>
    private void RemoveRow(SqlMemoryRow row)
    {
        if (_disposed || row.IsRemoving || !_rows.Contains(row)) return;
        row.IsRemoving = true;
        if (!SqlAssistChrome.MotionEnabled) { CompleteRemoval(row); return; }
        var exit = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SqlAssistChrome.MemoryCardExitDuration };
        exit.Tick += (_, _) =>
        {
            exit.Stop();
            SqlAssistPlatformGuard.Run("移除 SQL Memory 列", () => CompleteRemoval(row));
        };
        exit.Start();
    }

    private void CompleteRemoval(SqlMemoryRow row)
    {
        // 淡出期間換了篩選或重新整理：這一列已經不在新清單裡，不能照舊索引刪掉別人。
        var index = _rows.IndexOf(row);
        if (_disposed || index < 0) return;
        var selected = ReferenceEquals(_list.SelectedItem, row);
        var focused = _list.IsKeyboardFocusWithin;
        _rows.RemoveAt(index);
        if (selected && SqlMemoryBrowserModel.SelectionAfterRemoval(index, _rows.Count) is { } next)
        {
            _list.SelectedIndex = next;
            if (focused) (_list.ItemContainerGenerator.ContainerFromIndex(next) as ListBoxItem)?.Focus();
        }
        UpdateActions();
    }

    /// <summary>收藏更新後就地換列，保留已載入的頁與捲動位置；改到別的範圍或已不存在則移出清單。</summary>
    private void ReplaceRow(SqlMemoryRow row, SqlMemoryRow? updated)
    {
        var index = _rows.IndexOf(row);
        if (_disposed || index < 0) return;
        if (updated?.Favorite is not { } favorite || !_model.MatchesFavoriteScope(favorite.Favorite)) { RemoveRow(row); return; }
        var selected = ReferenceEquals(_list.SelectedItem, row);
        _rows[index] = updated;
        if (selected) _list.SelectedIndex = index;
    }

    private void UpdatePreview() =>
        _detail.Select(_model.IsAvailable && IsVisible ? _list.SelectedItem as SqlMemoryRow : null, _splitView.IsDetailExpanded);

    private void UpdateActions()
    {
        _pager.Update(_model.Footer(_rows.Count));
        // 第一頁用表面載入圖示；續頁的進度在頁尾原地，不遮住已經載入的列。
        _loading.IsLoading = _model.IsLoading && _rows.Count == 0;
        _list.CanAutoLoadMore = _model.CanAutoLoadMore;
        _connection.IsEnabled = _model.IsAvailable;
    }

    private void Report(string message)
    {
        if (_disposed) return;
        _status.Text = message; _status.ToolTip = message;
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Button Button(string label, Action action)
    {
        var button = SqlAssistChrome.CreateButton(label, SqlAssistChrome.DefaultMetrics);
        button.Click += (_, _) => SqlMemoryActions.Run(action, Report);
        return button;
    }
}
