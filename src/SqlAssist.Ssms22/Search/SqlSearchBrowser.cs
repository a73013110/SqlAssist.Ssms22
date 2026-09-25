using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.Connections;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Search;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Core.Tabular;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// SQL Search 工具窗的畫面與繫結。
/// </summary>
/// <remarks>
/// 與 SQL Memory 的瀏覽器同一種分工：輸入、範圍與選項的值交給 <see cref="SqlSearchBrowserModel"/>，
/// 回應也交回它決定要不要採用；這裡只做版面、繫結與派送。來源清單在
/// <see cref="SqlSearchProviders"/>，啟動在 <see cref="SqlSearchActivation"/>，三者都不互相知道細節。
///
/// 版面只有兩塊：工具列兩層（上層 filters 與常駐的分段開關，下層搜尋框與排序／重新整理，
/// 多選時由選取工具列蓋住）與主從區。條件不另起一列 chip：過濾按鈕的摘要與強調底框已經說了
/// 哪幾個維度有條件，在停靠面板裡多一列就是少看一筆結果。
///
/// 沒有狀態列：筆數與「這一份為什麼不完整」寫在清單的頁尾（與 SQL Memory 同一個
/// <see cref="SqlListPager"/>），按下去的結果與失敗一律走通知（<see cref="Notify"/>）。
/// </remarks>
internal sealed class SqlSearchBrowser : UserControl, IDisposable
{
    /// <summary>新列的進場旗標保留多久；比進場動畫長一點，之後捲動重用容器不會重播。</summary>
    private static readonly TimeSpan NewRowSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>一次套用幾列；兩百列一次塞進集合會讓清單重算一整份版面。</summary>
    private const int RowBatch = 40;

    private readonly IServiceProvider _services;
    private readonly SqlSearchCatalogs _catalogs;
    private readonly SqlSearchBrowserModel _model = new();
    private readonly SqlSearchProviders _providers = new();
    private readonly SqlSearchScopeDatabases _scopeDatabases = new();
    private readonly ObservableCollection<SqlSearchRow> _rows = new();
    private readonly Dictionary<string, string> _categoryLabels = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<SqlSearchCategoryOption> _categoryOptions;
    private readonly SqlSearchList _list = new();
    private readonly SqlSearchPreview _preview;
    private readonly MasterDetailView _splitView;
    private readonly SqlStateSurface _surface;
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SqlListPager _footer = new();
    private readonly SqlMatchToggles _matchToggles = new();
    private readonly Button _connection;
    private readonly SqlSearchSegments _segments = new();
    private readonly SqlFilterFlyout _server = new(SqlKindText.Server, SqlIcon.Server, SqlFilterMode.Single);
    private readonly SqlFilterFlyout _databases = new(SqlKindText.Database, SqlIcon.Database, SqlFilterMode.SearchableMultiple);
    private readonly SqlFilterFlyout _kinds = new(CommonText.Kind, SqlIcon.Filter);
    private readonly SqlCardSelection<SqlSearchRow, string> _selection;
    private readonly SqlSelectionBar _selectionBar;
    private readonly Button _sort = SqlAssistChrome.CreateIconButton(SqlIcon.SortDescending, SqlSearchText.Sort);
    private readonly Button _refresh = SqlAssistChrome.CreateIconButton(
        SqlIcon.Refresh, SqlSearchText.Refresh);
    private readonly ContextMenu _sortMenu = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _settleTimer;
    private CancellationTokenSource _request = new();
    private IReadOnlyList<SearchHit> _applying = Array.Empty<SearchHit>();
    private int _applied;

    /// <summary>上一次送出通知的那一句失敗；同一句每一輪都送的話，邊打字邊跳出同一則通知。</summary>
    private string _notifiedFailure = "";

    private SqlSearchRound? _round;

    private bool _activating;

    /// <summary>
    /// 作用中查詢視窗連著哪一台；每一列「會不會開未連線的視窗」照它算。
    /// </summary>
    /// <remarks>
    /// 在查詢視窗換過的那一刻（<see cref="ObserveActiveEditor"/>）問一次，新列建立時直接拿來用。
    /// 每一列、每畫一次各問一次宿主的話，一批一百列就是一百次連線查詢。它只管移至定義，不動範圍。
    /// </remarks>
    private string? _activeEditorServer;

    /// <summary>正在等物件總管展開節點；與開窗那一條分開記，兩件事可以同時在跑。</summary>
    private bool _selecting;
    private bool _ready;
    private bool _disposed;

    public SqlSearchBrowser(IServiceProvider services)
    {
        _services = services;
        // 清單、預覽與移至定義共用同一份目錄出處；三條路徑各問各的，症狀是指名了別台伺服器
        // 之後其中一條還在答查詢視窗那一台，而兩份看起來都很正常。欄位初始設定式跑在建構式
        // 本體之前，那時候 _services 還是 null，所以這兩個不能寫成欄位初始值。
        _catalogs = new SqlSearchCatalogs(services);
        _connection = SqlAssistChrome.CreateEditorConnectionButton(ReadEditorConnection);
        _connection.Click += (_, _) => _ = RunAsync(() => ApplyEditorConnectionAsync(automatic: false));
        _preview = new SqlSearchPreview(_catalogs, action => Run(() => RunRowAction(action)));
        foreach (var category in _providers.Aggregator.Categories) _categoryLabels[category.Id] = category.DisplayName;
        _categoryOptions = SqlSearchBrowserModel.CategoryOptions(_providers.Aggregator.Providers);
        _model.UseCategories(_categoryOptions);

        VsThemeBrushes.Apply(this);
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = SqlAssistChrome.DefaultMetrics.Body;
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        SetResourceReference(ForegroundProperty, ThemeBrush.WindowForeground);
        MinWidth = 300;

        var root = new DockPanel { Margin = new Thickness(SqlAssistChrome.Spacing.Group) };
        // 工具窗沒有原生 Titlebar，第一列直接是工具列；不另做一條看起來像第二條標題列的粗體區塊。
        // 工具列與已選條件列之間、以及整塊與清單之間的間距都由這一層給，子元素不自己帶 margin。
        var header = new SqlStack(SqlAssistChrome.Spacing.Tight)
        {
            Margin = new Thickness(0, 0, 0, SqlAssistChrome.Spacing.Group)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // 勾選以結果的去重鍵為鍵，與清單的焦點／預覽分開；動作只有複製，之後的批次動作加在這裡。
        _selection = new SqlCardSelection<SqlSearchRow, string>(_rows, row => row.Key, StringComparer.Ordinal);
        _selection.AddAction(new SqlSelectionAction(SqlIcon.Copy, CommonText.Copy, CopySelectionAsync,
            shortcutKey: Key.C, shortcutModifiers: ModifierKeys.Control));
        // 多選時選取工具列蓋在搜尋列同一格上，正好在清單上面；與 SQL Memory 同一份。
        _selectionBar = new SqlSelectionBar(_selection, CreateSearchRow()) { ReturnFocus = _list.FocusCurrentRow };
        header.Children.Add(CreateToolbar(_selectionBar.Slot));

        _list.SetRowsSource(_rows, _footer);
        _list.EnableSelection(_selection);
        _list.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 選取", UpdatePreview);
        _list.OpenRequested += (_, _) => _ = RunAsync(ActivateAsync);
        _list.RowActionRequested += action => Run(() => RunRowAction(action));
        _list.ContextMenu = CreateRowMenu();
        // 載入、空、讀不到與權限不足疊在同一塊內容上：四種「現在沒東西可看」不各占一塊版面。
        _surface = new SqlStateSurface(_list);
        // 還沒選伺服器與選的那一台連不上，下一步都是換一台，所以按鈕只有一顆：打開伺服器面板。
        // 開窗時不替使用者打開：物件總管服務第一次取用會把那個工具視窗叫出來，這一步由他發動。
        _surface.ActionRequested += (_, _) => Run(_server.Open);
        _splitView = new MasterDetailView(_surface, _preview, _preview.Summary, MasterDetailView.DefaultSideBySideWidth);
        _splitView.DetailExpandedChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 預覽", UpdatePreview);
        root.Children.Add(_splitView);
        Content = root;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SqlAssistChrome.Debounce.Search };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Search(); };
        _settleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = NewRowSettle };
        _settleTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("結束 SQL Search 進場", () =>
        {
            _settleTimer.Stop();
            foreach (var row in _rows) row.IsNew = false;
        });

        // 只攔 Ctrl+F。上一處／下一處沒有鍵盤捷徑：F3 被宿主在 WPF 看到之前就吃掉了
        // （命令路由的 pretranslate），這裡接不到，理由見 SqlMatchNavigator。
        PreviewKeyDown += (_, e) => Run(() =>
        {
            if (e.Key != Key.F || e.KeyboardDevice.Modifiers != ModifierKeys.Control) return;

            _search.Focus();
            e.Handled = true;
        });

        IsVisibleChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 可見度", () =>
        {
            if (IsVisible) OnShown();
            // 看不見的工具窗不該還佔著連線；取消之後上一份結果留在畫面上，回來時重搜。
            else CancelRequest();
        });
        ActiveSqlEditor.Changed += OnActiveEditorChanged;
        SqlEditorConnectionWatcher.Changed += OnActiveEditorChanged;

        // 記住的只有「怎麼比對」那三項；伺服器、資料庫與種類刻意不記，理由見 docs/search.md。
        if (_model.RestoreMatchState(SqlAssistState.SearchMatchState))
        {
            _segments.Value = _model.Targets;
            UpdateFilterChrome();
        }

        _ready = true;
        ObserveActiveEditor();
        ObserveCatalog(out _);
        UpdateChrome();
    }

    /// <summary>把焦點放到搜尋框；命令帶使用者過來時就是為了打字。</summary>
    public void FocusSearch() => _search.Focus();

    /// <summary>換語言重建時帶到新的一份；設下去就照一般輸入重搜。</summary>
    internal string SearchText
    {
        get => _search.Text;
        set => _search.Text = value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActiveSqlEditor.Changed -= OnActiveEditorChanged;
        SqlEditorConnectionWatcher.Changed -= OnActiveEditorChanged;
        _searchTimer.Stop();
        _settleTimer.Stop();
        _request.Cancel();
        _request.Dispose();
        _scopeDatabases.Dispose();
        _preview.Dispose();
    }

    /// <summary>
    /// 工具列下層的搜尋列：搜尋框吃滿剩餘空間，框裡是大小寫與全字，框外接排序與重新整理。
    /// </summary>
    /// <remarks>
    /// 排序與重新整理接在搜尋框右邊：兩顆作用在「這一份結果」，不是「要搜什麼」，
    /// 跟上層那些縮小範圍的篩選不是同一件事。
    /// </remarks>
    private SqlInputRow CreateSearchRow()
    {
        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, CommonText.ClearSearch);
        clear.IsEnabled = false;
        clear.Click += (_, _) => Run(() => { _search.Clear(); _search.Focus(); });
        _search.ToolTip = SqlSearchText.SearchToolTip;
        AutomationProperties.SetName(_search, SqlSearchText.SearchName);
        _search.TextChanged += (_, _) => Run(() =>
        {
            clear.IsEnabled = _search.Text.Length > 0;
            _model.Text = _search.Text;
            Changed();
        });

        // 大小寫與全字修飾的是「這個字串怎麼比」，不是搜哪裡，所以留在搜尋框裡而不是工具列上。
        // 列距由工具列決定，搜尋列自己不帶外距——兩處各留一份的症狀是兩層之間多出半列空白。
        var bar = SqlAssistChrome.CreateInputBar(SqlIcon.Search, _search, clear, _matchToggles.Buttons.ToArray());
        _matchToggles.Changed += (_, _) => Run(() =>
        {
            _model.MatchOptions = _matchToggles.Options;
            RememberMatchState();
            FiltersChanged();
        });

        ConfigureSort();
        _refresh.Click += (_, _) => Run(() =>
        {
            _providers.Invalidate();
            // 清單一起丟：剛建好的資料庫不在上一次那一份裡，而那正是使用者按重新整理的理由。
            _scopeDatabases.Invalidate();
            // 索引與定義一起丟：只丟索引的話，改過的預存程序在清單上換了位置，
            // 預覽卻還畫著改之前那一份，而畫面上看不出那個差別。
            _preview.InvalidateDefinitions();
            // 條件沒變，勾選照鍵留著；新結果套完之後才去掉已經不在的那幾筆。
            Changed(immediate: true, keepChecks: true);
        });

        return new SqlInputRow(bar, _sort, _refresh);
    }

    /// <summary>
    /// 工具列兩層：上層依序是伺服器、資料庫、種類與比對位置，下層是搜尋列那一格，貼著清單。
    /// </summary>
    /// <remarks>
    /// 比對位置是常駐的分段開關而不是下拉：它是切換最頻繁的一項，藏進下拉會多兩次點擊。
    /// 種類與資料庫反過來——十幾種物件攤成 pill 會佔掉兩列，在停靠面板裡等於少看四筆結果，
    /// 所以只在按鈕上留摘要。
    ///
    /// 上層分三群，中間由工具列補上共用的分隔線：伺服器與資料庫回答「搜哪裡」，種類回答
    /// 「搜什麼」，分段開關回答「比對哪裡」。攤成一排的話，使用者會以為種類是第三個範圍。
    ///
    /// 伺服器是單選：換一台換的是整份目錄，理由見 <see cref="SqlSearchBrowserModel.Scope"/>。
    /// </remarks>
    /// <param name="searchSlot">搜尋列與蓋在它上面的選取工具列那一格。</param>
    private FrameworkElement CreateToolbar(FrameworkElement searchSlot)
    {
        _segments.ValueChanged += (_, _) => Run(() =>
        {
            _model.Targets = _segments.Value;
            RememberMatchState();
            Changed();
        });

        ConfigureKinds();
        ConfigureDatabases();
        ConfigureServer();

        return new SqlSearchToolbar(
            searchSlot, _segments,
            new[] { new FrameworkElement[] { _connection, _server, _databases }, new FrameworkElement[] { _kinds } });
    }

    /// <summary>把比對方式交給狀態存放區。</summary>
    /// <remarks>
    /// 一改就記，不等關閉：SSMS 直接結束的那一次沒有人來得及收尾，而那正是最常見的關法。
    /// 還沒 <see cref="_ready"/> 表示這一次是還原本身，不必原樣寫回去。
    /// </remarks>
    private void RememberMatchState()
    {
        if (!_ready) return;
        SqlAssistState.SearchMatchState = _model.MatchStateToken;
    }

    /// <remarks>
    /// 排序只是同一份答案的另一種看法，所以選單換的是 <see cref="Reorder"/> 而不是重搜一輪。
    /// 按鈕的圖示跟著目前的排序走，收起文字之後它仍分得出現在排的是哪一種。
    /// </remarks>
    private void ConfigureSort()
    {
        foreach (var option in SqlSearchSortOption.All)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                Tag = option.Value,
                Icon = SqlAssistChrome.CreateIcon(SortIcon(option.Value))
            };
            item.Click += (_, _) => Run(() =>
            {
                if (_model.Sort == option.Value) return;
                _model.Sort = option.Value;
                UpdateSortButton();
                Reorder();
            });
            _sortMenu.Items.Add(item);
        }

        VsThemeBrushes.Apply(_sortMenu);

        _sort.Click += (_, _) => Run(() =>
        {
            foreach (MenuItem item in _sortMenu.Items) item.IsChecked = Equals(item.Tag, _model.Sort);
            _sortMenu.PlacementTarget = _sort;
            _sortMenu.IsOpen = true;
        });

        UpdateSortButton();
    }

    [Localizable(false)]
    private static SqlIcon SortIcon(SqlSearchSort sort) => sort switch
    {
        // 相關度是「分數由高到低」；那正是降冪。
        SqlSearchSort.Relevance => SqlIcon.SortDescending,
        SqlSearchSort.Name => SqlIcon.SortAscending,
        SqlSearchSort.Kind => SqlIcon.SortByKind,
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "沒有這個排序的圖示。")
    };

    private void UpdateSortButton()
    {
        var option = SqlSearchSortOption.For(_model.Sort);
        _sort.Content = SqlAssistChrome.CreateIcon(SortIcon(_model.Sort));
        _sort.ToolTip = SqlSearchText.SortToolTip(option.Label);
        AutomationProperties.SetName(_sort, SqlSearchText.SortToolTip(option.Label));
    }

    private void ConfigureKinds()
    {
        VsThemeBrushes.Apply(_kinds);
        // 種類沒有全選也沒有清除：全部就是第一列那個預設，而全選會送出一份與它結果相同、
        // 摘要卻完全不同的條件——使用者分不出自己現在是哪一種。
        _kinds.OptionsRequested += (_, _) => Run(FillKinds);
    }

    /// <summary>
    /// 種類下拉：照 provider 宣告的群與順序分段。
    /// </summary>
    /// <remarks>
    /// 分段的字取自 provider 的顯示字，一個 provider 只掛一次：一個來源可以切成好幾群，
    /// 但使用者要分的是「資料庫物件」與「SQL Agent 作業」這一層，同一個名字連掛兩次
    /// 只會看起來像清單重複了。只有一個 provider 有分類時整份不分段——一條標題底下就是全部，
    /// 那一列只是白佔一列。
    /// </remarks>
    private void FillKinds()
    {
        var groups = new List<SqlFilterGroup>();
        var options = new List<SqlFilterOption>();
        var group = "";
        var caption = "";
        var titled = new HashSet<string>(StringComparer.Ordinal);

        void Flush()
        {
            if (options.Count == 0) return;
            groups.Add(new SqlFilterGroup(caption, options.ToArray()));
            options.Clear();
        }

        foreach (var option in _categoryOptions)
        {
            if (options.Count != 0 && !string.Equals(option.GroupId, group, StringComparison.Ordinal)) Flush();

            if (options.Count == 0)
            {
                group = option.GroupId;
                caption = titled.Add(option.GroupLabel) ? option.GroupLabel : "";
            }

            var id = option.Id;
            options.Add(new SqlFilterOption(option.Label, "", _model.IsCategorySelected(id), selected => Run(() =>
            {
                if (!_model.SetCategorySelected(id, selected)) return;
                // 第一列那個「全部」跟著變，但不重建整份清單：使用者正在連勾好幾個。
                _kinds.SyncEmptyOption(_model.CategoryIds.Count == 0);
                FiltersChanged();
            })));
        }

        Flush();

        // 只有一段時不掛標題；那一條字底下就是整份清單，說不出任何新資訊。
        if (groups.Count == 1) groups[0] = new SqlFilterGroup("", groups[0].Items);

        // 第一列是「全部」，與按鈕摘要共用同一份字：摘要寫著「全部」而清單上一個勾都沒有時，
        // 使用者會以為自己把條件弄丟了，或以為這個下拉壞了。
        _kinds.SetEmptyOption(new SqlFilterOption(
            SqlSearchBrowserModel.AllCategoriesLabel,
            SqlSearchText.AllCategoriesDescription,
            _model.CategoryIds.Count == 0,
            selected => Run(() =>
            {
                if (!selected || !_model.ClearCategories()) return;
                FiltersChanged();
                Defer(FillKinds);
            })));

        _kinds.SetOptions(groups);
    }

    private void ConfigureDatabases()
    {
        VsThemeBrushes.Apply(_databases);
        _databases.OptionsRequested += (_, _) => _ = RunAsync(ShowDatabasesAsync);
        // 全選等清單到齊才動手：只全選手上那一份的症狀是使用者在清單還在路上時按了它，
        // 而勾起來的是幾個名稱而不是整台。面板上打了字就只勾篩出來的那幾個，而且照面板
        // 同一條比對規則再篩一次——這裡自己寫一份的下場是看到五個、勾起來七個。
        _databases.SelectAllRequested += pattern => _ = RunAsync(async () =>
        {
            var databases = await _scopeDatabases.EnsureAsync(_catalogs.Resolve());
            var changed = false;
            foreach (var database in databases)
            {
                if (!SqlFilterFlyout.Matches(database.Name, pattern)) continue;
                changed |= _model.Scope.SetDatabaseSelected(database.Name, selected: true);
            }

            FillDatabases(databases);
            if (changed) FiltersChanged();
        });
        // 全不選與第一列那個「全部」是同一件事，所以走同一條清除路徑，不另寫一份狀態同步。
        _databases.ClearAllRequested += (_, _) => Run(ClearDatabases);
        _databases.SetBulkCommands(SqlFilterBulkCommands.SelectAndClear);
    }

    /// <summary>清掉整個資料庫維度；第一列那個預設與全不選共用這一份。</summary>
    private void ClearDatabases()
    {
        if (!_model.Scope.ClearDatabases()) return;
        FiltersChanged();
        // 其餘幾列的勾要一起清掉，但重建整份清單得等這一次的繫結回寫結束：
        // 在回寫途中換掉 ItemsSource 等於把正在發事件的那一顆核取方塊回收掉。
        Defer(() => FillDatabases(_scopeDatabases.Items));
    }

    /// <summary>
    /// 面板打開了：先畫手上有的，清單還沒到就一邊說一句一邊去問。
    /// </summary>
    /// <remarks>
    /// 展開下拉<b>就是</b>使用者在要求這份清單，所以這裡去問資料庫是對的；禁止的是在沒有人
    /// 打開它的時候先問一輪。問一次留一份，換連線與按重新整理才重問，規則在
    /// <see cref="SqlSearchScopeDatabases"/>。
    /// </remarks>
    private async Task ShowDatabasesAsync()
    {
        var catalog = _catalogs.Resolve();

        // 先畫：已經勾起來的條件一定要看得見，否則使用者在等清單的期間取消不掉自己剛選的那一個。
        FillDatabases(_scopeDatabases.Items);
        if (catalog is null || _scopeDatabases.IsLoaded) return;

        _databases.SetNotice(SqlSearchText.LoadingDatabases, busy: true);
        FillDatabases(await _scopeDatabases.EnsureAsync(catalog));
    }

    /// <summary>
    /// 資料庫下拉的兩段：使用者資料庫與系統資料庫。
    /// </summary>
    /// <remarks>
    /// 已經勾起來的名稱排在最前面且一律列出，就算這一份清單裡沒有它：伺服器回不來或名稱剛被
    /// 卸除時，使用者仍然要取消得掉自己剛選的條件。分段依伺服器說的
    /// <see cref="SqlCatalogSearchDatabase.IsSystem"/>，只有還沒問到清單的名稱才退回那份四個
    /// 名字的後備名單。
    /// </remarks>
    private void FillDatabases(IReadOnlyList<SqlCatalogSearchDatabase> databases)
    {
        var user = new List<SqlFilterOption>();
        var system = new List<SqlFilterOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string database, bool isSystem)
        {
            if (!seen.Add(database)) return;
            var option = new SqlFilterOption(
                database,
                SqlSearchText.DatabaseOptionDescription,
                _model.Scope.IsDatabaseSelected(database),
                selected => Run(() =>
                {
                    if (!_model.Scope.SetDatabaseSelected(database, selected)) return;
                    _databases.SyncEmptyOption(_model.Scope.Databases.Count == 0);
                    FiltersChanged();
                }));
            (isSystem ? system : user).Add(option);
        }

        foreach (var database in _model.Scope.Databases) Add(database, SqlSearchBrowserModel.IsSystemDatabase(database));
        foreach (var database in databases) Add(database.Name, database.IsSystem);

        // 第一列是「全部」，與按鈕摘要共用同一份字；選它等於清掉整個維度，
        // 所以面板上不另畫一顆「清除」。
        _databases.SetEmptyOption(new SqlFilterOption(
            SqlSearchBrowserModel.AllDatabasesLabel,
            SqlSearchText.AllDatabasesDescription,
            _model.Scope.Databases.Count == 0,
            // 取消勾它不是一個範圍；面板那一列自己會彈回去，這裡只忽略。
            selected => Run(() => { if (selected) ClearDatabases(); })));

        _databases.SetOptions(new[]
        {
            new SqlFilterGroup(SqlSearchText.UserDatabases, user),
            new SqlFilterGroup(SqlSearchText.SystemDatabases, system)
        });
        _databases.SetNotice(DatabaseNotice(seen.Count));
    }

    /// <summary>
    /// 清單上方那一句；沒有話要說時是空字串。
    /// </summary>
    /// <remarks>
    /// 「問不到」與「這台上一個都進不去」要分開說：前者叫使用者重試或去看權限，後者是答案本身。
    /// 混成一句的症狀是他反覆重開下拉，等一份永遠不會出現的清單。
    /// </remarks>
    private string DatabaseNotice(int listed)
    {
        if (_scopeDatabases.IsUnavailable)
        {
            return listed == 0
                ? SqlSearchText.DatabaseListUnavailable
                : SqlSearchText.DatabaseListStale;
        }

        if (!_scopeDatabases.IsLoaded) return "";
        return listed == 0 ? SqlSearchText.NoAccessibleDatabases : "";
    }

    /// <summary>
    /// 伺服器單選：目前連得上的每一台——物件總管上已連線的，加上查詢視窗連著的那一台。
    /// </summary>
    /// <remarks>
    /// 清單只在使用者打開下拉那一刻重問（沒有 I/O，見 <see cref="SsmsObjectExplorer"/>）。
    /// <b>禁止</b>改成輪詢：物件總管服務第一次取用會把那個工具視窗叫出來，
    /// 使用者把它關掉之後，輪詢會在他沒有要求的時候替他開回去。
    /// </remarks>
    private void ConfigureServer()
    {
        VsThemeBrushes.Apply(_server);
        _server.OptionsRequested += (_, _) => Run(FillServer);
    }

    /// <summary>
    /// 伺服器下拉：一台一行，沒有「查詢視窗」這種會跟著分頁變的選項。
    /// </summary>
    /// <remarks>
    /// 查詢視窗連著的那一台不在物件總管上時也列出來：套用按鈕套得到的，面板上就要選得到。
    /// 同一台不列兩次，比對走連線字串裡的伺服器名稱（<see cref="SqlSearchCatalogs.IsSameServer(string?, string?)"/>），
    /// 不是快取鍵——快取鍵是整串正規化過的連線字串，同一台伺服器的兩條連線幾乎不會相等。
    /// 目前選的那一台（從查詢視窗套用、而那個分頁已經換掉）兩處都沒有時照樣列出來，
    /// 否則面板上看不出現在搜的是哪一台。
    ///
    /// 問不到物件總管時<b>明說</b>，不假裝這就是全部：少列一台而使用者看不出差別，
    /// 比只列一台更糟。
    /// </remarks>
    private void FillServer()
    {
        var servers = _catalogs.ListServers();

        // 選的那一台已經從物件總管上消失了（使用者中斷了連線）：回到還沒選，
        // 而不是留著一個連不上的範圍讓每一輪都空手而回。
        if (_catalogs.DropMissingServer(servers)) ServerChanged();

        var editorServer = _catalogs.ActiveEditorServerName();
        var listed = new List<string?>();
        var options = new List<SqlFilterOption>();

        void Add(string label, string? serverName, Func<bool> isSelected, string hint, Action select)
        {
            listed.Add(serverName);
            // 單選：勾掉等於沒有範圍可搜，所以只理會「選這一個」。
            options.Add(new SqlFilterOption(label, hint, isSelected(),
                value => Run(() => { if (value && !isSelected()) select(); })));
        }

        foreach (var server in servers ?? Array.Empty<SsmsObjectExplorerServer>())
        {
            Add(server.DisplayName, server.ServerName, () => _catalogs.IsSelected(server),
                SqlSearchText.ExplorerServerDescription, () => SelectServer(server));
        }

        if (editorServer is not null && !listed.Exists(name => SqlSearchCatalogs.IsSameServer(name, editorServer)))
        {
            Add(editorServer, editorServer, () => _catalogs.IsSelected(editorServer),
                SqlSearchText.EditorServerDescription, () => _ = RunAsync(SelectEditorServerAsync));
        }

        if (_catalogs.ServerName is { } current && !listed.Exists(_catalogs.IsSelected))
        {
            Add(current, current, () => true, SqlSearchText.CurrentServerDescription, () => { });
        }

        _server.SetOptions(new[] { new SqlFilterGroup("", options) });
        _server.SetNotice(servers is null ? SqlSearchText.ExplorerUnavailable : "");
    }

    /// <summary>查詢視窗現在的連線；沒有視窗或沒有連線時為 null。只給套用按鈕的 Tooltip 用。</summary>
    private SqlConnectionLabel? ReadEditorConnection() =>
        SqlAssistPlatformGuard.Probe("取得查詢視窗連線", () => SqlWindowConnections.ReadActive(_services), null);

    /// <summary>
    /// 套用查詢視窗的連線：伺服器換成它連著的那一台，資料庫換成它連著的那一個。
    /// </summary>
    /// <param name="automatic">
    /// 還沒選伺服器時自動補的那一次：查詢視窗沒有連線就安靜地什麼都不做，而且等的途中使用者
    /// 自己挑了一台就不蓋掉他。
    /// </param>
    /// <remarks>
    /// 與 SQL Memory 那一顆同一條規則（<see cref="SqlConnectionScope.Apply"/>）：取代而不是加進去，
    /// 之後切分頁、換連線都不再跟著走。用的是查詢視窗<b>那條連線</b>（<see cref="SqlSearchCatalogs.ReadActiveEditorAsync"/>），
    /// 所以物件總管上沒有的那一台也搜得到。查詢視窗沒有連線時<b>不動</b>範圍。
    /// </remarks>
    private async Task ApplyEditorConnectionAsync(bool automatic)
    {
        var connection = await _catalogs.ReadActiveEditorAsync().ConfigureAwait(true);
        if (_disposed || (automatic && _catalogs.ServerName is not null)) return;

        var label = connection is null
            ? null
            : new SqlConnectionLabel(connection.Origin.ServerName, connection.Catalog.ConnectionSource.DatabaseName);

        if (connection is null || !_model.Scope.Apply(label))
        {
            if (!automatic) NotifyNotConnected();
            return;
        }

        _catalogs.Select(connection);
        ScopeChanged();
    }

    /// <summary>按了套用、查詢視窗卻沒有連線；範圍沒動，說一句為什麼沒反應。</summary>
    private static void NotifyNotConnected() =>
        Notify(NotificationCatalog.ApplyingEditorConnection, NotificationStatus.Failed,
            message: SqlEditorConnectionText.NotConnectedReport);

    /// <summary>從伺服器下拉選查詢視窗連著的那一台（它不在物件總管上）；資料庫回到「全部」。</summary>
    private async Task SelectEditorServerAsync()
    {
        if (await _catalogs.ReadActiveEditorAsync().ConfigureAwait(true) is not { } connection)
        {
            NotifyNotConnected();
            return;
        }

        if (!_disposed && _catalogs.Select(connection)) ServerChanged();
    }

    /// <summary>選物件總管上的一台；資料庫回到「全部」。</summary>
    /// <remarks>
    /// 索引<b>不</b>丟：它照連線的快取鍵存，換回來時原本那一份還在。
    /// </remarks>
    private void SelectServer(SsmsObjectExplorerServer server)
    {
        if (_catalogs.Select(server)) ServerChanged();
    }

    /// <summary>
    /// 選定的伺服器換了（或放掉了）：模型那一份跟著換，資料庫的勾選由它一起清掉，再重搜。
    /// </summary>
    /// <remarks>
    /// 名稱照 <see cref="SqlSearchCatalogs.ServerName"/> 寫回模型：兩份各自決定的話，摘要說的
    /// 與實際搜的會是兩台。
    /// </remarks>
    private void ServerChanged()
    {
        if (_catalogs.ServerName is { } name) _model.Scope.SetServerSelected(name, selected: true);
        else _model.Scope.ClearServers();

        ScopeChanged();
        // 選的那一台放掉了（從物件總管中斷）：與開窗時同一條規則，還沒選就自動套用查詢視窗那一條。
        if (_catalogs.ServerName is null) _ = RunAsync(() => ApplyEditorConnectionAsync(automatic: true));
    }

    /// <summary>範圍換了：新的目錄交給 provider，上一台的清單與資料庫面板一起換掉，再重搜一輪。</summary>
    private void ScopeChanged()
    {
        if (_disposed) return;

        // 上一台的清單不代表這一台了。留到新結果回來才換的話，這一台還在建索引的那幾秒，
        // 摘要寫著這一台，下面列的卻是上一台的物件。
        if (ObserveCatalog(out _)) ClearRows();
        // 勾選的重建要等這一輪事件走完：面板的繫結可能還在回寫。伺服器面板只在開著時重畫——
        // 列伺服器會取用物件總管服務，沒有人在看的時候不去叫它。
        Defer(() => FillDatabases(_scopeDatabases.Items));
        if (_server.IsOpen) Defer(FillServer);
        Changed(immediate: true);
    }

    /// <summary>
    /// 作用中的查詢視窗換了（切分頁，或同一個分頁裡換連線、換資料庫、中斷）。
    /// </summary>
    /// <remarks>
    /// <b>不動範圍</b>：伺服器與資料庫是使用者明確選的，只有套用按鈕會換成查詢視窗那一條。
    /// 這裡只做兩件事——每一列「移至定義會不會開未連線的視窗」重算，以及還沒選伺服器時自動套用一次。
    /// </remarks>
    private void OnActiveEditorChanged(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("排入 SQL Search 查詢視窗更新", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                _ = SqlAssistPlatformGuard.RunAsync("更新 SQL Search 查詢視窗", ObserveActiveEditorAsync, fallback: false))));

    private async Task<bool> ObserveActiveEditorAsync()
    {
        if (_disposed) return false;

        ObserveActiveEditor();
        if (IsVisible && _catalogs.ServerName is null) await ApplyEditorConnectionAsync(automatic: true).ConfigureAwait(true);
        return true;
    }

    /// <summary>重讀作用中查詢視窗連著哪一台，並讓清單上既有的列照它重算。</summary>
    private void ObserveActiveEditor()
    {
        _activeEditorServer = _catalogs.ActiveEditorServerName();
        foreach (var row in _rows) row.ObserveActiveEditor(_activeEditorServer);
    }

    /// <summary>工具窗重新出現：同一組條件重搜；還沒選伺服器就先自動套用查詢視窗那一條。</summary>
    private void OnShown()
    {
        ObserveActiveEditor();
        ObserveCatalog(out _);
        Changed(immediate: true, keepChecks: true);
        if (_catalogs.ServerName is null) _ = RunAsync(() => ApplyEditorConnectionAsync(automatic: true));
    }

    /// <summary>
    /// 把選定那一台的目錄交給 provider，並同步跟著它走的狀態。
    /// </summary>
    /// <remarks>
    /// 換範圍與每一輪搜尋前都走這一支：物件總管那一台的目錄在第一次解析時才建，連不上的那一次
    /// 下一輪會再試。
    /// </remarks>
    /// <returns>範圍換到另一個目錄或另一台時為 true。</returns>
    private bool ObserveCatalog(out SqlMetadataCatalog? catalog)
    {
        // 目錄與伺服器一次交出，命中帶的才是這份目錄那一台；說不出是哪一台就不搜。
        var connection = _catalogs.ResolveConnection();
        catalog = connection?.Catalog;
        var moved = _providers.UseConnection(connection);
        _model.HasConnection = _providers.HasConnection;
        // 清單快取跟著連線走，而且在這裡就同步：展開下拉時才比對的話，換一台之後
        // IsLoaded 仍是上一台的 true，下拉會一直畫著上一台的資料庫。
        _scopeDatabases.SyncTo(catalog);
        // 伺服器下拉一律可按：連不上目前這一台時，換一台正是使用者要做的事。
        _databases.IsEnabled = catalog is not null;
        return moved;
    }

    /// <summary>篩選改變：更新摘要，然後重跑一輪。</summary>
    private void FiltersChanged()
    {
        UpdateFilterChrome();
        Changed();
    }

    private void UpdateFilterChrome()
    {
        // 寫回不發 Changed（見 SqlMatchToggles），所以不會再跑一輪、再寫回來一次。
        _matchToggles.Options = _model.MatchOptions;

        _kinds.UpdateSummary(_model.CategorySummary(), Join(_model.CategoryIds.Select(Label)));
        _databases.UpdateSummary(_model.DatabaseSummary(), Join(_model.Scope.Databases));
        _server.UpdateSummary(_model.ServerSummary(), "");

        // 選了值的維度換強調底框；窄窗收掉摘要之後，看得出哪幾顆有作用靠的就是它。
        // 伺服器必選，選定之後一樣亮，否則一排按鈕裡只有每一輪都在用的那一台看起來像還沒設定。
        _server.HasSelection = _model.Scope.Servers.Count != 0;
        _kinds.HasSelection = _model.CategoryIds.Count != 0;
        _databases.HasSelection = _model.Scope.Databases.Count != 0;
    }

    private string Label(string categoryId) =>
        _categoryLabels.TryGetValue(categoryId, out var label) ? label : categoryId;

    private static string Join(IEnumerable<string> values) => string.Join(CommonText.ListSeparator, values);

    /// <summary>輸入、範圍或選項改變：作廢這一輪，但<b>不清空清單</b>，等新結果回來才換。</summary>
    /// <param name="keepChecks">
    /// 條件沒變、只是同一組條件重跑（重新整理、工具窗重新出現）：勾選照鍵留著，新結果套完再去掉
    /// 已經不在的那幾筆。條件變了就清空：留著的勾可能不在新的結果裡，筆數會對不上畫面。
    /// </param>
    private void Changed(bool immediate = false, bool keepChecks = false)
    {
        if (!_ready || _disposed) return;
        if (!keepChecks) _selection.Clear();
        CancelRequest();
        _model.Invalidate();
        UpdateChrome();
        _searchTimer.Stop();
        if (immediate) Search();
        else _searchTimer.Start();
    }

    private void CancelRequest()
    {
        _searchTimer.Stop();
        _settleTimer.Stop();
        _request.Cancel();
        _request.Dispose();
        _request = new CancellationTokenSource();
    }

    private void Search() => _ = RunAsync(SearchAsync);

    private async Task SearchAsync()
    {
        if (_disposed || !IsVisible) return;

        var token = _request.Token;

        // 只更新目錄，不重跑這一輪：物件總管那一台上一次連不上的話，這一輪再試一次。
        ObserveCatalog(out _);
        var known = _scopeDatabases.Items.Select(database => database.Name).ToArray();
        if (_model.Begin(_providers.IsIndexed(_model.ToSearchScope(), known)) is not { } round)
        {
            // 這一輪不會有新結果來換掉舊的（清空了搜尋框或斷了線）；留著上一份等於拿過期的
            // 清單冒充目前條件的答案。
            ClearRows();
            UpdateChrome();
            return;
        }

        UpdateChrome();

        try
        {
            // 背景執行緒跑整輪；UI 執行緒不同步等待，第一次建索引可能要數秒。
            var results = await Task.Run(() => _providers.Aggregator.SearchAsync(round.Query, token), token);
            if (token.IsCancellationRequested || !_model.Accept(round, results)) return;
            Apply(round, _model.Arrange(results.Hits));
        }
        catch (OperationCanceledException)
        {
            // 取消是打字驅動搜尋的正常流程，不是失敗。
        }
        catch (Exception error)
        {
            SqlAssistDiagnostics.WriteAlways("SQL Search 失敗：" + error.Message);
            _model.Fail(round, SqlSearchText.SearchFailed(error.Message));
        }
        finally
        {
            _model.End(round);
            if (_model.IsCurrent(round)) NotifyFailure();
            UpdateChrome();
        }
    }

    /// <summary>
    /// 這一輪有失敗（整輪或某個來源）就送一則通知；同一句只送一次，換了一句或好了才重新算。
    /// </summary>
    /// <remarks>
    /// 失敗的那一句也寫在頁尾或狀態表面上（說明畫面），通知另外留一份進「通知失敗」可以回看。
    /// 每一輪都送的話，同一個讀不到的來源會在使用者每打一個字時跳出一次。
    /// </remarks>
    private void NotifyFailure()
    {
        var failure = _model.Failure;
        if (string.Equals(failure, _notifiedFailure, StringComparison.Ordinal)) return;
        _notifiedFailure = failure;
        if (failure.Length != 0) Notify(NotificationCatalog.SearchingDatabaseObjects, NotificationStatus.Failed, message: failure);
    }

    /// <summary>
    /// 換上新結果。
    /// </summary>
    /// <remarks>
    /// 到這裡才清空：先清再等結果的話，每打一個字清單都會閃一次空白，而上一份其實還讀得懂。
    /// 之後分批附加，一次幾十列，讓虛擬化面板有機會分攤版面重算。
    /// </remarks>
    private void Apply(SqlSearchRound round, IReadOnlyList<SearchHit> hits)
    {
        _model.RememberSelection((_list.SelectedItem as SqlSearchRow)?.Key);
        _settleTimer.Stop();
        _rows.Clear();
        _round = round;
        _applying = hits;
        _applied = 0;
        AppendBatch(round);
    }

    /// <summary>
    /// 換一種排序：手上的結果重排，<b>不重跑一輪</b>。
    /// </summary>
    /// <remarks>
    /// 排序只是同一份答案的另一種看法。重搜一次的代價是再掃一遍資料庫，而使用者只是想
    /// 先看名稱 A–Z；沒有結果可以重排時什麼都不做，不假裝按了有反應。
    /// </remarks>
    private void Reorder()
    {
        if (_round is not { } round || _applying.Count == 0) return;
        _model.RememberSelection((_list.SelectedItem as SqlSearchRow)?.Key);
        var ordered = _model.Arrange(_applying);
        _settleTimer.Stop();
        _rows.Clear();
        _applying = ordered;
        _applied = 0;
        // 重排不播進場動畫：那是「這幾列是新的」的訊號，而這一批列與上一秒的是同一份。
        AppendBatch(round, motion: false);
    }

    private void ClearRows()
    {
        _selection.Clear();
        _settleTimer.Stop();
        _round = null;
        _applying = Array.Empty<SearchHit>();
        _applied = 0;
        if (_rows.Count != 0) _rows.Clear();
        _preview.Select(null);
    }

    private void AppendBatch(SqlSearchRound round, bool? motion = null)
    {
        if (_disposed || !_model.IsCurrent(round)) return;

        var animate = motion ?? SqlAssistChrome.MotionEnabled;

        if (!AppendRows(animate))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                SqlAssistPlatformGuard.Run("附加 SQL Search 結果", () => AppendBatch(round, animate))));
        }
    }

    /// <returns>true 表示這一份已經全部套完。</returns>
    private bool AppendRows(bool motion)
    {
        var end = Math.Min(_applied + RowBatch, _applying.Count);

        for (; _applied < end; _applied++)
        {
            var hit = _applying[_applied];
            var label = _categoryLabels.TryGetValue(hit.CategoryId, out var text) ? text : hit.CategoryId;
            _rows.Add(new SqlSearchRow(hit, label, _activeEditorServer) { IsNew = motion });
        }

        // 還在分批套上時算「還有沒載入的」：這時全選的是整份答案，後面幾批進來照樣勾著。
        _selection.HasMore = _applied < _applying.Count;
        if (_applied < _applying.Count) return false;

        // 同一組條件重跑的結果套完了：勾選只留仍在這一份裡的。
        _selection.RetainLoaded();

        // 每一批都重新計時：直接 Start 對已經在跑的計時器不重新計時，第二批之後的新列會在
        // 第一批那一輪到期時被一起清掉 IsNew，進場動畫播到一半停住。
        if (motion && _rows.Count > 0) { _settleTimer.Stop(); _settleTimer.Start(); }
        if (_model.ResolveSelection(_rows.Select(row => row.Key).ToArray(), _list.SelectedItem is not null) is { } index)
        {
            _list.SelectedIndex = index;
        }

        UpdateChrome();
        return true;
    }

    /// <summary>單筆的「複製限定名稱」；剪貼簿被鎖住時與批次複製同一句回報。</summary>
    private static async Task CopyNameAsync(SqlSearchRow? row)
    {
        if (row is null) return;
        var failure = await SqlClipboard.WriteTextAsync(row.QualifiedName).ConfigureAwait(true);
        Notify(NotificationCatalog.CopyingQualifiedName, failure is null ? NotificationStatus.Succeeded : NotificationStatus.Failed,
            row.QualifiedName, failure ?? "");
    }

    /// <summary>
    /// 選取工具列的「複製」（多選模式中的 Ctrl+C 也是它）：TSV 與 HTML 同時放上剪貼簿，順序照清單。
    /// </summary>
    /// <remarks>
    /// 這一輪的答案已經整份在手上，所以沒有 SQL Memory 那種背景讀取：全選時還沒分批套上的那幾批
    /// 先補完再複製。只讀列上已有的資料，不讀定義本文。結果與 SQL Memory 的批次複製同一則通知；
    /// 例外由 <see cref="RunAsync"/> 送通知，所以這一支不擲出例外——工具列的點擊接不住它。
    /// </remarks>
    private async Task<bool> CopySelectionAsync()
    {
        var succeeded = false;
        await RunAsync(async () =>
        {
            if (_selection.IsAllMatching && _selection.HasMore && _round is { } round && _model.IsCurrent(round))
            {
                while (!AppendRows(motion: false)) { }
            }

            var content = SqlTabularText.Build(SqlSearchRow.CopyColumns, _selection.CheckedRows());
            var failure = content.RowCount == 0
                ? SqlClipboard.EmptyMessage
                : await SqlClipboard.WriteAsync(SqlClipboard.CreateDataObject(content)).ConfigureAwait(true);
            Notify(NotificationCatalog.CopyingSqlList, failure is null ? NotificationStatus.Succeeded : NotificationStatus.Failed,
                message: failure ?? SqlClipboard.CopiedNote(content.RowCount));
            succeeded = failure is null;
        }).ConfigureAwait(true);
        return succeeded;
    }

    [Localizable(false)]
    private void RunRowAction(SqlSearchRowAction action)
    {
        switch (action)
        {
            case SqlSearchRowAction.Activate:
                _ = RunAsync(ActivateAsync);
                return;
            case SqlSearchRowAction.SelectInExplorer:
                _ = RunAsync(SelectInExplorerAsync);
                return;
            case SqlSearchRowAction.Copy:
                _ = RunAsync(() => CopyNameAsync(_list.SelectedItem as SqlSearchRow));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "沒有這個結果列操作。");
        }
    }

    private void UpdatePreview()
    {
        _preview.Select(_splitView.IsDetailExpanded ? _list.SelectedItem as SqlSearchRow : null);
        UpdateChrome();
    }

    /// <summary>雙擊、Enter、預覽或右鍵選單的「移至定義」：把這一筆的定義開進新的查詢視窗。</summary>
    /// <remarks>
    /// 酬載辨識、導航與結果的通知都在 <see cref="SqlSearchActivation"/>；這裡只負責選了哪一列，
    /// 以及一次只開一個。做不到的那一種（沒有可開的東西）也由它說，雙擊之後才不會毫無動靜。
    /// </remarks>
    private async Task ActivateAsync()
    {
        if (_list.SelectedItem is not SqlSearchRow row) return;

        // 一次只開一個視窗。查詢加開窗要好幾秒，而那幾秒裡清單照樣可以再雙擊一次；
        // 沒有這一道就是連點兩下開出兩個查詢視窗（F12 那一條由 SqlDefinitionOpener 自己擋）。
        if (_activating) return;
        _activating = true;

        try
        {
            await SqlSearchActivation.ActivateAsync(row.Hit, _services, _catalogs);
        }
        finally
        {
            _activating = false;
        }
    }

    /// <summary>右鍵選單或列上那一顆的「在物件總管中選取」。</summary>
    /// <remarks>
    /// 與 <see cref="ActivateAsync"/> 各寫一支而不是共用一個帶參數的版本：可用判斷、
    /// 進行中那句話與每一種失敗都不一樣，合起來就是一連串 if，而那正是加第三個動作時
    /// 最難改的形狀。辨識酬載與導航都在 <see cref="SqlSearchActivation"/>。
    /// </remarks>
    private async Task SelectInExplorerAsync()
    {
        if (_list.SelectedItem is not SqlSearchRow row) return;

        if (_selecting) return;
        _selecting = true;

        try
        {
            // 展開節點要向伺服器問資料，大的資料庫上是好幾秒；進行中與結果由那一則通知說。
            await SqlSearchActivation.SelectInExplorerAsync(row.Hit, _services, _catalogs);
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>
    /// 結果列的右鍵選單。
    /// </summary>
    /// <remarks>
    /// 與列上停駐才出現的動作列同一份 <see cref="SqlSearchRowCommand.All"/>，順序也相同：
    /// 兩處各寫一次的下場是同一批操作在卡片與快捷選單對不起來。結構預覽<b>不</b>在裡面——
    /// 它要的是編輯器文字裡的一段錨點，工具窗的一列結果沒有那個東西，理由見
    /// <see cref="SqlSearchActivation"/>。
    /// </remarks>
    private ContextMenu CreateRowMenu()
    {
        var menu = new ContextMenu();
        var items = new List<(SqlSearchRowAction Action, MenuItem Item)>();

        foreach (var command in SqlSearchRowCommand.All)
        {
            var item = new MenuItem { Header = command.Label, Icon = SqlAssistChrome.CreateIcon(command.Icon) };
            var action = command.Action;
            item.Click += (_, _) => Run(() => RunRowAction(action));
            items.Add((action, item));
            menu.Items.Add(item);
        }

        VsThemeBrushes.Apply(menu);

        menu.Opened += (_, _) => SqlAssistPlatformGuard.Run("更新 SQL Search 快捷選單", () =>
        {
            var row = _list.SelectedItem as SqlSearchRow;

            foreach (var (action, item) in items)
            {
                item.IsEnabled = row is not null && IsAvailable(action, row);
                item.Header = row is not null && LabelOf(action, row) is { } label
                    ? label
                    : SqlSearchRowCommand.For(action).Label;

                // 空字串會畫成一個空的提示框；沒有描述就整個不掛。
                item.ToolTip = row is not null && DescribeAction(action, row) is { Length: > 0 } description
                    ? description
                    : null;
            }
        });

        return menu;
    }

    /// <summary>這一列做不做得到這個操作；做不到的不讓它亮著。</summary>
    /// <remarks>
    /// 答案在 <see cref="SqlSearchRow"/> 上算過一次，這裡只把操作對到屬性：列上那顆按鈕
    /// 讀的是同一個值（繫結走 <c>SqlSearchRowCommand.AvailabilityPath</c>），
    /// 兩處各算一次的症狀是選單變灰而停駐時的同一顆按得下去。
    /// </remarks>
    private static bool IsAvailable(SqlSearchRowAction action, SqlSearchRow row) => action switch
    {
        SqlSearchRowAction.Activate => row.CanActivate,
        SqlSearchRowAction.SelectInExplorer => row.CanSelectInExplorer,
        _ => true
    };

    /// <summary>名稱隨列而變的操作在這一列叫什麼；固定名稱的操作為 null。</summary>
    /// <remarks>讀的是列上算好的那一個值，與停駐那一顆的繫結（<c>SqlSearchRowCommand.LabelPath</c>）同一份。</remarks>
    private static string? LabelOf(SqlSearchRowAction action, SqlSearchRow row) => action switch
    {
        SqlSearchRowAction.Activate => row.ActivateLabel,
        _ => null
    };

    /// <summary>這個操作會做什麼；沒有話可說時是空字串，呼叫端據此不掛提示框。</summary>
    private static string DescribeAction(SqlSearchRowAction action, SqlSearchRow row) => action switch
    {
        SqlSearchRowAction.Activate => row.ActivateDescription,
        SqlSearchRowAction.SelectInExplorer => SqlSearchActivation.DescribeSelectInExplorer(row.Hit),
        _ => ""
    };

    private void UpdateChrome()
    {
        if (_disposed) return;
        _surface.State = _model.Surface(_rows.Count);
        _footer.Update(_model.Footer(_rows.Count));
        UpdateFilterChrome();
    }

    /// <summary>
    /// SQL Search 上按下去的結果；種類與來源寫死：這幾件事一律是使用者在這個工具窗上按的。
    /// </summary>
    /// <remarks>
    /// 移至定義與在物件總管中選取不走這裡：它們與 F12 同一種（<see cref="NotificationKind.Navigation"/>），
    /// 由 <see cref="SqlSearchActivation"/> 自己送。預覽的那幾件也走這一支，兩處不各寫一份三軸。
    /// </remarks>
    internal static void Notify(string title, NotificationStatus status, string subject = "", string message = "") =>
        NotificationCenter.Default.Post(title, NotificationKind.Search, NotificationOrigin.User, NotificationLevel.Info,
            status, subject, message: message);

    // 使用者主動觸發的失敗要看得見，所以這裡不是 SqlAssistPlatformGuard 而是送通知。
    private void Run(Action action) => _ = RunAsync(() => { action(); return Task.CompletedTask; });

    /// <summary>等這一輪事件走完再做；失敗仍然送通知。</summary>
    /// <remarks>
    /// 用在「要換掉正在發事件的那個控制項」的場合：面板重建會回收核取方塊，而它的
    /// <c>IsChecked</c> 回寫還在堆疊上。收掉的視窗不補做——那時候畫面已經沒有人在看。
    /// </remarks>
    private void Defer(Action action) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed) return;
            Run(action);
        }));

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SqlAssistDiagnostics.WriteAlways("SQL Search 操作失敗：" + error.Message);
            if (!_disposed) Notify(NotificationCatalog.RunningSearchAction, NotificationStatus.Failed, message: error.Message);
        }
    }
}
