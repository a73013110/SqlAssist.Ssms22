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
    // 兩顆都是多選：History 與 Favorites 的列早就存在，這一層只是縮小已存的那一份，而使用者要比的
    // 往往就是「這幾台上的同一段 SQL」。名單一頁一百個且可續頁，所以帶搜尋框；全選不放，
    // 它與第一列那個「全部」是同一件事。
    private readonly ConnectionFacet _serverFacet =
        new(new SqlFilterFlyout("伺服器", SqlIcon.Server, SqlFilterMode.SearchableMultiple), databases: false, "伺服器",
            "不限伺服器；每一台上的紀錄都列。", "更多伺服器", " 台");
    private readonly ConnectionFacet _databaseFacet =
        new(new SqlFilterFlyout("資料庫", SqlIcon.Database, SqlFilterMode.SearchableMultiple), databases: true, "資料庫",
            "不限資料庫；目前條件下的每一個都列。", "更多資料庫", " 個");
    private readonly ConnectionFacet[] _connectionFacets;
    private readonly SqlPillSelector _kind = Pills(SqlMemoryBrowserModel.KindOptions);
    private readonly SqlPillSelector _period = Pills(SqlMemoryBrowserModel.PeriodOptions);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _hostStatus = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly SqlMemoryPager _pager = new();
    private readonly SqlStateSurface _surface;
    private readonly Button _connection;
    private readonly Button _refresh = SqlAssistChrome.CreateIconButton(
        SqlIcon.Refresh, "重新整理：重讀這一份清單。");
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _settleTimer;
    private readonly SqlMemoryPreview _detail;
    private readonly MasterDetailView _splitView;
    private readonly SqlMemoryRecoveryView _recoveryView = new();
    private readonly TabItem _usageTab = SqlAssistChrome.CreateMemoryUsageTab();
    private readonly SqlMemoryUsagePanel _usagePanel;
    private readonly UIElement[] _listChrome;
    private CancellationTokenSource _facets = new();
    private CancellationTokenSource _request = new();
    private bool _ready;
    private bool _disposed;
    private bool _listStale;
    /// <summary>這一輪讀清單失敗了；一列都沒有時由狀態表面說，還有列時留在狀態列。</summary>
    private string _loadFailure = "";

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
        header.Children.Add(SqlAssistChrome.CreateMemoryToolbar(
            _tabs, Button("設定", () => SqlMemoryActions.OpenSettings(_package))));
        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋");
        clear.Click += (_, _) => SqlMemoryActions.Run(() => { _search.Clear(); _search.Focus(); }, Report);
        _search.ToolTip = "區分大小寫的字面搜尋；歷史搜尋 SQL，收藏搜尋名稱、說明與 SQL。";
        System.Windows.Automation.AutomationProperties.SetName(_search, "搜尋 SQL 或收藏");
        _refresh.Click += (_, _) => SqlMemoryActions.Run(RefreshList, Report);
        // 與 SQL Search 同一列規範：框裡是修飾搜尋字串的直接控制，框外右緣是作用在這一份
        // 清單的操作。History／Favorites 沒有排序（清單本來就依時間），所以那一格是目前連線。
        var searchRow = new SqlInputRow(
            SqlAssistChrome.CreateInputBar(SqlIcon.Search, _search, clear), _connection, _refresh)
        {
            Margin = new Thickness(0, 0, 0, 6)
        };
        header.Children.Add(searchRow);
        Select(_period, SqlMemoryBrowserModel.PeriodOptions, _model.Period);
        // 狀態、期間與連線是第二層的三群，併在同一列：各佔一列的那一版在停靠面板裡等於
        // 永久少看一筆 SQL，而放不下的時候那一層本來就會整群換行。
        _connectionFacets = new[] { _serverFacet, _databaseFacet };
        var filters = SqlAssistChrome.CreateMemoryFilterRow(
            _kind, _period, _serverFacet.Panel, _databaseFacet.Panel);
        filters.Margin = new Thickness(0, 4, 0, 0);
        header.Children.Add(filters);
        _hostStatus.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_hostStatus);
        // 搜尋、篩選與那一列右緣的操作只屬於清單分頁；切到用量分頁整列一起收起，
        // 用量自己的重新整理在它的狀態卡片上。
        _listChrome = new UIElement[] { searchRow, filters };

        _status.TextWrapping = TextWrapping.Wrap; _status.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);

        _list.SetRowsSource(_rows, _pager);
        _list.LoadMoreRequested += (_, _) => Load();
        _pager.LoadMoreRequested += (_, _) => SqlMemoryActions.Run(Load, Report);
        _surface = new SqlStateSurface(_list);
        _detail = new SqlMemoryPreview(_commands, Report);
        _splitView = new MasterDetailView(_surface, _detail, _detail.Summary, MasterDetailView.DefaultSideBySideWidth);
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
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SqlAssistChrome.Debounce.MemorySearch };
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
        foreach (var facet in _connectionFacets) ConfigureFacet(facet);
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
            InvalidateFacets(); Changed();
        }
        else if (_listStale && _model.IsAvailable) RefreshList();
        UpdateHistoryFilters();
    }

    /// <summary>狀態與期間只屬於 History；收起來之後列首那一條分隔線由篩選列跟著收。</summary>
    private void UpdateHistoryFilters()
    {
        var visible = _model.IsFavorites ? Visibility.Collapsed : Visibility.Visible;
        _kind.Visibility = _period.Visibility = visible;
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
            InvalidateFacets();
            Invalidate();
            if (_model.IsAvailable) Load();
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
        // 模型一次換掉兩份名單，不能在指定伺服器的路徑上把剛指定的資料庫清掉。
        foreach (var facet in _connectionFacets) { UpdateFacetSummary(facet); facet.Reset(); FillFacet(facet); }
        Changed();
    }

    /// <summary>
    /// 兩顆連線面板共用的排序選項。
    /// </summary>
    /// <remarks>
    /// 圖示走與工具列同一份 <see cref="SqlAssistChrome.MemoryOptionIcon"/> 對照，而面板上的按鈕
    /// 與它的選單又讀同一份這個清單：三處各挑一次圖示的下場是同一個排序在三個地方長得不一樣。
    /// 共用同一個執行個體也讓每個面板只建一次選單（見 <see cref="SqlFilterFlyout.SetSortOptions"/>）。
    /// </remarks>
    private static readonly IReadOnlyList<SqlFilterSortOption> FacetSorts = Array.AsReadOnly(
        SqlMemoryBrowserModel.SortOptions.Select(option => new SqlFilterSortOption(
            option.Value, option.Label, option.ShortLabel, SqlAssistChrome.MemoryOptionIcon(option.Value))).ToArray());

    /// <summary>未指定名稱那一列的字；面板第一列與按鈕摘要共用同一份。</summary>
    private const string AnyFacetLabel = "全部";

    private void ConfigureFacet(ConnectionFacet facet)
    {
        var panel = facet.Panel;
        VsThemeBrushes.Apply(panel);
        panel.SetSortOptions(FacetSorts, facet.Sort);
        UpdateFacetSummary(facet);
        FillFacet(facet);
        panel.OptionsRequested += (_, _) => SqlMemoryActions.Run(() => ShowFacet(facet), Report);
        panel.MoreRequested += (_, _) => SqlMemoryActions.Run(() => LoadFacet(facet), Report);
        panel.SortRequested += value => SqlMemoryActions.Run(() =>
        {
            if (value is not SqlConnectionFacetSort sort || facet.Sort == sort) return;
            facet.Sort = sort;
            panel.SetSortOptions(FacetSorts, sort);
            // 排序換的是同一份名單的先後，不是條件：已選的那一個留著，名單從第一頁重問。
            facet.Reset(); FillFacet(facet); LoadFacet(facet);
        }, Report);
    }

    /// <summary>
    /// 面板打開了：先畫手上有的，沒有名單才去問。
    /// </summary>
    /// <remarks>
    /// 展開下拉<b>就是</b>使用者在要求這份名單，所以這裡去問儲存層是對的；禁止的是沒有人打開它
    /// 的時候先問一輪。已經選起來的那一個一定要先畫得出來，否則他在等名單的期間換不回去。
    /// </remarks>
    private void ShowFacet(ConnectionFacet facet)
    {
        FillFacet(facet);
        if (!facet.IsLoaded) LoadFacet(facet);
    }

    /// <summary>問下一頁名稱；第一頁與續頁走同一條路，差別只有位移。</summary>
    private void LoadFacet(ConnectionFacet facet)
    {
        if (!_model.IsAvailable || _disposed || !IsVisible) return;
        var requestId = _model.BeginFacet(facet.Databases);
        var host = _model.HostGeneration;
        var token = _facets.Token;
        var request = _model.FacetRequest(facet.Databases, facet.Sort, facet.Offset);
        facet.Panel.SetNotice("正在讀取" + facet.Name + "清單…", busy: true);
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            try
            {
                var names = await SqlMemoryHost.Runtime.ReadConnectionFacetsAsync(request, token);
                // 名稱載入同樣有世代檢查，舊範圍的回應不能蓋掉新頁面。
                if (_disposed || token.IsCancellationRequested || !_model.IsCurrentFacet(facet.Databases, requestId, host)) return;
                facet.Append(names);
                facet.Panel.SetNotice("");
                FillFacet(facet);
            }
            catch (Exception error)
            {
                if (_disposed || token.IsCancellationRequested || !_model.IsCurrentFacet(facet.Databases, requestId, host)) return;
                // 這一句留在面板裡而不是狀態列：使用者正盯著那份空清單等答案，而狀態列說的是清單那一輪。
                facet.Panel.SetNotice(SqlMemoryTimeText.Failure(facet.Name + "清單載入", error));
            }
        }, Report);
    }

    /// <summary>
    /// 把手上的名稱畫成面板的選項；第一列是「全部」。
    /// </summary>
    /// <remarks>
    /// 已選的那一個可能不在手上這幾頁裡（換過排序，或還沒續到那一頁）：列在最前面且一律列出，
    /// 否則使用者在面板上取消不掉自己剛選的條件。
    /// </remarks>
    private void FillFacet(ConnectionFacet facet)
    {
        var options = new List<SqlFilterOption>();
        var selected = Selection(facet);

        void Add(string name) => options.Add(new SqlFilterOption(
            name, facet.Name + "：" + name, IsFacetSelected(facet, name),
            on => SqlMemoryActions.Run(() => ToggleFacet(facet, name, on), Report)));

        // 已經勾起來的名稱可能不在手上這幾頁裡（換過排序，或還沒續到那一頁）：排在最前面且一律列出，
        // 否則使用者在面板上取消不掉自己剛勾的條件。
        foreach (var name in selected) if (!facet.Names.Contains(name)) Add(name);
        foreach (var name in facet.Names) Add(name);

        facet.Panel.SetEmptyOption(new SqlFilterOption(AnyFacetLabel, facet.EmptyHint, selected.Count == 0,
            on => SqlMemoryActions.Run(() => { if (on) ClearFacet(facet); }, Report)));
        facet.Panel.SetOptions(new[] { new SqlFilterGroup("", options) });
        facet.Panel.SetMore(facet.HasMore ? facet.MoreLabel : null);
    }

    /// <summary>這一顆面板目前勾起來的名稱；模型是唯一的出處，面板與按鈕都只是把它畫出來。</summary>
    private IReadOnlyList<string> Selection(ConnectionFacet facet) =>
        facet.Databases ? _model.Databases : _model.Servers;

    private bool IsFacetSelected(ConnectionFacet facet, string name) =>
        facet.Databases ? _model.IsDatabaseSelected(name) : _model.IsServerSelected(name);

    /// <summary>勾或取消勾一個名稱；語意是對已存的列篩選，不是切換 SSMS 連線。</summary>
    private void ToggleFacet(ConnectionFacet facet, string name, bool selected)
    {
        var changed = facet.Databases
            ? _model.SetDatabaseSelected(name, selected)
            : _model.SetServerSelected(name, selected);
        if (!changed) return;
        // 只把第一列那個「全部」的勾改過來，不重建整份清單：使用者正在連勾好幾個，
        // 重建會把捲動位置與鍵盤焦點一起丟掉，而他還在往下走。
        facet.Panel.SyncEmptyOption(Selection(facet).Count == 0);
        AfterFacetChanged(facet, refill: false);
    }

    /// <summary>回到「全部」；面板第一列是這個維度唯一的清除入口，所以不另畫一顆取消全選。</summary>
    private void ClearFacet(ConnectionFacet facet)
    {
        var changed = facet.Databases ? _model.ClearDatabases() : _model.ClearServers();
        // 其餘幾列的勾要一起清掉，所以這一支非重建不可。
        if (changed) AfterFacetChanged(facet, refill: true);
    }

    /// <summary>
    /// 條件真的變了之後共用的收尾。
    /// </summary>
    /// <remarks>
    /// 動過伺服器就連資料庫那一顆一起重畫：資料庫名單是照選中的伺服器問回來的，
    /// 而模型已經把上一輪的資料庫清掉了（見 <see cref="SqlMemoryBrowserModel.SetServerSelected"/>）。
    /// 重建等這一輪事件走完再做——面板的繫結還在回寫，就地換掉 <c>ItemsSource</c> 等於回收
    /// 正在發事件的那一顆核取方塊。
    /// </remarks>
    private void AfterFacetChanged(ConnectionFacet facet, bool refill)
    {
        UpdateFacetSummary(facet);
        if (!facet.Databases)
        {
            UpdateFacetSummary(_databaseFacet);
            _databaseFacet.Reset();
            Defer(() => FillFacet(_databaseFacet));
        }

        if (refill) Defer(() => FillFacet(facet));
        Changed();
    }

    /// <summary>
    /// 按鈕上的摘要；一個都沒勾就是「全部」，與面板第一列共用同一份字。
    /// </summary>
    /// <remarks>
    /// 勾了好幾個時按鈕上只剩數量，完整名單留在 Tooltip 與面板裡：名字全攤在按鈕上會把那一列撐到換行，
    /// 而在停靠面板裡換行的代價就是少看幾筆結果。
    /// </remarks>
    private void UpdateFacetSummary(ConnectionFacet facet)
    {
        var selected = Selection(facet);
        facet.Panel.UpdateSummary(
            SqlFilterSummary.Of(selected.Count, AnyFacetLabel, selected.Count == 1 ? selected[0] : null, facet.Unit),
            selected.Count == 0 ? facet.EmptyHint : SqlFilterSummary.Detail(selected));
    }

    /// <summary>手上的名單作廢；下次打開面板才重問，沒有人在看的時候不去問儲存層。</summary>
    private void InvalidateFacets()
    {
        _facets.Cancel(); _facets.Dispose(); _facets = new CancellationTokenSource();
        foreach (var facet in _connectionFacets) { facet.Reset(); FillFacet(facet); }
    }

    /// <summary>等這一輪事件走完再做；面板重建會回收正在回寫勾選狀態的那一顆選項。</summary>
    private void Defer(Action action) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed) SqlMemoryActions.Run(action, Report);
        }));

    /// <summary>一顆連線篩選面板的狀態：累積到的名稱、下一頁位移與排序。</summary>
    /// <remarks>
    /// 位移與「還有沒有下一頁」留在宿主而不是面板：一頁幾筆、以及多回一筆代表還有下一頁，
    /// 都是 <see cref="SqlConnectionFacetRequest"/> 這一層自己的契約，共用的面板不該認得它。
    /// </remarks>
    private sealed class ConnectionFacet
    {
        private readonly List<string> _names = new();

        public ConnectionFacet(SqlFilterFlyout panel, bool databases, string name, string emptyHint, string moreLabel,
            string unit)
        {
            Panel = panel; Databases = databases; Name = name; EmptyHint = emptyHint; MoreLabel = moreLabel; Unit = unit;
        }

        public SqlFilterFlyout Panel { get; }

        public bool Databases { get; }

        public string Name { get; }

        /// <summary>未選時面板第一列與按鈕 Tooltip 的說明。</summary>
        public string EmptyHint { get; }

        public string MoreLabel { get; }

        /// <summary>摘要只剩數量時的量詞；「3 個」與「3 台」讀起來不是同一件事。</summary>
        public string Unit { get; }

        public SqlConnectionFacetSort Sort { get; set; } = SqlConnectionFacetSort.Recent;

        public int Offset { get; private set; }

        public bool HasMore { get; private set; }

        /// <summary>已經問過至少一頁；沒問過的面板打開時才去問。</summary>
        public bool IsLoaded { get; private set; }

        public IReadOnlyList<string> Names => _names;

        public void Reset() { _names.Clear(); Offset = 0; HasMore = false; IsLoaded = false; }

        /// <param name="names">儲存層多回一筆代表還有下一頁；多出的那一筆不顯示。</param>
        public void Append(IReadOnlyList<string> names)
        {
            var count = Math.Min(names.Count, SqlConnectionFacetRequest.PageSize);
            for (var i = 0; i < count; i++) if (!_names.Contains(names[i])) _names.Add(names[i]);
            Offset += count;
            HasMore = names.Count > SqlConnectionFacetRequest.PageSize;
            IsLoaded = true;
        }
    }

    private void Changed()
    {
        if (!_ready || _disposed) return;
        SqlMemoryActions.Run(() =>
        {
            // 伺服器與資料庫篩選兩頁同一種語意，只有狀態與期間屬於 History。
            UpdateHistoryFilters();
            Invalidate();
            _searchTimer.Start();
        }, Report);
    }

    private void Invalidate()
    {
        _searchTimer.Stop(); _settleTimer.Stop();
        _request.Cancel(); _request.Dispose(); _request = new CancellationTokenSource();
        _model.Invalidate(DateTimeOffset.Now);
        _loadFailure = "";
        Report("");
        _rows.Clear(); _detail.Select(null); UpdateActions();
    }

    /// <summary>重讀清單並保留選取；用量分頁期間也照常讀，回到清單時已是最新。</summary>
    private void RefreshList()
    {
        _listStale = false;
        _model.RememberSelection((_list.SelectedItem as SqlMemoryRow)?.Id);
        InvalidateFacets(); Invalidate(); Load();
    }

    private void Load() => _ = SqlMemoryActions.RunAsync(LoadAsync, Report);

    private async Task LoadAsync()
    {
        if (_disposed || !IsVisible || _model.BeginLoad() is not { } load) return;
        var token = _request.Token;
        _loadFailure = ""; Report(""); UpdateActions();
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
            if (!token.IsCancellationRequested && _model.IsCurrent(load))
            {
                _loadFailure = SqlMemoryTimeText.Failure("載入", error);
                // 清單上還留著前幾頁時失敗留在狀態列：蓋住讀得到的那幾十筆沒有道理。
                if (_rows.Count != 0) Report(_loadFailure);
            }
        }
        finally { _model.End(load); UpdateActions(); }
    }

    /// <summary>刪除成功：先讓卡片淡出，再真正移出集合；選取留在原位置（原本的下一列），鍵盤焦點跟著走。</summary>
    private void RemoveRow(SqlMemoryRow row)
    {
        if (_disposed || row.IsRemoving || !_rows.Contains(row)) return;
        row.IsRemoving = true;
        if (!SqlAssistChrome.MotionEnabled) { CompleteRemoval(row); return; }
        var exit = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SqlAssistChrome.CardExitDuration };
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
        var footer = _model.Footer(_rows.Count);
        // 空狀態搬到主內容區：頁尾那顆膠囊貼在一整片空白的下緣，而使用者的視線在中間。
        // 兩邊各說一次的話，「沒有符合條件」看起來像發生了兩件事。
        var empty = footer.Kind == SqlMemoryFooterKind.Empty;
        _pager.Update(empty ? SqlMemoryFooter.Hidden : footer);
        _surface.State = SqlMemorySurfaceState.For(footer, _model.IsLoading, _rows.Count, _loadFailure);
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
