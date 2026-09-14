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
using SqlAssist.Core.QueryMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.QueryMemory;

/// <summary>
/// SQL Memory 工具窗的畫面與繫結。篩選轉請求、分頁世代與選取還原在 <see cref="QueryMemoryBrowserModel"/>；
/// 這裡只把控制項的值交給模型、把回應交回模型決定要不要採用。
/// </summary>
internal sealed class QueryMemoryBrowser : UserControl, IDisposable
{
    private readonly SqlAssistPackage _package;
    private readonly QueryMemoryBrowserModel _model = new();
    private readonly ObservableCollection<QueryMemoryRow> _rows = new();
    private readonly SqlMemoryList _list = new();
    private readonly TabControl _tabs = new();
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SqlConnectionFilter _server = new("伺服器");
    private readonly SqlConnectionFilter _database = new("資料庫");
    private readonly SqlPillSelector _kind = Pills(QueryMemoryBrowserModel.KindOptions);
    private readonly SqlPillSelector _period = Pills(QueryMemoryBrowserModel.PeriodOptions);
    private readonly SqlPillSelector _scope = Pills(QueryMemoryBrowserModel.ScopeOptions);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _hostStatus = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _count = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly Button _more;
    private readonly Button _connection;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly QueryMemoryDetail _detail;
    private readonly SqlMemorySplitView _splitView;
    private readonly FrameworkElement _historyFilters;
    private CancellationTokenSource _facets = new();
    private CancellationTokenSource _request = new();
    private bool _batchFilters;
    private bool _ready;
    private bool _disposed;
    private bool _opening;

    public QueryMemoryBrowser(SqlAssistPackage package)
    {
        _package = package;
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
            _tabs.Items.Add(new TabItem { Header = name, Template = SqlAssistChrome.CreateTabItemTemplate() });
        _tabs.SelectedIndex = 0;
        _connection = SqlAssistChrome.CreateQueryConnectionButton();
        _connection.Click += (_, _) => QueryMemoryActions.Run(UseCurrentConnection, Report);
        header.Children.Add(SqlAssistChrome.CreateQueryToolbar(_tabs, _connection, Button("重新整理", Refresh),
            Button("設定", () => QueryMemoryActions.OpenSettings(_package))));
        var clear = SqlAssistChrome.CreateQueryIconButton("Clear", "清除搜尋");
        clear.Click += (_, _) => QueryMemoryActions.Run(() => { _search.Clear(); _search.Focus(); }, Report);
        _search.ToolTip = "區分大小寫的字面搜尋；歷史搜尋 SQL，收藏搜尋名稱、說明與 SQL。";
        System.Windows.Automation.AutomationProperties.SetName(_search, "搜尋 SQL 或收藏");
        header.Children.Add(SqlAssistChrome.CreateSearchBar(_search, clear));
        var filters = new StackPanel();
        Select(_period, QueryMemoryBrowserModel.PeriodOptions, _model.Period);
        _historyFilters = SqlAssistChrome.CreateQueryHistoryFilters(_kind, _period);
        filters.Children.Add(_historyFilters); filters.Children.Add(_scope);
        _scope.Visibility = Visibility.Collapsed;
        header.Children.Add(filters);
        header.Children.Add(_server); header.Children.Add(_database);
        VsThemeBrushes.Apply(_server.SortMenu); VsThemeBrushes.Apply(_database.SortMenu);
        _hostStatus.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_hostStatus);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var pagination = new DockPanel();
        _more = Button("載入更多", Load);
        DockPanel.SetDock(_more, Dock.Right); pagination.Children.Add(_more); pagination.Children.Add(_count);
        footer.Children.Add(pagination);
        _status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_status);

        _list.ItemsSource = _rows;
        _detail = new QueryMemoryDetail(package, Refresh);
        _splitView = new SqlMemorySplitView(_list, _detail, _detail.Summary);
        _splitView.DetailExpandedChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL 預覽", UpdatePreview);
        root.Children.Add(_splitView); Content = root;
        _list.ContextMenu = CreateContextMenu();
        _list.RowActionRequested += action => QueryMemoryActions.Run(() =>
        {
            if (!_model.IsAvailable) return;
            if (action == "Open") OpenSelected();
            else if (action == "Copy") CopySelected();
            else if (action == "Favorite") AddFavorite();
        }, Report);
        _list.OpenRequested += (_, _) => QueryMemoryActions.Run(OpenSelected, Report);
        _list.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換查詢記憶選取", () =>
        {
            UpdateActions();
            UpdatePreview();
        });
        PreviewKeyDown += (_, e) => QueryMemoryActions.Run(() =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
        }, Report);
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Load(); };
        // 只刷新相對時間；宿主狀態由事件推過來，不輪詢。
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _clockTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("更新查詢記憶時間", () => { foreach (var row in _rows) row.RefreshTime(); });
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _tabs)) return;
            _model.Tab = _tabs.SelectedIndex == 1 ? QueryMemoryBrowserTab.Favorites : QueryMemoryBrowserTab.History;
            Changed(); ReloadFacets();
        };
        _kind.SelectionChanged += (_, _) => { _model.Kind = QueryMemoryBrowserModel.KindOptions[_kind.SelectedIndex].Value; Changed(); };
        _period.SelectionChanged += (_, _) => { _model.Period = QueryMemoryBrowserModel.PeriodOptions[_period.SelectedIndex].Value; Changed(); };
        _scope.SelectionChanged += (_, _) =>
        {
            _model.Scope = QueryMemoryBrowserModel.ScopeOptions[_scope.SelectedIndex].Value;
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
        IsVisibleChanged += (_, _) => SqlAssistPlatformGuard.Run("切換查詢記憶可見度", () =>
        {
            if (IsVisible) { _clockTimer.Start(); foreach (var row in _rows) row.RefreshTime(); ObserveHost(forceReload: !_model.IsLoading); }
            else { _clockTimer.Stop(); _facets.Cancel(); Invalidate(); _detail.Select(null); }
        });
        QueryMemoryHost.Runtime.StatusChanged += OnRuntimeStatusChanged;
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
        QueryMemoryHost.Runtime.StatusChanged -= OnRuntimeStatusChanged;
        _clockTimer.Stop(); _searchTimer.Stop();
        _request.Cancel(); _request.Dispose(); _facets.Cancel(); _facets.Dispose(); _detail.Dispose();
    }

    private static SqlPillSelector Pills<T>(IReadOnlyList<QueryMemoryOption<T>> options) =>
        new(options.Select(option => option.Label).ToArray());

    private static void Select<T>(SqlPillSelector selector, IReadOnlyList<QueryMemoryOption<T>> options, T value)
    {
        for (var i = 0; i < options.Count; i++)
            if (EqualityComparer<T>.Default.Equals(options[i].Value, value)) { selector.SelectedIndex = i; return; }
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        foreach (var pair in new[] { ("複製 SQL", (Action)CopySelected), ("開啟至新查詢", (Action)OpenSelected),
            ("Add to Favorites", (Action)AddFavorite) })
        {
            var item = new MenuItem { Header = pair.Item1 };
            item.Click += (_, _) => QueryMemoryActions.Run(pair.Item2, Report);
            menu.Items.Add(item);
        }
        VsThemeBrushes.Apply(menu);
        menu.Opened += (_, _) => SqlAssistPlatformGuard.Run("更新 SQL Memory 快捷選單", () =>
        {
            var row = _list.SelectedItem as QueryMemoryRow;
            foreach (MenuItem item in menu.Items) item.IsEnabled = _model.IsAvailable && row is not null;
            var favorite = (MenuItem)menu.Items[2];
            favorite.IsEnabled = _model.IsAvailable && row?.CanAddFavorite == true;
            favorite.Visibility = row?.IsFavorite == true ? Visibility.Collapsed : Visibility.Visible;
            favorite.ToolTip = row?.AddFavoriteHint;
        });
        return menu;
    }

    /// <summary>宿主狀態可能在背景執行緒改變；排回 UI 執行緒再比對。</summary>
    private void OnRuntimeStatusChanged(object? sender, QueryMemoryRuntimeStatus status) =>
        SqlAssistPlatformGuard.Probe("排入查詢記憶狀態更新", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                SqlAssistPlatformGuard.Run("更新查詢記憶狀態", () => ObserveHost(forceReload: false)))));

    private void ObserveHost(bool forceReload)
    {
        if (_disposed) return;
        var runtime = QueryMemoryHost.Runtime;
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
        var failure = _model.UseConnection(QueryMemoryConnections.ReadActive(_package));
        if (failure is not null) { Report(failure); return; }
        // 一次更新兩個條件，不能在 Server 事件裡把剛指定的 Database 清掉。
        _batchFilters = true;
        try
        {
            Select(_scope, QueryMemoryBrowserModel.ScopeOptions, _model.Scope);
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
        _ = QueryMemoryActions.RunAsync(async () =>
        {
            try
            {
                var names = await QueryMemoryHost.Runtime.ReadConnectionFacetsAsync(request, token);
                if (!_disposed && !token.IsCancellationRequested && _model.IsCurrentFacet(databases, requestId, host))
                    filter.SetOptions(names);
            }
            catch (Exception error)
            {
                // 名稱載入同樣有世代檢查，舊範圍的失敗不能蓋掉新頁面。
                if (!_disposed && !token.IsCancellationRequested && _model.IsCurrentFacet(databases, requestId, host))
                    Report(QueryMemoryPresentation.Failure("連線篩選載入", error));
            }
        }, Report);
    }

    private void AddFavorite()
    {
        if (!_model.IsAvailable || _list.SelectedItem is not QueryMemoryRow { CanAddFavorite: true } row) return;
        var dialog = new FavoriteMetadataWindow(_package, row, false);
        if (dialog.ShowModal() == true) { Refresh(); Report("已加入收藏。"); }
    }

    private void Changed()
    {
        if (!_ready || _disposed || _batchFilters) return;
        QueryMemoryActions.Run(() =>
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
        _searchTimer.Stop();
        _request.Cancel(); _request.Dispose(); _request = new CancellationTokenSource();
        _model.Invalidate(DateTimeOffset.Now);
        _rows.Clear(); _detail.Select(null); UpdateActions();
    }

    private void Refresh()
    {
        _model.RememberSelection((_list.SelectedItem as QueryMemoryRow)?.Id);
        Invalidate(); Load(); ReloadFacets();
    }

    private void Load() => _ = QueryMemoryActions.RunAsync(LoadAsync, Report);

    private async Task LoadAsync()
    {
        if (_disposed || !IsVisible || _model.BeginLoad() is not { } load) return;
        if (load.BlockedMessage is not null) { Report(load.BlockedMessage); return; }
        var token = _request.Token;
        Report("正在載入…"); UpdateActions();
        try
        {
            QueryMemoryRow[] rows;
            bool accepted;
            if (load.Favorites is { } favorites)
            {
                var page = await QueryMemoryHost.Runtime.ReadFavoriteQueriesAsync(favorites, token);
                rows = page.Items.Select(item => new QueryMemoryRow(item)).ToArray();
                accepted = !token.IsCancellationRequested && _model.Accept(load, page);
            }
            else
            {
                var page = await QueryMemoryHost.Runtime.ReadHistoryAsync(load.History!, token);
                rows = page.Items.Select(item => new QueryMemoryRow(item)).ToArray();
                accepted = !token.IsCancellationRequested && _model.Accept(load, page);
            }
            if (!accepted) return;
            foreach (var row in rows) _rows.Add(row);
            if (_model.ResolveSelection(_rows.Select(row => row.Id).ToArray(), _list.SelectedItem is not null) is { } index)
                _list.SelectedIndex = index;
            Report(_model.PageMessage(_rows.Count));
        }
        catch (Exception error)
        {
            // 回應失敗只更新同一世代；舊查詢不得蓋掉新的狀態訊息。
            if (!token.IsCancellationRequested && _model.IsCurrent(load)) Report(QueryMemoryPresentation.Failure("載入", error));
        }
        finally { _model.End(load); UpdateActions(); }
    }

    private void UpdatePreview() =>
        _detail.Select(_model.IsAvailable && IsVisible ? _list.SelectedItem as QueryMemoryRow : null, _splitView.IsDetailExpanded);

    private void CopySelected() => ReadSelectedContent(false);

    private void OpenSelected() => ReadSelectedContent(true);

    private void ReadSelectedContent(bool open)
    {
        if (!_model.IsAvailable || _opening || _list.SelectedItem is not QueryMemoryRow row) return;
        var token = _request.Token;
        var host = _model.HostGeneration;
        _opening = true;
        _ = QueryMemoryActions.RunAsync(async () =>
        {
            try
            {
                var content = await QueryMemoryHost.Runtime.ReadContentAsync(row.ContentId, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || !_model.IsAvailable || host != _model.HostGeneration) return;
                if (content is null) throw new InvalidOperationException("內容已不存在，請重新整理。");
                if (open) QueryMemoryActions.OpenQuery(_package, content.SqlText);
                else Clipboard.SetText(content.SqlText);
                Report(open ? "已開啟新查詢；未執行 SQL。" : "已複製完整 SQL。");
            }
            catch (Exception error)
            {
                // 切頁後取消的全文讀取，不得再寫剪貼簿、開窗或覆蓋新頁面的訊息。
                if (!_disposed && !token.IsCancellationRequested && _model.IsAvailable && host == _model.HostGeneration)
                    Report(QueryMemoryPresentation.Failure(open ? "開啟" : "複製", error));
            }
            finally { _opening = false; }
        }, Report);
    }

    private void UpdateActions()
    {
        _more.IsEnabled = _model.CanLoadMore;
        _more.Content = _model.LoadMoreLabel;
        _connection.IsEnabled = _model.IsAvailable;
        _count.Text = $"已載入 {_rows.Count} 筆";
    }

    private void Report(string message) { if (!_disposed) { _status.Text = message; _status.ToolTip = message; } }

    private Button Button(string label, Action action)
    {
        var button = SqlAssistChrome.CreateButton(label, SqlAssistChrome.DefaultMetrics);
        button.Click += (_, _) => QueryMemoryActions.Run(action, Report);
        return button;
    }
}
