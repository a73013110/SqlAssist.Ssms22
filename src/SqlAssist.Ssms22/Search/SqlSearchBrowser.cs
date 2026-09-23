using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.Diagnostics;
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
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly SqlMatchToggles _matchToggles = new();
    private readonly Button _connection;
    private readonly SqlSearchSegments _segments = new();
    private readonly SqlFilterFlyout _server = new("伺服器", SqlIcon.Server, SqlFilterMode.Single);
    private readonly SqlFilterFlyout _databases = new("資料庫", SqlIcon.Database, SqlFilterMode.SearchableMultiple);
    private readonly SqlFilterFlyout _kinds = new("種類", SqlIcon.Filter);
    private readonly SqlCardSelection<SqlSearchRow, string> _selection;
    private readonly SqlSelectionBar _selectionBar;
    private readonly Button _sort = SqlAssistChrome.CreateIconButton(SqlIcon.SortDescending, "排序");
    private readonly Button _refresh = SqlAssistChrome.CreateIconButton(
        SqlIcon.Refresh, "重新整理：丟掉已建立的索引並重新搜尋；改過結構之後用它。");
    private readonly ContextMenu _sortMenu = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _settleTimer;
    private CancellationTokenSource _request = new();
    private IReadOnlyList<SearchHit> _applying = Array.Empty<SearchHit>();
    private int _applied;
    private string _statusTone = "";

    private SqlSearchRound? _round;

    private bool _activating;

    /// <summary>
    /// 作用中查詢視窗連著哪一台；每一列「會不會開未連線的視窗」照它算。
    /// </summary>
    /// <remarks>
    /// 在範圍或查詢視窗換過的那一刻（<see cref="ObserveConnection"/>）問一次，新列建立時直接拿來用。
    /// 每一列、每畫一次各問一次宿主的話，一批一百列就是一百次連線查詢。
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
        _connection.Click += (_, _) => Run(UseEditorConnection);
        _preview = new SqlSearchPreview(_catalogs);
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
        _selection.AddAction(new SqlSelectionAction(SqlIcon.Copy, "複製", CopySelectionAsync,
            shortcutKey: Key.C, shortcutModifiers: ModifierKeys.Control));
        // 多選時選取工具列蓋在搜尋列同一格上，正好在清單上面；與 SQL Memory 同一份。
        _selectionBar = new SqlSelectionBar(_selection, CreateSearchRow()) { ReturnFocus = _list.FocusCurrentRow };
        header.Children.Add(CreateToolbar(_selectionBar.Slot));

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Visibility = Visibility.Collapsed;
        _status.Margin = new Thickness(0, SqlAssistChrome.Spacing.Group, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _list.SetRowsSource(_rows);
        _list.EnableSelection(_selection);
        _list.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 選取", UpdatePreview);
        _list.OpenRequested += (_, _) => _ = RunAsync(ActivateAsync);
        _list.RowActionRequested += action => Run(() => RunRowAction(action));
        _list.ContextMenu = CreateRowMenu();
        // 載入、空、讀不到與權限不足疊在同一塊內容上：四種「現在沒東西可看」不各占一塊版面。
        _surface = new SqlStateSurface(_list);
        // 兩種沒有連線的狀態互斥，所以按鈕只有一顆：指名的那一台連不上就回到查詢視窗，
        // 否則去物件總管挑一台。判斷條件與 SqlSearchBrowserModel.Surface 的那一條相同。
        _surface.ActionRequested += (_, _) => Run(() =>
        {
            if (_model.Server is { Length: > 0 }) SelectServer(null);
            else PickServerFromExplorer();
        });
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
            if (IsVisible) ObserveConnection(reload: true);
            // 看不見的工具窗不該還佔著連線；取消之後上一份結果留在畫面上，回來時重搜。
            else CancelRequest();
        });
        ActiveSqlEditor.Changed += OnConnectionContextChanged;
        SqlEditorConnectionWatcher.Changed += OnConnectionContextChanged;

        // 記住的只有「怎麼比對」那三項；伺服器、資料庫與種類刻意不記，理由見 docs/search.md。
        if (_model.RestoreMatchState(SqlAssistState.SearchMatchState))
        {
            _segments.Value = _model.Targets;
            UpdateFilterChrome();
        }

        _ready = true;
        ObserveConnection(reload: false);
    }

    /// <summary>把焦點放到搜尋框；命令帶使用者過來時就是為了打字。</summary>
    public void FocusSearch() => _search.Focus();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActiveSqlEditor.Changed -= OnConnectionContextChanged;
        SqlEditorConnectionWatcher.Changed -= OnConnectionContextChanged;
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
        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋");
        clear.IsEnabled = false;
        clear.Click += (_, _) => Run(() => { _search.Clear(); _search.Focus(); });
        _search.ToolTip = "搜尋物件名稱、資料行與定義本文；名稱走模糊比對，" +
            "本文是字面比對，勾了右邊任一顆之後名稱也改成字面比對。";
        AutomationProperties.SetName(_search, "搜尋資料庫物件");
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
    /// 伺服器是單選：換一台換的是整份目錄，理由見 <see cref="ConfigureServer"/>。
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
        _sort.ToolTip = "排序：" + option.Label;
        AutomationProperties.SetName(_sort, "排序：" + option.Label);
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
    /// 分段的字取自 provider 的顯示字，一個 provider 只掛一次：目錄物件把收納桶切成第二群，
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
            "不限物件種類；每一個 provider 宣告的種類都搜。",
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
                changed |= _model.SetDatabaseSelected(database.Name, selected: true);
            }

            FillDatabases(databases);
            if (changed) FiltersChanged();
        });
        // 全不選與第一列那個「連線預設」是同一件事，所以走同一條清除路徑，不另寫一份狀態同步。
        _databases.ClearAllRequested += (_, _) => Run(ClearDatabases);
        _databases.SetBulkCommands(SqlFilterBulkCommands.SelectAndClear);
    }

    /// <summary>清掉整個資料庫維度；第一列那個預設與全不選共用這一份。</summary>
    private void ClearDatabases()
    {
        if (!_model.ClearDatabases()) return;
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

        _databases.SetNotice("正在讀取資料庫清單…", busy: true);
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
        // 清單回來的那一刻才知道連線預設是哪一個：物件總管那條連線的連線物件上沒有初始目錄，
        // 而摘要與面板第一列都要說得出名字。
        if (string.IsNullOrEmpty(_model.CurrentDatabase) && _scopeDatabases.CurrentName is { Length: > 0 } current)
        {
            _model.CurrentDatabase = current;
            UpdateFilterChrome();
        }

        var user = new List<SqlFilterOption>();
        var system = new List<SqlFilterOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string database, bool isSystem)
        {
            if (!seen.Add(database)) return;
            var option = new SqlFilterOption(
                database,
                "只搜尋這個資料庫；每指名一個就是一次含定義本文的索引。",
                _model.IsDatabaseSelected(database),
                selected => Run(() =>
                {
                    if (!_model.SetDatabaseSelected(database, selected)) return;
                    _databases.SyncEmptyOption(_model.Databases.Count == 0);
                    FiltersChanged();
                }));
            (isSystem ? system : user).Add(option);
        }

        foreach (var database in _model.Databases) Add(database, SqlSearchBrowserModel.IsSystemDatabase(database));
        foreach (var database in databases) Add(database.Name, database.IsSystem);

        // 第一列是「沒有指名」那個預設，與按鈕摘要共用同一份字；選它等於清掉整個維度，
        // 所以面板上不另畫一顆「清除」。
        _databases.SetEmptyOption(new SqlFilterOption(
            _model.ConnectionDefaultSummary(),
            "不指名資料庫；只搜這條連線預設的那一個，不建任何額外索引。",
            _model.Databases.Count == 0,
            // 取消勾它不是一個範圍；面板那一列自己會彈回去，這裡只忽略。
            selected => Run(() => { if (selected) ClearDatabases(); })));

        _databases.SetOptions(new[]
        {
            new SqlFilterGroup("使用者資料庫", user),
            new SqlFilterGroup("系統資料庫", system)
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
                ? "問不到資料庫清單；仍搜得到目前連線的那一個，或去看這個登入的權限。"
                : "問不到最新的資料庫清單，這一份可能是舊的。";
        }

        if (!_scopeDatabases.IsLoaded) return "";
        return listed == 0 ? "這個登入在這台伺服器上進不去任何資料庫。" : "";
    }

    /// <summary>
    /// 空狀態那顆按鈕：去物件總管找一台。
    /// </summary>
    /// <remarks>
    /// 開窗時<b>不</b>自動退回物件總管，這一步一定由使用者發動：物件總管服務第一次取用會把
    /// 那個工具視窗叫出來（見 <see cref="ConfigureServer"/>），而使用者可能正是把它關掉的人。
    ///
    /// 只有一台時直接用它——那不是替他猜，清單上只有那一個答案；好幾台就打開同一份伺服器面板
    /// 讓他挑，不自己選一台，理由與 <see cref="SqlSearchCatalogs.DropMissingServer"/> 相同：
    /// 默默換掉使用者的範圍比留著更糟。
    /// </remarks>
    private void PickServerFromExplorer()
    {
        var servers = _catalogs.ListServers();

        if (servers is null)
        {
            Report("問不到物件總管；請在 SQL 查詢視窗連上資料庫。");
            return;
        }

        if (servers.Count == 0)
        {
            Report("物件總管上還沒有連上的 SQL Server。");
            return;
        }

        if (servers.Count == 1)
        {
            SelectServer(servers[0]);
            Report("已改用 " + servers[0].DisplayName + "。");
            return;
        }

        _server.Open();
    }

    /// <summary>
    /// 伺服器單選：跟著查詢視窗，或指名物件總管上已連線的其中一台。
    /// </summary>
    /// <remarks>
    /// 單選而不是多選：換一台換的是整份目錄，同時搜好幾台要的是每台一個 provider、
    /// 一道硬性期限與一份說得出「哪幾台沒回來」的文案，那些都還沒有。做成看起來可以
    /// 複選的樣子，使用者勾了兩台卻只有一台的結果，而畫面上看不出少了哪一台。
    ///
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
    /// 伺服器下拉的兩段：跟著查詢視窗，與物件總管上已連線的伺服器。
    /// </summary>
    /// <remarks>
    /// 物件總管上那一台若就是查詢視窗連的那一台，就不另外列一次：同一台列兩行，
    /// 使用者會以為那是兩個不同的範圍。比對走連線字串裡的伺服器名稱（
    /// <see cref="SqlSearchCatalogs.ActiveEditorServerName"/>），不是快取鍵——快取鍵是
    /// 整串正規化過的連線字串，同一台伺服器的兩條連線幾乎不會相等。
    ///
    /// 問不到物件總管時<b>明說</b>，不假裝這就是全部：少列一台而使用者看不出差別，
    /// 比只列一台更糟。
    /// </remarks>
    private void FillServer()
    {
        var servers = _catalogs.ListServers();

        // 指名的那一台已經從物件總管上消失了（使用者中斷了連線）：換回查詢視窗並重搜，
        // 而不是留著一個連不上的範圍讓每一輪都空手而回。
        if (_catalogs.DropMissingServer(servers))
        {
            _model.Server = null;
            ObserveConnection(reload: true);
        }

        var editorServer = _catalogs.ActiveEditorServerName();
        var options = new List<SqlFilterOption>
        {
            new(
                SqlEditorConnectionText.Label(editorServer),
                "跟著作用中的查詢視窗；切到連著別台的分頁就跟著換。",
                _catalogs.FollowsActiveEditor,
                // 單選：勾掉等於沒有範圍可搜，所以勾與不勾都是「選這一個」。
                _ => Run(() => SelectServer(null)))
        };

        var explorer = new List<SqlFilterOption>();

        foreach (var server in servers ?? Array.Empty<SsmsObjectExplorerServer>())
        {
            // 同一台不列兩次；查詢視窗那一行已經涵蓋它，而且那一行還會跟著分頁換。
            if (SqlSearchCatalogs.IsSameServer(server, editorServer)) continue;

            var selected = _catalogs.Server is { } current &&
                string.Equals(current.RootUrn, server.RootUrn, StringComparison.Ordinal);

            explorer.Add(new SqlFilterOption(
                server.DisplayName,
                "改用物件總管上這一台的連線搜尋；清單、預覽與定義都跟著換過去。",
                selected,
                value => Run(() => SelectServer(value ? server : null))));
        }

        // 問不到物件總管時，那一句掛在第一段的標題上而不是第二段：空的段落整段不畫，
        // 掛在那裡的話使用者只會看到一份看起來就是全部的清單。
        _server.SetOptions(new[]
        {
            new SqlFilterGroup(servers is null ? "問不到物件總管，只列得出這一台" : "", options),
            new SqlFilterGroup("物件總管", explorer)
        });
    }

    /// <summary>查詢視窗現在的連線；沒有視窗或沒有連線時為 null。</summary>
    private SqlConnectionLabel? ReadEditorConnection() =>
        SqlAssistPlatformGuard.Probe("取得查詢視窗連線", () => SqlWindowConnections.ReadActive(_services), null);

    /// <summary>
    /// 範圍換回查詢視窗那條連線：跟著查詢視窗，資料庫回到它連著的那一個。
    /// </summary>
    /// <remarks>
    /// 與 SQL Memory 那一顆結果一致、做法不同：這裡本來就有「跟著查詢視窗」這個狀態，
    /// 所以是改回那個狀態，之後換分頁仍會跟著走；不是把當下的名稱寫死成指名一台。
    /// 查詢視窗沒有連線時<b>不動</b>範圍：換回去的結果是一個搜不到任何東西的範圍，
    /// 而使用者按下去是為了搜那條連線。已經是那個範圍時什麼都不做，按了不會有損失。
    /// </remarks>
    private void UseEditorConnection()
    {
        if (ReadEditorConnection() is not { Server.Length: > 0 })
        {
            Report(SqlEditorConnectionText.NotConnectedReport);
            return;
        }

        if (!_catalogs.FollowsActiveEditor) SelectServer(null);
        ClearDatabases();
    }

    /// <summary>
    /// 換一台伺服器；<paramref name="server"/> 為 null 表示回到作用中的查詢視窗。
    /// </summary>
    /// <remarks>
    /// 資料庫的勾選在觀測到換台時由 <see cref="SqlSearchBrowserModel.ObserveServer"/> 清掉，
    /// 不在這裡清：跟著查詢視窗換台是同一條規則。索引<b>不</b>丟：它照連線的快取鍵存，
    /// 換回來時原本那一份還在。
    /// </remarks>
    private void SelectServer(SsmsObjectExplorerServer? server)
    {
        if (!_catalogs.Select(server))
        {
            // 勾掉目前這一個不是一個範圍；把勾選寫回去，不留一個什麼都沒選的選單。
            FillServer();
            return;
        }

        _model.Server = server?.DisplayName;
        FillServer();
        ObserveConnection(reload: true);

        // 指名一台之後直接把資料庫面板打開：物件總管那條連線的預設資料庫通常是 master，
        // 而「搜整台的 master」幾乎不會是使用者要的範圍。下一步擺在眼前比讓他自己發現
        // 範圍不對便宜得多，而那一次展開同時也問到了連線預設是哪一個。
        // 換回查詢視窗時不開：那一條連線的資料庫就是他正在看的那一個。
        if (server is not null && IsVisible) Defer(_databases.Open);
    }

    /// <remarks>
    /// 兩個出處：切換分頁（<see cref="ActiveSqlEditor.Changed"/>），以及同一個分頁裡換連線、
    /// 換資料庫或中斷（<see cref="SqlEditorConnectionWatcher.Changed"/>）。只接前者的症狀是
    /// 在原分頁改連別台之後，列上「會不會開未連線的視窗」與範圍摘要都停在上一台。
    /// 後者每批 F5 都會發一次，所以不強制重搜：範圍真的換了才重搜。
    ///
    /// 先等中繼資料服務確認再讀：事件當下服務手上的目錄還是上一台的，那時讀等於「沒換」，
    /// 而確認完之後不會再有任何事件叫這裡重讀。
    /// </remarks>
    private void OnConnectionContextChanged(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("排入 SQL Search 連線更新", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                _ = SqlAssistPlatformGuard.RunAsync("更新 SQL Search 連線", ObserveConfirmedConnectionAsync, fallback: false))));

    private async Task<bool> ObserveConfirmedConnectionAsync()
    {
        await _catalogs.ConfirmAsync().ConfigureAwait(true);
        ObserveConnection(reload: false);
        return true;
    }

    /// <summary>重讀範圍與作用中的查詢視窗；範圍換過或 <paramref name="reload"/> 時把這一輪作廢重搜。</summary>
    private void ObserveConnection(bool reload)
    {
        if (_disposed) return;

        var moved = ObserveCatalog(out var catalog);
        _activeEditorServer = _catalogs.ActiveEditorServerName();
        _model.ActiveEditorServer = _catalogs.FollowsActiveEditor ? _activeEditorServer : null;
        // 移至定義會不會開未連線的視窗跟著查詢視窗走；清單上既有的列在這裡重算，不在每次繪製時問。
        foreach (var row in _rows) row.ObserveActiveEditor(_activeEditorServer);
        // 伺服器下拉一律可按：連不上目前這一台時，換一台正是使用者要做的事。
        _databases.IsEnabled = catalog is not null;

        if ((reload || moved) && IsVisible) Changed(immediate: true, keepChecks: !moved);
        else UpdateChrome();
    }

    /// <summary>
    /// 把範圍的目錄與伺服器交給 provider，並同步跟著它走的狀態。
    /// </summary>
    /// <remarks>
    /// 連線觀測與每一輪搜尋前都走這一支；各寫一份的症狀是兩邊對「目前資料庫」的說法不同
    /// （物件總管那條連線沒有初始目錄，只有一邊記得退回清單上的名稱）。
    /// </remarks>
    /// <returns>範圍換到另一個目錄或另一台時為 true。</returns>
    private bool ObserveCatalog(out SqlMetadataCatalog? catalog)
    {
        // 目錄與伺服器一次交出，命中帶的才是這份目錄那一台；說不出是哪一台就不搜。
        var connection = _catalogs.ResolveConnection();
        catalog = connection?.Catalog;
        var moved = _providers.UseConnection(connection);
        _model.HasConnection = _providers.HasConnection;

        if (_model.ObserveServer(connection?.Origin.ServerName))
        {
            // 上一台的清單與勾選都不代表這一台了。清單留到新結果回來才換的話，這一台還在建索引的
            // 那幾秒，摘要寫著這一台，下面列的卻是上一台的物件。勾選的重建要等這一輪事件走完。
            ClearRows();
            Defer(() => FillDatabases(_scopeDatabases.Items));
        }

        // 清單快取跟著連線走，而且在這裡就同步：展開下拉時才比對的話，換一台之後
        // IsLoaded 仍是上一台的 true，下拉會一直畫著上一台的資料庫。
        _scopeDatabases.SyncTo(catalog);
        // 兩顆按鈕的摘要都要說得出跟著誰、搜的是哪一個；「查詢視窗」與「連線預設」單獨出現時
        // 分不出是沒連線，還是只搜得到 master。物件總管那條連線上沒有初始目錄，名稱要等
        // 資料庫清單回來才補得上（見 FillDatabases）。
        _model.CurrentDatabase = catalog?.ConnectionSource.DatabaseName is { Length: > 0 } name
            ? name
            : _scopeDatabases.CurrentName;
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
        _databases.UpdateSummary(_model.DatabaseSummary(), Join(_model.Databases));
        _server.UpdateSummary(_model.ServerSummary(), "");

        // 有條件的維度換強調底框；窄窗收掉摘要之後，看得出哪幾顆在縮小結果靠的就是它。
        _kinds.IsNarrowed = _model.CategoryIds.Count != 0;
        _databases.IsNarrowed = _model.Databases.Count != 0;
        _server.IsNarrowed = _model.Server is { Length: > 0 };
    }

    private string Label(string categoryId) =>
        _categoryLabels.TryGetValue(categoryId, out var label) ? label : categoryId;

    private static string Join(IEnumerable<string> values) => string.Join("、", values);

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
        Report("");
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

        // 換過連線之後服務手上的目錄可能還是上一台的；搜尋等得起，先確認。等的途中又有新的一輪
        // 就讓給它。
        var token = _request.Token;
        await _catalogs.ConfirmAsync().ConfigureAwait(true);
        if (_disposed || !IsVisible || token.IsCancellationRequested) return;

        // 只更新目錄，不重跑這一輪；使用者可能在去彈跳期間換過查詢視窗。
        ObserveCatalog(out _);
        if (_model.Begin(_providers.IsIndexed(_model.Scope)) is not { } round)
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
            _model.Fail(round, "搜尋失敗：" + error.Message);
        }
        finally
        {
            _model.End(round);
            UpdateChrome();
        }
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
    private async Task CopyNameAsync(SqlSearchRow? row)
    {
        if (row is null) return;
        var failure = await SqlClipboard.WriteAsync(new DataObject(DataFormats.UnicodeText, row.QualifiedName)).ConfigureAwait(true);
        Report(failure ?? "已複製名稱。");
    }

    /// <summary>
    /// 選取工具列的「複製」（多選模式中的 Ctrl+C 也是它）：TSV 與 HTML 同時放上剪貼簿，順序照清單。
    /// </summary>
    /// <remarks>
    /// 這一輪的答案已經整份在手上，所以沒有 SQL Memory 那種背景讀取：全選時還沒分批套上的那幾批
    /// 先補完再複製。只讀列上已有的資料，不讀定義本文。失敗由 <see cref="RunAsync"/> 寫到狀態列，
    /// 所以這一支不擲出例外——工具列的點擊接不住它。
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
            if (content.RowCount == 0) { Report(SqlClipboard.EmptyMessage); return; }
            var failure = await SqlClipboard.WriteAsync(SqlClipboard.CreateDataObject(content)).ConfigureAwait(true);
            Report(failure ?? SqlClipboard.CopiedMessage(content.RowCount), failure is null ? "copied" : "");
            succeeded = failure is null;
        }).ConfigureAwait(true);
        return succeeded;
    }

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
            case SqlSearchRowAction.Preview:
                _splitView.SetDetailExpanded(true);
                UpdatePreview();
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

    /// <summary>雙擊、Enter 或右鍵選單的「移至定義」：把這一筆的定義開進新的查詢視窗。</summary>
    /// <remarks>
    /// 酬載辨識與導航都在 <see cref="SqlSearchActivation"/>；這裡只負責選了哪一列與怎麼回報。
    /// 失敗一律寫進頁尾——使用者是自己雙擊的，什麼都沒發生等於故障。
    /// </remarks>
    private async Task ActivateAsync()
    {
        if (_list.SelectedItem is not SqlSearchRow row) return;

        if (!row.CanActivate)
        {
            Report("這一筆沒有可以開啟的東西。");
            return;
        }

        // 一次只開一個視窗。查詢加開窗要好幾秒，而那幾秒裡清單照樣可以再雙擊一次；
        // 沒有這一道就是連點兩下開出兩個查詢視窗（F12 那一條由 SqlDefinitionOpener 自己擋）。
        if (_activating) return;
        _activating = true;

        try
        {
            // 先說一句，否則雙擊之後畫面完全沒有動靜。開出來的東西叫什麼由
            // SqlSearchActivation 說——這裡寫死「定義」的話，作業那幾列會說出一個
            // 它們沒有的東西，而辨識型別不准發生在這一層。
            var noun = SqlSearchActivation.SubjectNoun(row.Hit);
            Report("正在取得 " + row.Title + " 的" + noun + "…", "activating");
            var (failure, unconnected) = await SqlSearchActivation.ActivateAsync(row.Hit, _services, _catalogs);
            // 成功那一句也由 SqlSearchActivation 說：開出來的視窗有沒有連線是按下去那一刻才定案的，
            // 而沒有連線的那一種要先講，使用者按 F5 時跳出連線對話框才不會以為壞了。
            Report(
                failure ?? SqlSearchActivation.DescribeOpened(row.Hit, unconnected),
                failure is null ? "activated" : "");
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

        if (!row.CanSelectInExplorer)
        {
            Report("這一筆在物件總管上沒有自己的節點。");
            return;
        }

        if (_selecting) return;
        _selecting = true;

        try
        {
            // 展開節點要向伺服器問資料，大的資料庫上是好幾秒；先說一句，否則按下去毫無動靜，
            // 而物件總管的視窗還要再過一會兒才跳出來。
            Report("正在物件總管中選取 " + row.Title + "…", "selecting");
            var failure = await SqlSearchActivation.SelectInExplorerAsync(row.Hit, _services, _catalogs);
            Report(failure ?? "已在物件總管中選取 " + row.Title + "。", failure is null ? "selected" : "");
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
        UpdateFilterChrome();
        Report(_model.Status(_rows.Count), _model.Tone.ToString());
    }

    /// <param name="tone">
    /// 這一句在說哪一件事。狀態回饋只在換了一種說法時播一次：拿整句話比對的話，
    /// 每一批結果讓筆數加一，頁尾就會抖一下。
    /// </param>
    private void Report(string message, string tone = "")
    {
        if (_disposed) return;
        _status.Text = message;
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var current = message.Length == 0 ? "" : tone.Length == 0 ? message : tone;
        if (string.Equals(current, _statusTone, StringComparison.Ordinal)) return;
        _statusTone = current;
        if (message.Length != 0) SqlAssistChrome.PlayStatusPop(_status);
    }

    // 使用者主動觸發的失敗要看得見，所以這裡不是 SqlAssistPlatformGuard 而是回到狀態列。
    private void Run(Action action) => _ = RunAsync(() => { action(); return Task.CompletedTask; });

    /// <summary>等這一輪事件走完再做；失敗仍然回到狀態列。</summary>
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
            Report(error.Message);
        }
    }
}
