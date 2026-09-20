using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.Notifications;
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
    private readonly SqlMemoryRecoveryView _recoveryView = new();
    private readonly FrameworkElement _historyFilters;
    private readonly TabItem _usageTab = SqlAssistChrome.CreateMemoryUsageTab();
    private readonly SqlMemoryUsagePanel _usagePanel;
    private readonly UIElement[] _listChrome;
    private CancellationTokenSource _facets = new();
    private CancellationTokenSource _request = new();
    private bool _batchFilters;
    private bool _ready;
    private bool _disposed;
    private bool _listStale;

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
        _tabs.Items.Add(SqlAssistChrome.CreateIconTab(SqlIcon.History, "History"));
        _tabs.Items.Add(SqlAssistChrome.CreateIconTab(SqlIcon.Favorite, "Favorites"));
        _tabs.Items.Add(_usageTab);
        _tabs.SelectedIndex = HistoryTab;
        _connection = SqlAssistChrome.CreateMemoryConnectionButton();
        _connection.Click += (_, _) => SqlMemoryActions.Run(UseCurrentConnection, Report);
        header.Children.Add(SqlAssistChrome.CreateMemoryToolbar(_tabs, _connection, Button("重新整理", RefreshCurrentTab),
            Button("設定", () => SqlMemoryActions.OpenSettings(_package))));
        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋");
        clear.Click += (_, _) => SqlMemoryActions.Run(() => { _search.Clear(); _search.Focus(); }, Report);
        _search.ToolTip = "區分大小寫的字面搜尋；歷史搜尋 SQL，收藏搜尋名稱、說明與 SQL。";
        System.Windows.Automation.AutomationProperties.SetName(_search, "搜尋 SQL 或收藏");
        var searchBar = SqlAssistChrome.CreateSearchBar(_search, clear);
        header.Children.Add(searchBar);
        var filters = new StackPanel();
        Select(_period, SqlMemoryBrowserModel.PeriodOptions, _model.Period);
        _historyFilters = SqlAssistChrome.CreateMemoryHistoryFilters(_kind, _period);
        filters.Children.Add(_historyFilters);
        header.Children.Add(filters);
        header.Children.Add(_server); header.Children.Add(_database);
        VsThemeBrushes.Apply(_server.SortMenu); VsThemeBrushes.Apply(_database.SortMenu);
        _hostStatus.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_hostStatus);
        // 搜尋、篩選與「目前連線」只屬於清單分頁；切到用量分頁一起收起。
        _listChrome = new UIElement[] { _connection, searchBar, filters, _server, _database };

        _status.TextWrapping = TextWrapping.Wrap; _status.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);

        _list.SetRowsSource(_rows, _pager);
        _list.LoadMoreRequested += (_, _) => Load();
        _pager.LoadMoreRequested += (_, _) => SqlMemoryActions.Run(Load, Report);
        _loading = new SqlLoadingSurface(_list);
        _detail = new SqlMemoryPreview(_commands, Report);
        _splitView = new SqlMemorySplitView(_loading, _detail, _detail.Summary);
        _splitView.DetailExpandedChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL 預覽", UpdatePreview);
        _recoveryView.Visibility = Visibility.Collapsed;
        _recoveryView.RebuildRequested += (_, _) => OnRecoveryRebuildRequested();
        _recoveryView.OpenFolderRequested += (_, _) => OnRecoveryOpenFolderRequested();
        _usagePanel = new SqlMemoryUsagePanel(package, Report);
        _usagePanel.View.Visibility = Visibility.Collapsed;
        // 整理期間可能已經切回清單分頁；那時直接重讀，否則等回到清單分頁才讀。
        _usagePanel.RecordsChanged += (_, _) => SqlMemoryActions.Run(() =>
        {
            if (IsUsageSelected || !_model.IsAvailable) _listStale = true;
            else RefreshList();
        }, Report);
        var bodyContainer = new Grid();
        bodyContainer.Children.Add(_splitView);
        bodyContainer.Children.Add(_recoveryView);
        bodyContainer.Children.Add(_usagePanel.View);
        root.Children.Add(bodyContainer); Content = root;
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
            if (e.Key != Key.F || e.KeyboardDevice.Modifiers != ModifierKeys.Control || IsUsageSelected) return;
            _search.Focus(); e.Handled = true;
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
            if (ReferenceEquals(e.Source, _tabs)) SqlMemoryActions.Run(OnTabChanged, Report);
        };
        _kind.SelectionChanged += (_, _) => { _model.Kind = SqlMemoryBrowserModel.KindOptions[_kind.SelectedIndex].Value; Changed(); };
        _period.SelectionChanged += (_, _) => { _model.Period = SqlMemoryBrowserModel.PeriodOptions[_period.SelectedIndex].Value; Changed(); };
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
            if (IsVisible)
            {
                _clockTimer.Start(); foreach (var row in _rows) row.RefreshTime(); ObserveHost(forceReload: !_model.IsLoading);
                if (IsUsageSelected) _usagePanel.Reload();
            }
            else { _clockTimer.Stop(); _facets.Cancel(); Invalidate(); _detail.Select(null); _usagePanel.Suspend(); }
        });
        SqlMemoryHost.Runtime.StatusChanged += OnRuntimeStatusChanged;
        SqlMemoryHost.Runtime.CapacityChanged += OnCapacityChanged;
        SqlAssistChrome.SetUsageBadge(_usageTab, SqlMemoryHost.Runtime.CapacitySeverity, motion: false);
        _ready = true;
        ObserveHost(forceReload: false);
    }

    private const int HistoryTab = 0;
    private const int FavoritesTab = 1;
    private const int UsageTab = 2;

    private bool IsUsageSelected => _tabs.SelectedIndex == UsageTab;

    public void ShowPage(SqlMemoryPage page)
    {
        var index = page switch { SqlMemoryPage.Favorites => FavoritesTab, SqlMemoryPage.Usage => UsageTab, _ => HistoryTab };
        // 復原卡片在畫面上時用量沒有東西可讀，命令仍帶到清單分頁看復原說明。
        if (index == UsageTab && !_usageTab.IsEnabled) index = _model.IsFavorites ? FavoritesTab : HistoryTab;
        // 已在同一個分頁不會觸發 SelectionChanged；命令的意思是「帶我去看最新的」，所以用量要重讀。
        if (_tabs.SelectedIndex == index && index == UsageTab) _usagePanel.Reload();
        _tabs.SelectedIndex = index;
        if (index != UsageTab) _search.Focus();
    }

    /// <summary>
    /// 分頁切換：清單的兩個分頁共用主從區，用量分頁取代它。清單的頁面與選取在用量分頁期間保留，
    /// 回到原本的清單分頁不重載；只有換了清單分頁，或期間做過整理動作，才重讀。
    /// </summary>
    private void OnTabChanged()
    {
        if (_disposed) return;
        if (IsUsageSelected)
        {
            foreach (var element in _listChrome) element.Visibility = Visibility.Collapsed;
            _splitView.Visibility = Visibility.Collapsed;
            _usagePanel.View.Visibility = Visibility.Visible;
            SqlAssistChrome.PlayAppear(_usagePanel.View);
            _usagePanel.Reload();
            return;
        }

        if (_usagePanel.View.Visibility == Visibility.Visible)
        {
            _usagePanel.Suspend();
            _usagePanel.View.Visibility = Visibility.Collapsed;
            foreach (var element in _listChrome) element.Visibility = Visibility.Visible;
            if (_recoveryView.Visibility != Visibility.Visible)
            {
                _splitView.Visibility = Visibility.Visible;
                SqlAssistChrome.PlayAppear(_splitView);
            }
        }
        var tab = _tabs.SelectedIndex == FavoritesTab ? SqlMemoryBrowserTab.Favorites : SqlMemoryBrowserTab.History;
        if (_model.Tab != tab)
        {
            _listStale = false;
            _model.Tab = tab;
            Changed(); ReloadFacets();
        }
        else if (_listStale && _model.IsAvailable) RefreshList();
        _historyFilters.Visibility = _model.IsFavorites ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SqlMemoryHost.Runtime.StatusChanged -= OnRuntimeStatusChanged;
        SqlMemoryHost.Runtime.CapacityChanged -= OnCapacityChanged;
        _usagePanel.Dispose();
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

    /// <summary>容量分級可能在背景維護的執行緒改變；排回 UI 執行緒更新工具列的警示點。</summary>
    private void OnCapacityChanged(object? sender, SqlMemoryCapacityChangedEventArgs change) =>
        SqlAssistPlatformGuard.Probe("排入 SQL Memory 容量更新", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                SqlAssistPlatformGuard.Run("更新 SQL Memory 用量警示", () =>
                {
                    if (!_disposed) SqlAssistChrome.SetUsageBadge(_usageTab, SqlMemoryHost.Runtime.CapacitySeverity);
                }))));

    private void ObserveHost(bool forceReload)
    {
        if (_disposed) return;
        var runtime = SqlMemoryHost.Runtime;
        var status = runtime.Status;
        var recovery = RecoveryOffer(status);
        ShowRecovery(status, recovery);

        if (_model.ObserveHost(runtime.IsAvailable, status.Generation))
        {
            if (IsUsageSelected) _usagePanel.Reload();
            // 清單、facets 與預覽都屬於舊儲存；換世代就整份作廢。
            _facets.Cancel(); _server.ResetOptions(); _database.ResetOptions();
            Invalidate();
            if (_model.IsAvailable) { Load(); ReloadFacets(); }
            else
            {
                _detail.Select(null);
                // 復原卡片已經把狀況與下一步說完了，狀態列不再重講一次。
                Report(recovery is null ? "SQL Memory 尚未就緒；可由設定啟用或重新啟用。" : "");
            }
        }
        else if (forceReload && _model.IsAvailable && IsVisible) RefreshList();
        UpdateActions();
    }

    /// <summary>可以就地復原的開檔失敗；其餘狀態一律留給狀態列那一行。</summary>
    private static (string Title, string Description)? RecoveryOffer(SqlMemoryRuntimeStatus status)
    {
        if (status.Phase != SqlMemoryRuntimePhase.OpenFailed || !SqlMemoryRecoveryService.CanRecover) return null;
        return status.ErrorKind switch
        {
            SqlMemoryStorageErrorKind.Incompatible => ("資料庫版本不相容",
                "現有的 SQL Memory 資料庫不是這個版本能開啟的。備份並重建之後，歷史與收藏從空白開始記錄，舊檔案留在同一個資料夾。"),
            SqlMemoryStorageErrorKind.Corrupt => ("資料庫檔案損毀",
                "SQL Memory 資料庫已無法開啟。備份並重建之後，歷史與收藏從空白開始記錄，損毀的檔案留在同一個資料夾。"),
            _ => null,
        };
    }

    private void ShowRecovery(SqlMemoryRuntimeStatus status, (string Title, string Description)? recovery)
    {
        // 復原卡片優先：資料庫開不起來時用量沒有東西可讀，分頁停用並帶回清單分頁看復原說明。
        _usageTab.IsEnabled = recovery is null;
        if (recovery is not null && IsUsageSelected) _tabs.SelectedIndex = _model.IsFavorites ? FavoritesTab : HistoryTab;
        if (recovery is not { } text)
        {
            _recoveryView.Visibility = Visibility.Collapsed;
            _splitView.Visibility = IsUsageSelected ? Visibility.Collapsed : Visibility.Visible;
            _hostStatus.Text = status.Message;
            _hostStatus.Visibility = _hostStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        _recoveryView.SetStatus(text.Title, text.Description);
        _hostStatus.Visibility = Visibility.Collapsed;
        // 同一個失敗會來好幾次狀態更新；已經在畫面上就不重播進場。
        if (_recoveryView.Visibility == Visibility.Visible) return;
        _splitView.Visibility = Visibility.Collapsed;
        _recoveryView.Visibility = Visibility.Visible;
        _recoveryView.Reveal();
    }

    private void OnRecoveryRebuildRequested()
    {
        var owner = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        var confirmed = SqlAssistConfirmationWindow.Confirm(owner!, "重建 SQL Memory 資料庫",
            "備份並重新建立 SQL Memory 資料庫？",
            "現有的資料庫檔案更名封存在同一個資料夾，不會刪除；新資料庫從空白開始記錄，" +
            "已封存的歷史與收藏這個版本讀不回來。", "備份並重建");

        if (!confirmed) return;

        _ = SqlMemoryActions.RunAsync(async () =>
        {
            // 重建要開檔、封存與建立新 schema，使用者可能中途切回編輯器；「正在重建」交給卡片，
            // 兩顆按鈕停用已經說了這一區動不了。備份檔名只有這裡放得下：通知文案不放路徑與檔名。
            using var notification = NotificationCenter.Default.Begin(NotificationCatalog.RebuildingSqlMemory,
                NotificationKind.SqlMemory, NotificationOrigin.User, NotificationLevel.Info);
            _recoveryView.SetRebuilding(true);
            try
            {
                var backupPath = await SqlMemoryRecoveryService.BackupAndRecreateAsync().ConfigureAwait(true);
                if (backupPath.Length != 0) Report($"舊檔案備份為 {Path.GetFileName(backupPath)}。");
            }
            catch (OperationCanceledException)
            {
                notification.Cancel();
                throw;
            }
            catch (Exception)
            {
                // 重建失敗要讀完才知道下一步，除了卡片也留在復原卡片的狀態列：這是使用者按下去的修復。
                notification.Fail();
                throw;
            }
            finally { _recoveryView.SetRebuilding(false); }
        }, Report);
    }

    // 不走 SqlAssistPlatformGuard：使用者按了按鈕卻什麼都沒開，沒有訊息只會被當成按鈕壞了。
    private void OnRecoveryOpenFolderRequested() =>
        SqlMemoryActions.Run(SqlMemoryRecoveryService.OpenDatabaseFolder, Report);

    private void UseCurrentConnection()
    {
        var failure = _model.UseConnection(SqlWindowConnections.ReadActive(_package));
        if (failure is not null) { Report(failure); return; }
        // 一次更新兩個條件，不能在 Server 事件裡把剛指定的 Database 清掉。
        _batchFilters = true;
        try { _server.Value = _model.Server; _database.Value = _model.Database; }
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
            // 伺服器與資料庫篩選兩頁同一種語意，只有狀態與期間屬於 History。
            _historyFilters.Visibility = _model.IsFavorites ? Visibility.Collapsed : Visibility.Visible;
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

    /// <summary>工具列的重新整理作用在目前的分頁。</summary>
    private void RefreshCurrentTab()
    {
        if (IsUsageSelected) _usagePanel.Reload();
        else RefreshList();
    }

    /// <summary>重讀清單並保留選取；用量分頁期間也照常讀，回到清單時已是最新。</summary>
    private void RefreshList()
    {
        _listStale = false;
        _model.RememberSelection((_list.SelectedItem as SqlMemoryRow)?.Id);
        Invalidate(); Load(); ReloadFacets();
    }

    private void Load() => _ = SqlMemoryActions.RunAsync(LoadAsync, Report);

    private async Task LoadAsync()
    {
        if (_disposed || !IsVisible || _model.BeginLoad() is not { } load) return;
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

    /// <summary>
    /// 收藏更新後移到最上面：清單依最後儲存時間排序，剛存的那筆就是最新的。保留其他已載入的頁；
    /// 改到目前篩選以外或已不存在則移出清單。
    /// </summary>
    private void ReplaceRow(SqlMemoryRow row, SqlMemoryRow? updated)
    {
        var index = _rows.IndexOf(row);
        if (_disposed || index < 0) return;
        if (updated?.Favorite is not { } favorite || !_model.MatchesFavoriteFilter(favorite.Favorite)) { RemoveRow(row); return; }
        var selected = ReferenceEquals(_list.SelectedItem, row);
        _rows.RemoveAt(index);
        _rows.Insert(0, updated);
        if (!selected) return;
        _list.SelectedIndex = 0;
        _list.ScrollIntoView(updated);
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
