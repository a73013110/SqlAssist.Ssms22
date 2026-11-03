using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
/// 沒有狀態列：筆數與「這一份完不完整」寫在清單的頁尾（與 SQL Memory 同一個
/// <see cref="SqlListPager"/>），跑得久的那一輪在清單上方有一條進度（<see cref="SqlProgressStrip"/>），
/// 按下去的結果與失敗一律走通知（<see cref="Notify"/>）。
/// </remarks>
internal sealed class SqlSearchBrowser : UserControl, IDisposable
{
    /// <summary>新列的進場旗標保留多久；比進場動畫長一點，之後捲動重用容器不會重播。</summary>
    private static readonly TimeSpan NewRowSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>一次套用幾列；兩百列一次塞進集合會讓清單重算一整份版面。</summary>
    private const int RowBatch = 40;

    /// <summary>
    /// 一輪跑超過這麼久才出現進度與邊跑邊列的結果。
    /// </summary>
    /// <remarks>
    /// 快取命中的那幾輪幾十毫秒就結束；每一輪都閃一下進度條，比什麼都不顯示更吵。
    /// </remarks>
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 建索引跑超過這麼久才送通知。
    /// </summary>
    /// <remarks>
    /// 進度主要在清單上方那一條；通知是給切到別的視窗去的人看的。每一輪都送的話，
    /// 快的那幾輪會在通知島上閃一列，而使用者一直都在看著這個視窗。
    /// </remarks>
    private static readonly TimeSpan IndexNoticeDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;
    private readonly SqlCatalogScopeBar _scope;
    private readonly SqlSearchCatalogs _catalogs;
    private readonly SqlSearchBrowserModel _model = new();
    private readonly SqlSearchProviders _providers = new();
    private readonly ObservableCollection<SqlSearchRow> _rows = new();
    private readonly Dictionary<string, string> _categoryLabels = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<SqlSearchCategoryOption> _categoryOptions;
    private readonly SqlSearchList _list = new();
    private readonly SqlSearchPreview _preview;
    private readonly MasterDetailView _splitView;
    private readonly SqlStateSurface _surface;
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SqlListPager _footer = new();
    private readonly SqlProgressStrip _progress = new(SqlSearchText.Stop);
    private readonly DispatcherTimer _progressTimer;
    private readonly SearchEta _eta = new();
    private readonly Stopwatch _runClock = new();
    private readonly SqlMatchToggles _matchToggles = new();
    private readonly SqlSearchSegments _segments = new();
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

    /// <summary>正在跑、而且是畫面上那一輪的；進度與邊跑邊列的結果都讀它。</summary>
    private SearchRun? _run;

    /// <summary><see cref="_run"/> 在模型上的身分；邊跑邊列的列掛在它底下。</summary>
    private SqlSearchRound? _runRound;

    /// <summary>邊跑邊列時已經拿了第幾筆；每次只拿之後到的。</summary>
    private int _streamed;

    /// <summary>邊跑邊列的那一份；null 表示這一輪還沒開始列。</summary>
    private List<SearchHit>? _stream;

    /// <summary>邊跑邊列時已經列過的去重鍵：同一個物件的第二種命中等整輪結束才併進那一列。</summary>
    private readonly HashSet<string> _streamKeys = new(StringComparer.Ordinal);

    /// <summary>使用者按了停止；這一輪取消後的結果照樣採用，頁尾說「已停止」。</summary>
    private bool _stopRequested;

    /// <summary>建立搜尋索引那則通知；建索引跑得夠久才開，整輪結束才關。</summary>
    private NotificationScope? _indexNotice;

    private string _indexNoticeProgress = "";

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
        _scope = new SqlCatalogScopeBar(services, _model.Scope, new SqlCatalogScopeOptions(
            MultipleDatabases: true, SqlSearchText.DatabaseOptionDescription, SqlSearchText.AllDatabasesDescription,
            NotificationKind.Search));
        _scope.Changed += (_, change) => Run(() => ScopeChanged(change));
        _catalogs = new SqlSearchCatalogs(_scope.Selection);
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
        // 進度在工具列下面、清單上面：頁尾在清單的最後，列一多就捲不到。
        _progress.StopRequested += (_, _) => Run(Stop);
        header.Children.Add(_progress);

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
        _surface.ActionRequested += (_, _) => Run(_scope.OpenServers);
        _splitView = new MasterDetailView(_surface, _preview, _preview.Summary, MasterDetailView.DefaultSideBySideWidth);
        _splitView.DetailExpandedChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 預覽", UpdatePreview);
        root.Children.Add(_splitView);
        Content = root;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SqlAssistChrome.Debounce.Search };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Search(); };
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _progressTimer.Tick += (_, _) => SqlAssistPlatformGuard.Run("更新 SQL Search 進度", OnProgressTick);
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
            // 看不見的工具窗不該還佔著連線；建索引也一起停（它不跟著輪次取消）。
            // 取消之後上一份結果留在畫面上，回來時重搜，建到一半的索引從頭再建。
            else
            {
                CancelRequest();
                _providers.CancelBuilds();
            }
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
        _progressTimer.Stop();
        _request.Cancel();
        _request.Dispose();
        _providers.CancelBuilds();
        CloseIndexNotice(NotificationStatus.Canceled);
        _scope.Dispose();
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
            _scope.Refresh();
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

        return new SqlScopeToolbar(
            searchSlot,
            new[] { _scope.Controls.ToArray(), new FrameworkElement[] { _kinds }, new FrameworkElement[] { _segments } });
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

    /// <summary>
    /// 範圍列換了：目錄換了就清掉上一台的清單再立刻重搜；只換資料庫的勾選照一般輸入去彈跳。
    /// </summary>
    /// <remarks>
    /// 上一台的清單不代表這一台了。留到新結果回來才換的話，這一台還在建索引的那幾秒，
    /// 摘要寫著這一台，下面列的卻是上一台的物件。
    /// </remarks>
    private void ScopeChanged(SqlCatalogScopeChange change)
    {
        if (_disposed) return;

        if (change == SqlCatalogScopeChange.Databases)
        {
            Changed();
            return;
        }

        if (ObserveCatalog(out _)) ClearRows();
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
        if (IsVisible) await _scope.EnsureServerAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>重讀作用中查詢視窗連著哪一台，並讓清單上既有的列照它重算。</summary>
    private void ObserveActiveEditor()
    {
        _activeEditorServer = _scope.Selection.ActiveEditorServerName();
        foreach (var row in _rows) row.ObserveActiveEditor(_activeEditorServer);
    }

    /// <summary>工具窗重新出現：同一組條件重搜；還沒選伺服器就先自動套用查詢視窗那一條。</summary>
    private void OnShown()
    {
        ObserveActiveEditor();
        ObserveCatalog(out _);
        Changed(immediate: true, keepChecks: true);
        _ = RunAsync(_scope.EnsureServerAsync);
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
        var connection = _scope.Observe();
        catalog = connection?.Catalog;
        var moved = _providers.UseConnection(connection);
        _model.HasConnection = _providers.HasConnection;
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

        // 選了值的維度換強調底框；窄窗收掉摘要之後，看得出哪幾顆有作用靠的就是它。
        // 伺服器與資料庫那兩顆由範圍列自己畫。
        _kinds.HasSelection = _model.CategoryIds.Count != 0;
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
        _stopRequested = false;

        // 只更新目錄，不重跑這一輪：物件總管那一台上一次連不上的話，這一輪再試一次。
        ObserveCatalog(out _);
        var known = _scope.Databases.Items.Select(database => database.Name).ToArray();
        if (_model.Begin(_providers.IsIndexed(_model.ToSearchScope(), known)) is not { } round)
        {
            // 這一輪不會有新結果來換掉舊的（清空了搜尋框或斷了線）；留著上一份等於拿過期的
            // 清單冒充目前條件的答案。進度與建索引的通知也一起收：沒有下一輪來接手了。
            ClearRows();
            EndProgress(NotificationStatus.Canceled);
            UpdateChrome();
            return;
        }

        // 要先建索引的那一輪，上一份清單屬於別的條件而且會被轉圈蓋住好幾秒；先清掉，
        // 這一輪邊跑邊列的結果才不會和它混在一起。
        if (round.NeedsIndex) ClearRows();

        UpdateChrome();
        SearchResults? results = null;

        try
        {
            // 背景執行緒開始整輪；UI 執行緒不同步等待，第一次建索引可能要數秒到數分鐘。
            var run = await Task.Run(() => _providers.Aggregator.Start(round.Query, token), token).ConfigureAwait(true);
            BeginProgress(run, round);
            results = await run.Completion.ConfigureAwait(true);

            // 打字取消的那一輪整份不要；使用者按停止的那一輪照樣採用，頁尾說已停止。
            if ((token.IsCancellationRequested && !_stopRequested) || !_model.Accept(round, results)) return;
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

            if (_model.IsCurrent(round))
            {
                // 被打字取消的那一輪：下一輪馬上接手，進度條與建索引的通知留著給它（建索引
                // 不跟著輪次取消，那一則說的仍然是真的）。停止、收起視窗與正常結束才收尾。
                if (token.IsCancellationRequested && !_stopRequested && IsVisible) PauseProgress();
                else EndProgress(Outcome(results));

                NotifyFailure();
            }

            UpdateChrome();
        }
    }

    /// <summary>這一輪在通知上算哪一種結局；與頁尾同一個判準。</summary>
    private NotificationStatus Outcome(SearchResults? results) =>
        results is null ? _model.Failure.Length != 0 ? NotificationStatus.Failed : NotificationStatus.Canceled
        : results.IsCanceled ? NotificationStatus.Canceled
        : _model.IsComplete ? NotificationStatus.Succeeded
        : NotificationStatus.Degraded;

    /// <summary>使用者按了停止：這一輪與正在建的索引都停下，已經找到的留著。</summary>
    private void Stop()
    {
        if (!_model.IsRunning) return;

        _stopRequested = true;
        _providers.CancelBuilds();
        _request.Cancel();
    }

    /// <summary>這一輪開始了：進度從零算起，晚一小段時間才出現。</summary>
    private void BeginProgress(SearchRun run, SqlSearchRound round)
    {
        _run = run;
        _runRound = round;
        _streamed = 0;
        _stream = null;
        _streamKeys.Clear();
        _eta.Reset();
        _runClock.Restart();
        _progressTimer.Start();
    }

    /// <summary>
    /// 每隔一小段時間讀一次進度：更新清單上方那一條、列出新到的命中、更新建索引的通知。
    /// </summary>
    /// <remarks>
    /// 由畫面來問而不是 provider 推：建索引一秒讀上萬列，每一列推一次事件會把 UI 執行緒塞滿。
    /// </remarks>
    private void OnProgressTick()
    {
        if (_disposed || _run is not { } run) return;

        var elapsed = _runClock.Elapsed;
        if (elapsed < ProgressDelay) return;

        var progress = run.Progress();
        var remaining = progress.IsDeterminate ? _eta.Observe(elapsed, progress.Fraction) : null;
        _progress.Show(
            SqlSearchBrowserModel.ProgressText(progress, _model.IsIndexing, remaining),
            progress.IsDeterminate ? progress.Fraction : null);

        StreamArrived(run);
        UpdateIndexNotice(progress, elapsed);
    }

    /// <summary>
    /// 把新到的命中接在清單後面，照到達順序；整輪結束才照排序重排一次。
    /// </summary>
    /// <remarks>
    /// 每一次都重排的話，使用者正在看的那一列會一直跳走。同一個物件的第二種命中（名稱之後又在
    /// 本文命中）不另起一列，等整輪結束由聚合器併成一列。
    /// </remarks>
    private void StreamArrived(SearchRun run)
    {
        var arrived = run.ArrivedSince(_streamed);
        if (arrived.Count == 0) return;
        _streamed += arrived.Count;

        if (_runRound is not { } round || !_model.IsCurrent(round)) return;

        if (_stream is null)
        {
            // 這一輪第一批：上一輪的列不屬於這一份答案。
            ClearRows();
            _stream = new List<SearchHit>();
            _round = round;
            _applying = _stream;
        }

        foreach (var hit in arrived)
        {
            if (_streamKeys.Add(hit.DedupeKey)) _stream.Add(hit);
        }

        AppendBatch(round);
    }

    /// <summary>
    /// 建索引跑得夠久就開一則通知，之後跟著進度更新。
    /// </summary>
    /// <remarks>
    /// 開在這裡而不是一開始就開：快的那幾輪不該在通知島上閃一列。只有要建索引的那幾輪才開——
    /// 比對本身再慢也只有幾秒，使用者一直看著清單。
    /// </remarks>
    private void UpdateIndexNotice(SearchProgress progress, TimeSpan elapsed)
    {
        if (!_model.IsIndexing || elapsed < IndexNoticeDelay || !progress.IsDeterminate) return;

        _indexNotice ??= NotificationCenter.Default.BeginDetached(
            NotificationCatalog.BuildingSearchIndex, NotificationKind.Search, NotificationOrigin.User, NotificationLevel.Info,
            subject: SqlCatalogScopeSummary.Server(_model.Scope));

        var text = SqlSearchText.IndexNoticeProgress(progress.Finished, progress.Total);
        if (string.Equals(text, _indexNoticeProgress, StringComparison.Ordinal)) return;
        _indexNoticeProgress = text;
        _indexNotice.Report(text);
    }

    /// <summary>這一輪被下一輪取代：停止讀進度，進度條留著等下一輪接手。</summary>
    private void PauseProgress()
    {
        _progressTimer.Stop();
        _run = null;
        _runRound = null;
        _stream = null;
    }

    /// <summary>這一輪結束（完成、停止、失敗或沒有下一輪）：進度收起來，通知寫上結局。</summary>
    private void EndProgress(NotificationStatus outcome)
    {
        PauseProgress();
        _runClock.Reset();
        _progress.Hide();
        CloseIndexNotice(outcome);
    }

    /// <summary>
    /// 關掉建索引那則通知，結局與頁尾同一句。
    /// </summary>
    /// <remarks>
    /// 被下一輪取代（使用者又打了字）時<b>不</b>關（見 <see cref="PauseProgress"/>）：建索引不跟著
    /// 輪次取消，那一則說的仍然是真的，由接手的那一輪收尾。
    /// </remarks>
    private void CloseIndexNotice(NotificationStatus outcome)
    {
        if (_indexNotice is not { } notice) return;

        _indexNotice = null;
        _indexNoticeProgress = "";

        switch (outcome)
        {
            case NotificationStatus.Failed:
                notice.Fail();
                break;
            case NotificationStatus.Canceled:
                notice.Cancel();
                break;
            case NotificationStatus.Degraded:
                notice.Degrade();
                break;
        }

        // 有答案的那兩種寫上完整度，與頁尾同一句；停止與失敗由狀態本身說。
        if (outcome is NotificationStatus.Succeeded or NotificationStatus.Degraded) notice.Report(_model.CoverageSummary);
        notice.Dispose();
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
        // 邊跑邊列的那幾列照到達順序，整輪結束時照新的排序一次排好；途中重排會和下一批打架。
        if (_model.IsRunning) return;
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
