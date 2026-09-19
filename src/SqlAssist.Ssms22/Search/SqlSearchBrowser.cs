using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Search;
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
/// 版面只有三塊：工具列一列（搜尋框吃滿剩餘空間，比對位置是常駐的分段開關）、
/// 只在非預設時出現的已選條件列，以及主從區。那一列 chip 不佔預設版面，是這個工具窗在
/// 停靠面板裡多看得到幾筆結果的關鍵。
/// </remarks>
internal sealed class SqlSearchBrowser : UserControl, IDisposable
{
    /// <summary>去彈跳長度：短到打完一個詞就出結果，長到中間幾個字不各送一輪。</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>新列的進場旗標保留多久；比進場動畫長一點，之後捲動重用容器不會重播。</summary>
    private static readonly TimeSpan NewRowSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>一次套用幾列；兩百列一次塞進集合會讓清單重算一整份版面。</summary>
    private const int RowBatch = 40;

    /// <summary>主從區寬到這裡就轉成左右分割；工具窗停在下方時就是這一種。</summary>
    /// <remarks>
    /// 門檻取兩塊各 220 DIP 加上分隔線與外距。再低的話，轉向之後兩邊都窄到讀不完一個限定名稱，
    /// 而使用者只是把面板拉寬了一點。
    /// </remarks>
    private const double SideBySideWidth = 520;

    private readonly IServiceProvider _services;
    private readonly SqlSearchCatalogs _catalogs;
    private readonly SqlSearchBrowserModel _model = new();
    private readonly SqlSearchProviders _providers = new();
    private readonly ObservableCollection<SqlSearchRow> _rows = new();
    private readonly Dictionary<string, string> _categoryLabels = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<SqlSearchCategoryOption> _categoryOptions;
    private readonly SqlSearchList _list = new();
    private readonly SqlSearchPreview _preview;
    private readonly SqlMemorySplitView _splitView;
    private readonly SqlLoadingSurface _loading;
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _empty = SqlAssistChrome.CreateSearchEmptyState();
    private readonly ToggleButton _matchCasing = SqlAssistChrome.CreateSearchToggle(
        SqlIcon.MatchCase, "大小寫", "只取大小寫完全相同的本文命中；名稱一律不分大小寫。");
    private readonly ToggleButton _wholeWord = SqlAssistChrome.CreateSearchToggle(
        SqlIcon.WholeWord, "全字", "本文命中前後都必須是詞界。");
    private readonly SqlSearchSegments _segments = new();
    private readonly SqlSearchFilterButton _server = new("伺服器", SqlIcon.Server);
    private readonly SqlSearchFilterButton _databases = new("資料庫", SqlIcon.Database, filterable: true);
    private readonly SqlSearchFilterButton _kinds = new("種類", SqlIcon.Filter);
    private readonly SqlSearchChipBar _chips = new();
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

    /// <summary>目前畫在 chip 列上的那一組條件；相同就不重建，正在走 Tab 的人不會失去焦點。</summary>
    private string _chipSignature = "";
    private SqlSearchRound? _round;

    /// <summary>正在把模型的值寫回控制項；寫回去觸發的事件不是使用者的操作，不重跑一輪。</summary>
    private bool _syncing;
    private bool _activating;
    private bool _ready;
    private bool _disposed;

    public SqlSearchBrowser(IServiceProvider services)
    {
        _services = services;
        // 清單、預覽與移至定義共用同一份目錄出處；三條路徑各問各的，症狀是指名了別台伺服器
        // 之後其中一條還在答查詢視窗那一台，而兩份看起來都很正常。欄位初始設定式跑在建構式
        // 本體之前，那時候 _services 還是 null，所以這兩個不能寫成欄位初始值。
        _catalogs = new SqlSearchCatalogs(services);
        _preview = new SqlSearchPreview(_catalogs);
        foreach (var category in _providers.Aggregator.Categories) _categoryLabels[category.Id] = category.DisplayName;
        _categoryOptions = SqlSearchBrowserModel.CategoryOptions(_providers.Aggregator.Categories);
        _model.UseCategories(_categoryOptions);

        VsThemeBrushes.Apply(this);
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = SqlAssistChrome.DefaultMetrics.Body;
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        SetResourceReference(ForegroundProperty, ThemeBrush.WindowForeground);
        MinWidth = 300;

        var root = new DockPanel { Margin = new Thickness(8) };
        // 工具窗沒有原生 Titlebar，第一列直接是工具列；不另做一條看起來像第二條標題列的粗體區塊。
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        header.Children.Add(CreateToolbar());
        _chips.RemoveRequested += chip => Run(() =>
        {
            if (chip is not SqlSearchFilterChip filter) return;

            // 伺服器不只是一個名稱：拿掉它要把整份目錄換回查詢視窗那一台，
            // 只清模型的話清單還會從上一台回答。
            if (filter.Kind == SqlSearchFilterKind.Server) SelectServer(null);
            else if (_model.Remove(filter)) FiltersChanged();
        });
        header.Children.Add(_chips);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _list.SetRowsSource(_rows);
        _list.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 選取", UpdatePreview);
        _list.OpenRequested += (_, _) => _ = RunAsync(ActivateAsync);
        _list.RowActionRequested += action => Run(() => RunRowAction(action));
        _list.ContextMenu = CreateRowMenu();
        // 空狀態與載入圖示疊在同一塊內容上：兩種「現在沒東西可看」不各占一塊版面。
        var content = new Grid();
        content.Children.Add(_list);
        content.Children.Add(_empty);
        _loading = new SqlLoadingSurface(content);
        _splitView = new SqlMemorySplitView(_loading, _preview, _preview.Summary, SideBySideWidth);
        _splitView.DetailExpandedChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 預覽", UpdatePreview);
        // 剪貼簿可能被別的程序占用；失敗要看得見，否則使用者以為下一次貼上是這個名稱。
        _preview.CopyRequested += (_, _) => Run(() => CopyName(_preview.Current));
        root.Children.Add(_splitView);
        Content = root;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SearchDelay };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Search(); };
        _settleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = NewRowSettle };
        _settleTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("結束 SQL Search 進場", () =>
        {
            _settleTimer.Stop();
            foreach (var row in _rows) row.IsNew = false;
        });

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
        ActiveSqlEditor.Changed += OnEditorChanged;

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
        ActiveSqlEditor.Changed -= OnEditorChanged;
        _searchTimer.Stop();
        _settleTimer.Stop();
        _request.Cancel();
        _request.Dispose();
        _preview.Dispose();
    }

    /// <summary>
    /// 工具列：搜尋框吃滿剩餘空間，右邊依序是伺服器、資料庫、種類與比對位置。
    /// </summary>
    /// <remarks>
    /// 比對位置是常駐的分段開關而不是下拉：它是切換最頻繁的一項，藏進下拉會多兩次點擊。
    /// 種類與資料庫反過來——十幾種物件攤成 pill 會佔掉兩列，在停靠面板裡等於少看四筆結果，
    /// 所以只在按鈕上留摘要。
    /// </remarks>
    private FrameworkElement CreateToolbar()
    {
        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋");
        clear.IsEnabled = false;
        clear.Click += (_, _) => Run(() => { _search.Clear(); _search.Focus(); });
        _search.ToolTip = "搜尋物件名稱、資料行與定義本文；名稱走模糊比對，本文是字面比對。";
        AutomationProperties.SetName(_search, "搜尋資料庫物件");
        _search.TextChanged += (_, _) => Run(() =>
        {
            clear.IsEnabled = _search.Text.Length > 0;
            _model.Text = _search.Text;
            Changed();
        });

        // 大小寫與全字修飾的是「這個字串怎麼比」，不是搜哪裡，所以留在搜尋框裡而不是工具列上。
        var bar = SqlAssistChrome.CreateSearchBar(_search, clear, _matchCasing, _wholeWord);
        _matchCasing.Checked += (_, _) => Option(() => _model.MatchCasing = true);
        _matchCasing.Unchecked += (_, _) => Option(() => _model.MatchCasing = false);
        _wholeWord.Checked += (_, _) => Option(() => _model.WholeWord = true);
        _wholeWord.Unchecked += (_, _) => Option(() => _model.WholeWord = false);

        _segments.ValueChanged += (_, _) => Run(() =>
        {
            _model.Targets = _segments.Value;
            RememberMatchState();
            Changed();
        });

        ConfigureKinds();
        ConfigureDatabases();
        ConfigureServer();
        ConfigureSort();

        _refresh.Click += (_, _) => Run(() =>
        {
            _providers.Invalidate();
            // 索引與定義一起丟：只丟索引的話，改過的預存程序在清單上換了位置，
            // 預覽卻還畫著改之前那一份，而畫面上看不出那個差別。
            _preview.InvalidateDefinitions();
            Changed(immediate: true);
        });

        // 排序與重新整理排在分段開關右邊：兩顆都是圖示鈕，窄窗跟著分段開關一起換到第二列。
        return new SqlSearchToolbar(bar, _segments, new[] { _server, _databases, _kinds }, _sort, _refresh);
    }

    /// <summary>搜尋框裡的選項開關；寫回控制項時不重跑一輪。</summary>
    private void Option(Action apply) => Run(() =>
    {
        if (_syncing) return;
        apply();
        RememberMatchState();
        FiltersChanged();
    });

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
        VsThemeBrushes.Apply(_kinds.PopupSurface);
        _kinds.OptionsRequested += (_, _) => Run(FillKinds);
        _kinds.SelectAllRequested += (_, _) => Run(() =>
        {
            var changed = false;
            foreach (var option in _categoryOptions) changed |= _model.SetCategorySelected(option.Id, selected: true);
            if (changed) { FillKinds(); FiltersChanged(); }
        });
        _kinds.ClearRequested += (_, _) => Run(() =>
        {
            if (!_model.ClearCategories()) return;
            FillKinds();
            FiltersChanged();
        });
    }

    private void FillKinds()
    {
        var options = new List<SqlSearchFilterOption>(_categoryOptions.Count);

        foreach (var option in _categoryOptions)
        {
            var id = option.Id;
            options.Add(new SqlSearchFilterOption(option.Label, "", _model.IsCategorySelected(id), selected => Run(() =>
            {
                if (!_model.SetCategorySelected(id, selected)) return;
                FiltersChanged();
            })));
        }

        _kinds.SetOptions(new[] { new SqlSearchFilterGroup("", options) });
    }

    private void ConfigureDatabases()
    {
        VsThemeBrushes.Apply(_databases.PopupSurface);
        _databases.OptionsRequested += (_, _) => Run(FillDatabases);
        _databases.SelectAllRequested += (_, _) => Run(() =>
        {
            var changed = false;
            foreach (var database in CachedDatabases()) changed |= _model.SetDatabaseSelected(database, selected: true);
            if (changed) { FillDatabases(); FiltersChanged(); }
        });
        _databases.ClearRequested += (_, _) => Run(() =>
        {
            if (!_model.ClearDatabases()) return;
            FillDatabases();
            FiltersChanged();
        });
    }

    /// <summary>
    /// 資料庫下拉的兩段：使用者資料庫與系統資料庫。
    /// </summary>
    /// <remarks>
    /// 只列已經在快取裡的名稱：為了填一個下拉而去查一輪，等於在使用者沒有要求的時候連資料庫。
    /// 已經勾起來但這一輪不在快取裡的名稱仍要列出來，否則使用者取消不掉自己剛選的條件。
    /// </remarks>
    private void FillDatabases()
    {
        var user = new List<SqlSearchFilterOption>();
        var system = new List<SqlSearchFilterOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string database)
        {
            if (!seen.Add(database)) return;
            var option = new SqlSearchFilterOption(
                database,
                "只搜尋這個資料庫；每指名一個就是一次含定義本文的索引。",
                _model.IsDatabaseSelected(database),
                selected => Run(() =>
                {
                    if (!_model.SetDatabaseSelected(database, selected)) return;
                    FiltersChanged();
                }));
            (SqlSearchBrowserModel.IsSystemDatabase(database) ? system : user).Add(option);
        }

        foreach (var database in _model.Databases) Add(database);
        foreach (var database in CachedDatabases()) Add(database);

        _databases.SetOptions(new[]
        {
            new SqlSearchFilterGroup("使用者資料庫", user),
            new SqlSearchFilterGroup("系統資料庫", system)
        });
    }

    /// <summary>
    /// 伺服器單選：跟著查詢視窗，或指名物件總管上已連線的其中一台。
    /// </summary>
    /// <remarks>
    /// 單選而不是多選：換一台換的是整份目錄，同時搜好幾台要的是每台一個 provider、
    /// 一道硬性期限與一份說得出「哪幾台沒回來」的文案，那些都還沒有。做成看起來可以
    /// 複選的樣子，使用者勾了兩台卻只有一台的結果，而畫面上看不出少了哪一台。
    ///
    /// 清單只在使用者打開下拉那一刻重問（沒有 I/O，見 <see cref="SsmsObjectExplorerServers"/>）。
    /// <b>禁止</b>改成輪詢：物件總管服務第一次取用會把那個工具視窗叫出來，
    /// 使用者把它關掉之後，輪詢會在他沒有要求的時候替他開回去。
    /// </remarks>
    private void ConfigureServer()
    {
        VsThemeBrushes.Apply(_server.PopupSurface);
        _server.OptionsRequested += (_, _) => Run(FillServer);
        // 單選沒有「全選」可言；兩顆都只是重畫一次清單，不留按了沒有作用的開關。
        _server.SelectAllRequested += (_, _) => Run(FillServer);
        _server.ClearRequested += (_, _) => Run(() => SelectServer(null));
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
        var options = new List<SqlSearchFilterOption>
        {
            new(
                SqlSearchBrowserModel.ActiveEditorServerLabel + (editorServer is null ? "" : "（" + editorServer + "）"),
                "跟著作用中的查詢視窗；切到連著別台的分頁就跟著換。",
                _catalogs.FollowsActiveEditor,
                // 單選：勾掉等於沒有範圍可搜，所以勾與不勾都是「選這一個」。
                _ => Run(() => SelectServer(null)))
        };

        var explorer = new List<SqlSearchFilterOption>();

        foreach (var server in servers ?? Array.Empty<SsmsObjectExplorerServer>())
        {
            // 同一台不列兩次；查詢視窗那一行已經涵蓋它，而且那一行還會跟著分頁換。
            if (editorServer is not null &&
                string.Equals(server.ServerName, editorServer, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var selected = _catalogs.Server is { } current &&
                string.Equals(current.RootUrn, server.RootUrn, StringComparison.Ordinal);

            explorer.Add(new SqlSearchFilterOption(
                server.DisplayName,
                "改用物件總管上這一台的連線搜尋；清單、預覽與定義都跟著換過去。",
                selected,
                value => Run(() => SelectServer(value ? server : null))));
        }

        // 問不到物件總管時，那一句掛在第一段的標題上而不是第二段：空的段落整段不畫，
        // 掛在那裡的話使用者只會看到一份看起來就是全部的清單。
        _server.SetOptions(new[]
        {
            new SqlSearchFilterGroup(servers is null ? "問不到物件總管，只列得出這一台" : "", options),
            new SqlSearchFilterGroup("物件總管", explorer)
        });
    }

    /// <summary>
    /// 換一台伺服器；<paramref name="server"/> 為 null 表示回到作用中的查詢視窗。
    /// </summary>
    /// <remarks>
    /// 資料庫的勾選一併清掉：名稱是每台伺服器自己的，留著的症狀是換台之後整輪指名一個
    /// 那裡不存在的資料庫，而畫面上只看得到「沒有相符項目」。定義快取同理——
    /// <c>object_id</c> 跨伺服器毫無關係。索引<b>不</b>丟：它照連線的快取鍵存，
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
        _model.ClearDatabases();
        _preview.InvalidateDefinitions();
        FillServer();
        ObserveConnection(reload: true);
    }

    private IReadOnlyList<string> CachedDatabases() =>
        (_providers.HasConnection ? _catalogs.Resolve()?.CachedSnapshot?.Databases : null) ?? Array.Empty<string>();

    private void OnEditorChanged(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("排入 SQL Search 連線更新", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                SqlAssistPlatformGuard.Run("更新 SQL Search 連線", () => ObserveConnection(reload: true)))));

    /// <summary>重讀這一輪要用的目錄；換過連線就把這一輪作廢重搜。</summary>
    private void ObserveConnection(bool reload)
    {
        if (_disposed) return;

        var catalog = _catalogs.Resolve();
        _providers.UseCatalog(catalog);
        _model.HasConnection = catalog is not null;
        // 伺服器下拉一律可按：連不上目前這一台時，換一台正是使用者要做的事。
        _databases.IsEnabled = catalog is not null;

        // 換過查詢視窗就可能換了伺服器；上一台的定義留著會冒充這一台同號的物件。
        if (reload) _preview.InvalidateDefinitions();

        if (reload && IsVisible) Changed(immediate: true);
        else UpdateChrome();
    }

    /// <summary>篩選改變：更新 chip 列與摘要，然後重跑一輪。</summary>
    private void FiltersChanged()
    {
        UpdateFilterChrome();
        Changed();
    }

    private void UpdateFilterChrome()
    {
        // 寫回勾選狀態會觸發 Checked／Unchecked；沒有這道旗標就會再跑一輪，而那一輪又會寫回來。
        _syncing = true;
        try
        {
            _matchCasing.IsChecked = _model.MatchCasing;
            _wholeWord.IsChecked = _model.WholeWord;
        }
        finally
        {
            _syncing = false;
        }

        _kinds.UpdateSummary(_model.CategorySummary(), Join(_model.CategoryIds.Select(Label)));
        _databases.UpdateSummary(_model.DatabaseSummary(), Join(_model.Databases));
        _server.UpdateSummary(_model.ServerSummary(), "");

        // chip 只在條件真的變了才重建。每一批結果都重建一次的話，正在用 Tab 走過 chip 列的人
        // 會在結果載入到一半時失去鍵盤焦點。
        var chips = _model.Chips();
        var signature = string.Join("\n", chips.Select(chip => chip.Label));
        if (string.Equals(signature, _chipSignature, StringComparison.Ordinal)) return;
        _chipSignature = signature;
        _chips.SetChips(chips, chip => chip.Label);
    }

    private string Label(string categoryId) =>
        _categoryLabels.TryGetValue(categoryId, out var label) ? label : categoryId;

    private static string Join(IEnumerable<string> values) => string.Join("、", values);

    /// <summary>輸入、範圍或選項改變：作廢這一輪，但<b>不清空清單</b>，等新結果回來才換。</summary>
    private void Changed(bool immediate = false)
    {
        if (!_ready || _disposed) return;
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

        ObserveCatalogOnly();
        if (_model.Begin(_providers.IsIndexed(_model.Scope)) is not { } round)
        {
            // 這一輪不會有新結果來換掉舊的（清空了搜尋框或斷了線）；留著上一份等於拿過期的
            // 清單冒充目前條件的答案。
            ClearRows();
            UpdateChrome();
            return;
        }

        var token = _request.Token;
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

    /// <summary>只更新目錄，不重跑這一輪；使用者可能在去彈跳期間換過查詢視窗。</summary>
    private void ObserveCatalogOnly()
    {
        var catalog = _catalogs.Resolve();
        _providers.UseCatalog(catalog);
        _model.HasConnection = catalog is not null;
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
            _rows.Add(new SqlSearchRow(hit, label) { IsNew = motion });
        }

        if (_applied < _applying.Count) return false;

        if (motion && _rows.Count > 0) _settleTimer.Start();
        if (_model.ResolveSelection(_rows.Select(row => row.Key).ToArray(), _list.SelectedItem is not null) is { } index)
        {
            _list.SelectedIndex = index;
        }

        UpdateChrome();
        return true;
    }

    private void CopyName(SqlSearchRow? row)
    {
        if (row is null) return;
        Clipboard.SetText(row.Path.Length == 0 ? row.Title : row.Path);
        Report("已複製名稱。");
    }

    private void RunRowAction(SqlSearchRowAction action)
    {
        switch (action)
        {
            case SqlSearchRowAction.Activate:
                _ = RunAsync(ActivateAsync);
                return;
            case SqlSearchRowAction.Copy:
                CopyName(_list.SelectedItem as SqlSearchRow);
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

        if (!SqlSearchActivation.CanActivate(row.Hit))
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
            var failure = await SqlSearchActivation.ActivateAsync(row.Hit, _services, _catalogs);
            Report(
                failure ?? "已在新查詢視窗開啟 " + row.Title + " 的" + noun + "。",
                failure is null ? "activated" : "");
        }
        finally
        {
            _activating = false;
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
                item.IsEnabled = row is not null &&
                    (action != SqlSearchRowAction.Activate || SqlSearchActivation.CanActivate(row.Hit));

                // 空字串會畫成一個空的提示框；沒有描述就整個不掛。
                item.ToolTip = action == SqlSearchRowAction.Activate && row is not null &&
                    SqlSearchActivation.Describe(row.Hit) is { Length: > 0 } description
                    ? description
                    : null;
            }
        });

        return menu;
    }

    private void UpdateChrome()
    {
        if (_disposed) return;
        _loading.IsLoading = _model.ShowLoading(_rows.Count);
        var empty = _model.EmptyState(_rows.Count);
        _empty.Text = empty;
        _empty.Visibility = empty.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateFilterChrome();
        Report(_model.Status(), _model.Tone.ToString());
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
