using System;
using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly QueryMemoryPageState _state = new();
    private readonly ObservableCollection<QueryMemoryRow> _rows = new();
    private readonly SqlMemoryList _list = new();
    private readonly TabControl _tabs = new();
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SqlConnectionFilter _server = new("伺服器");
    private readonly SqlConnectionFilter _database = new("資料庫");
    private readonly SqlPillSelector _kind = new("全部", "執行", "草稿");
    private readonly SqlPillSelector _period = new("今天", "7 天", "30 天", "不限");
    private readonly SqlPillSelector _scope = new("全域", "指定伺服器", "指定資料庫");
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _hostStatus = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _count = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly Button _more;
    private readonly Button _connection;
    private CancellationTokenSource _facets = new();
    private int _serverRequest;
    private int _databaseRequest;
    private bool _batchFilters;
    private DateTime _lastTimeRefresh;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _hostTimer;
    private CancellationTokenSource _request = new();
    private readonly QueryMemoryDetail _detail;
    private readonly SqlMemorySplitView _splitView;
    private readonly FrameworkElement _historyFilters;
    private bool _ready;
    private bool _available;
    private bool _disposed;
    private DateTimeOffset? _since;
    /// <summary>上一頁因搜尋預算提早結束時的進度說明；null 表示游標是一般的「載入更多」。</summary>
    private string? _searchProgress;
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
        _period.SelectedIndex = 1;
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
        _more = Button("載入更多", () => Load());
        DockPanel.SetDock(_more, Dock.Right); pagination.Children.Add(_more); pagination.Children.Add(_count);
        footer.Children.Add(pagination);
        _status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_status);

        _list.ItemsSource = _rows;
        _detail = new QueryMemoryDetail(package, Refresh);
        _splitView = new SqlMemorySplitView(_list, _detail, _detail.Summary);
        _splitView.DetailExpandedChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL 預覽", UpdatePreview);
        root.Children.Add(_splitView); Content = root;
        var menu = new ContextMenu();
        foreach (var pair in new[] { ("複製 SQL", (Action)CopySelected), ("開啟至新查詢", (Action)OpenSelected),
            ("Add to Favorites", (Action)AddFavorite) })
        {
            var item = new MenuItem { Header = pair.Item1 };
            item.Click += (_, _) => QueryMemoryActions.Run(pair.Item2, Report);
            menu.Items.Add(item);
        }
        VsThemeBrushes.Apply(menu);
        _list.ContextMenu = menu;
        menu.Opened += (_, _) => SqlAssistPlatformGuard.Run("更新 SQL Memory 快捷選單", () =>
        {
            var row = _list.SelectedItem as QueryMemoryRow;
            foreach (MenuItem item in menu.Items) item.IsEnabled = _available && row is not null;
            ((MenuItem)menu.Items[2]).IsEnabled = _available && row?.CanAddFavorite == true;
            ((MenuItem)menu.Items[2]).Visibility = row?.IsFavorite == true ? Visibility.Collapsed : Visibility.Visible;
            ((MenuItem)menu.Items[2]).ToolTip = row?.AddFavoriteHint;
        });
        _list.RowActionRequested += action => QueryMemoryActions.Run(() =>
        {
            if (!_available) return;
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
        _hostTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _hostTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("更新查詢記憶狀態", CheckHost);
        _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, _tabs)) Changed(); };
        foreach (var combo in new[] { _kind, _period, _scope }) combo.SelectionChanged += (_, _) => Changed();
        _search.TextChanged += (_, _) => { clear.IsEnabled = _search.Text.Length > 0; Changed(); };
        clear.IsEnabled = false;
        _server.SelectionChanged += (_, _) => { if (_batchFilters) return; _database.Value = null; ReloadFacets(false); Changed(); };
        _database.SelectionChanged += (_, _) => Changed();
        _scope.SelectionChanged += (_, _) => ReloadFacets();
        _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, _tabs)) ReloadFacets(); };
        _server.OptionsRequested += (_, _) => LoadFacets(_server, false);
        _database.OptionsRequested += (_, _) => LoadFacets(_database, true);
        IsVisibleChanged += (_, _) => SqlAssistPlatformGuard.Run("切換查詢記憶可見度", () =>
        {
            if (IsVisible) { _hostTimer.Start(); CheckHost(); if (_available && !_state.Loading) Refresh(); }
            else { _hostTimer.Stop(); _facets.Cancel(); Invalidate(); _detail.Select(null); }
        });
        _ready = true;
        UpdateActions();
    }

    private bool IsFavorites => _tabs.SelectedIndex == 1;
    public void ShowPage(bool favorites)
    {
        _tabs.SelectedIndex = favorites ? 1 : 0;
        _search.Focus();
    }

    private void UseCurrentConnection()
    {
        var context = QueryMemoryConnections.ReadActive(_package);
        if (context is null || string.IsNullOrEmpty(context.Server) || string.IsNullOrEmpty(context.Database))
        {
            Report("目前沒有已連線的 SQL 查詢視窗；請先選取查詢視窗。原篩選未變更。");
            return;
        }
        // 一次更新兩個條件，不能在 Server 事件裡把剛指定的 Database 清掉。
        _batchFilters = true;
        try
        {
            if (IsFavorites) _scope.SelectedIndex = (int)FavoriteQueryScope.Database;
            _server.Value = context.Server; _database.Value = context.Database;
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
        if (!_available || _disposed || !IsVisible) return;
        var requestId = databases ? ++_databaseRequest : ++_serverRequest;
        var host = QueryMemoryHost.Generation;
        var token = _facets.Token;
        var request = new QueryConnectionFacetRequest(IsFavorites, (FavoriteQueryScope)_scope.SelectedIndex,
            databases, databases ? _server.Value : null, (QueryConnectionSort)filter.SortOrder, filter.Offset);
        _ = QueryMemoryActions.RunAsync(async () =>
        {
            try
            {
                var names = await QueryMemoryHost.ReadConnectionFacetsAsync(request, token);
                if (_disposed || !QueryMemoryHost.IsAvailable || token.IsCancellationRequested || host != QueryMemoryHost.Generation ||
                    requestId != (databases ? _databaseRequest : _serverRequest)) return;
                filter.SetOptions(names);
            }
            catch (Exception error)
            {
                // 名稱載入同樣有世代檢查，舊範圍的失敗不能蓋掉新頁面。
                if (!_disposed && !token.IsCancellationRequested && host == QueryMemoryHost.Generation &&
                    requestId == (databases ? _databaseRequest : _serverRequest)) Report("連線篩選載入失敗：" + error.Message);
            }
        }, Report);
    }

    private void AddFavorite()
    {
        if (!_available || _list.SelectedItem is not QueryMemoryRow { CanAddFavorite: true } row) return;
        var dialog = new FavoriteMetadataWindow(_package, row, false);
        if (dialog.ShowModal() == true) { Refresh(); Report("已加入收藏。"); }
    }

    private void CopySelected() => ReadSelectedContent(false);

    private void Changed()
    {
        if (!_ready || _disposed || _batchFilters) return;
        QueryMemoryActions.Run(() =>
        {
            _scope.Visibility = IsFavorites ? Visibility.Visible : Visibility.Collapsed;
            _historyFilters.Visibility = IsFavorites ? Visibility.Collapsed : Visibility.Visible;
            _server.IsEnabled = !IsFavorites || _scope.SelectedIndex > 0;
            _database.IsEnabled = !IsFavorites || _scope.SelectedIndex == 2;
            _server.Visibility = _server.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            _database.Visibility = _database.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            _server.EmptyLabel = _database.EmptyLabel = IsFavorites ? "請選擇" : "全部";
            Invalidate();
            _searchTimer.Start();
        }, Report);
    }

    private void Invalidate()
    {
        _searchTimer.Stop();
        _request.Cancel(); _request.Dispose(); _request = new CancellationTokenSource();
        _state.Reset(); _rows.Clear(); _detail.Select(null); _searchProgress = null; UpdateActions();
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
            _facets.Cancel(); _server.ResetOptions(); _database.ResetOptions();
            Invalidate();
            if (available) { Load(); ReloadFacets(); }
            else { _detail.Select(null); Report("SQL Memory 尚未就緒；可由設定啟用或重新啟用。"); }
        }
        if ((DateTime.UtcNow - _lastTimeRefresh).TotalMinutes >= 1)
        {
            _lastTimeRefresh = DateTime.UtcNow;
            foreach (var row in _rows) row.RefreshTime();
        }
        UpdateActions();
    }

    private void Refresh()
    {
        _restoreId = (_list.SelectedItem as QueryMemoryRow)?.Id;
        Invalidate(); Load(); ReloadFacets();
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
            var server = _server.Value;
            var database = _database.Value;
            QueryMemoryRow[] rows;
            string? cursor;
            string? progress;
            if (IsFavorites)
            {
                var scope = (FavoriteQueryScope)_scope.SelectedIndex;
                if (scope != FavoriteQueryScope.Global && (server is null || scope == FavoriteQueryScope.Database && database is null))
                {
                    Report(scope == FavoriteQueryScope.Server ? "請選擇收藏的伺服器。" : "請選擇收藏的伺服器與資料庫，或使用目前連線。");
                    return;
                }
                var request = new FavoriteQueryRequest(50, scope, scope == FavoriteQueryScope.Global ? null : server,
                    scope == FavoriteQueryScope.Database ? database : null, search, _state.Cursor);
                var page = await QueryMemoryHost.ReadFavoriteQueriesAsync(request, token);
                rows = page.Items.Select(item => new QueryMemoryRow(item)).ToArray(); cursor = page.NextCursor;
                progress = page.IsSearchPartial ? "已搜尋部分收藏" : null;
            }
            else
            {
                var request = new QueryHistoryRequest(50, (QueryHistoryKind)_kind.SelectedIndex, search,
                    server, database, _since, cursor: _state.Cursor);
                var page = await QueryMemoryHost.ReadHistoryAsync(request, token);
                rows = page.Items.Select(item => new QueryMemoryRow(item)).ToArray(); cursor = page.NextCursor;
                progress = !page.IsSearchPartial ? null : page.SearchedThrough is { } through
                    ? "已搜尋至 " + through.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) : "已搜尋部分紀錄";
            }
            if (token.IsCancellationRequested || !QueryMemoryHost.IsAvailable || hostGeneration != QueryMemoryHost.Generation ||
                !_state.Accept(generation, cursor)) return;
            _searchProgress = progress;
            foreach (var row in rows) _rows.Add(row);
            if (_restoreId is { } restore && _rows.FirstOrDefault(row => row.Id == restore) is { } selected)
                _list.SelectedItem = selected;
            _restoreId = null;
            if (_list.SelectedItem is null && _rows.Count > 0) _list.SelectedIndex = 0;
            // 搜尋每頁只檢查有限的候選；提早結束不是「沒有結果」，交給使用者決定是否繼續往前找。
            Report(progress is not null ? progress + "，繼續搜尋可再往前找。" :
                _rows.Count == 0 ? "沒有符合條件的項目。可清除搜尋或放寬期間與範圍。" : "");
        }
        catch (Exception error)
        {
            // 回應失敗只更新同一世代；舊查詢不得蓋掉新的狀態訊息。
            if (generation == _state.Generation && !token.IsCancellationRequested) Report("載入失敗：" + error.Message);
        }
        finally { _state.Fail(generation); UpdateActions(); }
    }

    private void UpdatePreview() =>
        _detail.Select(_available && IsVisible ? _list.SelectedItem as QueryMemoryRow : null, _splitView.IsDetailExpanded);

    private void OpenSelected() => ReadSelectedContent(true);

    private void ReadSelectedContent(bool open)
    {
        if (!_available || _opening || _list.SelectedItem is not QueryMemoryRow row) return;
        var token = _request.Token;
        var generation = QueryMemoryHost.Generation;
        _opening = true;
        _ = QueryMemoryActions.RunAsync(async () =>
        {
            try
            {
                var content = await QueryMemoryHost.ReadContentAsync(row.ContentId, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || !QueryMemoryHost.IsAvailable || generation != QueryMemoryHost.Generation) return;
                if (content is null) throw new InvalidOperationException("內容已不存在，請重新整理。");
                if (open) QueryMemoryActions.OpenQuery(_package, content.SqlText);
                else Clipboard.SetText(content.SqlText);
                Report(open ? "已開啟新查詢；未執行 SQL。" : "已複製完整 SQL。");
            }
            catch (Exception error)
            {
                // 切頁後取消的全文讀取，不得再寫剪貼簿、開窗或覆蓋新頁面的訊息。
                if (!_disposed && !token.IsCancellationRequested && QueryMemoryHost.IsAvailable && generation == QueryMemoryHost.Generation)
                    Report((open ? "開啟失敗：" : "複製失敗：") + error.Message);
            }
            finally { _opening = false; }
        }, Report);
    }

    private void UpdateActions()
    {
        _more.IsEnabled = _available && !_state.Loading && _state.Cursor is not null;
        _more.Content = _searchProgress is null ? "載入更多" : "繼續搜尋";
        _connection.IsEnabled = _available;
        _count.Text = $"已載入 {_rows.Count} 筆";
    }
    private void Report(string message) { if (!_disposed) { _status.Text = message; _status.ToolTip = message; } }
    private Button Button(string label, Action action, bool primary = false)
    {
        var button = SqlAssistChrome.CreateButton(label, SqlAssistChrome.DefaultMetrics, primary);
        button.Click += (_, _) => QueryMemoryActions.Run(action, Report);
        return button;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _hostTimer.Stop(); _searchTimer.Stop();
        _request.Cancel(); _request.Dispose(); _facets.Cancel(); _facets.Dispose(); _detail.Dispose();
    }
}
