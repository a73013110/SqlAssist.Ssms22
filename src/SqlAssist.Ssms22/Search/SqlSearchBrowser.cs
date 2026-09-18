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
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Caching;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// SQL Search 工具窗的畫面與繫結。
/// </summary>
/// <remarks>
/// 與 SQL Memory 的瀏覽器同一種分工：輸入、範圍與選項的值交給 <see cref="SqlSearchBrowserModel"/>，
/// 回應也交回它決定要不要採用；這裡只做版面、繫結與派送。來源清單在
/// <see cref="SqlSearchProviders"/>，啟動在 <see cref="SqlSearchActivation"/>，三者都不互相知道細節。
/// </remarks>
internal sealed class SqlSearchBrowser : UserControl, IDisposable
{
    /// <summary>去彈跳長度：短到打完一個詞就出結果，長到中間幾個字不各送一輪。</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>新列的進場旗標保留多久；比進場動畫長一點，之後捲動重用容器不會重播。</summary>
    private static readonly TimeSpan NewRowSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>一次套用幾列；兩百列一次塞進集合會讓分組檢視重算一整份版面。</summary>
    private const int RowBatch = 40;

    private readonly IServiceProvider _services;
    private readonly SqlSearchBrowserModel _model = new();
    private readonly SqlSearchProviders _providers = new();
    private readonly ObservableCollection<SqlSearchRow> _rows = new();
    private readonly Dictionary<string, string> _categoryLabels = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<SqlSearchCategoryOption> _categoryOptions;
    private readonly SqlSearchList _list = new();
    private readonly SqlSearchPreview _preview = new();
    private readonly SqlMemorySplitView _splitView;
    private readonly SqlLoadingSurface _loading;
    private readonly TextBox _search = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _scope = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _empty = SqlAssistChrome.CreateSearchEmptyState();
    private readonly CheckBox _matchCasing = SqlAssistChrome.CreateSearchOption("大小寫", "只取大小寫完全相同的本文命中；名稱一律不分大小寫。");
    private readonly CheckBox _wholeWord = SqlAssistChrome.CreateSearchOption("全字", "本文命中前後都必須是詞界。");
    private readonly Button _database;
    private readonly ContextMenu _databases = new();
    private readonly SqlPillSelector _categories;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _settleTimer;
    private CancellationTokenSource _request = new();
    private IReadOnlyList<SearchHit> _applying = Array.Empty<SearchHit>();
    private int _applied;
    private string _statusTone = "";
    private bool _activating;
    private bool _ready;
    private bool _disposed;

    public SqlSearchBrowser(IServiceProvider services)
    {
        _services = services;
        foreach (var category in _providers.Aggregator.Categories) _categoryLabels[category.Id] = category.DisplayName;
        _categoryOptions = SqlSearchBrowserModel.CategoryOptions(_providers.Aggregator.Categories);
        _categories = new SqlPillSelector(_categoryOptions.Select(option => option.Label).ToArray());

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

        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋");
        clear.IsEnabled = false;
        clear.Click += (_, _) => Run(() => { _search.Clear(); _search.Focus(); });
        _search.ToolTip = "搜尋物件名稱、資料行與定義本文；名稱走模糊比對，本文是字面比對。";
        AutomationProperties.SetName(_search, "搜尋資料庫物件");
        header.Children.Add(SqlAssistChrome.CreateSearchBar(_search, clear));

        _database = SqlAssistChrome.CreateButton("資料庫", SqlAssistChrome.DefaultMetrics);
        _database.Padding = new Thickness(6, 2, 6, 2);
        _database.ToolTip = "選擇要搜尋的資料庫；只索引指名的那一個，不預先索引整台伺服器。";
        AutomationProperties.SetName(_database, "搜尋範圍的資料庫");
        _database.Click += (_, _) => Run(OpenDatabaseMenu);
        VsThemeBrushes.Apply(_databases);

        var refresh = SqlAssistChrome.CreateButton("重新整理", SqlAssistChrome.DefaultMetrics);
        refresh.Padding = new Thickness(6, 2, 6, 2);
        refresh.ToolTip = "丟掉已建立的索引並重新搜尋；改過結構之後用它。";
        refresh.Click += (_, _) => Run(() => { _providers.Invalidate(); Changed(immediate: true); });

        _scope.Margin = new Thickness(6, 0, 8, 0);
        _scope.VerticalAlignment = VerticalAlignment.Center;
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        options.Children.Add(_database);
        options.Children.Add(_scope);
        options.Children.Add(_matchCasing);
        options.Children.Add(_wholeWord);
        options.Children.Add(refresh);
        header.Children.Add(options);

        AutomationProperties.SetName(_categories, "結果分類");
        header.Children.Add(_categories);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _list.SetRowsSource(_rows, nameof(SqlSearchRow.GroupLabel));
        _list.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換 SQL Search 選取", UpdatePreview);
        _list.OpenRequested += (_, _) => _ = RunAsync(ActivateAsync);
        _list.ContextMenu = CreateRowMenu();
        // 空狀態與載入圖示疊在同一塊內容上：兩種「現在沒東西可看」不各占一塊版面。
        var content = new Grid();
        content.Children.Add(_list);
        content.Children.Add(_empty);
        _loading = new SqlLoadingSurface(content);
        _splitView = new SqlMemorySplitView(_loading, _preview, _preview.Summary);
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

        _search.TextChanged += (_, _) => Run(() =>
        {
            clear.IsEnabled = _search.Text.Length > 0;
            _model.Text = _search.Text;
            Changed();
        });
        _matchCasing.Checked += (_, _) => Run(() => { _model.MatchCasing = true; Changed(); });
        _matchCasing.Unchecked += (_, _) => Run(() => { _model.MatchCasing = false; Changed(); });
        _wholeWord.Checked += (_, _) => Run(() => { _model.WholeWord = true; Changed(); });
        _wholeWord.Unchecked += (_, _) => Run(() => { _model.WholeWord = false; Changed(); });
        _categories.SelectionChanged += (_, _) => Run(() =>
        {
            _model.CategoryId = _categoryOptions[_categories.SelectedIndex].Id;
            Changed();
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
    }

    private void OnEditorChanged(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("排入 SQL Search 連線更新", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                SqlAssistPlatformGuard.Run("更新 SQL Search 連線", () => ObserveConnection(reload: true)))));

    /// <summary>重讀目前查詢視窗的連線；換過連線就把這一輪作廢重搜。</summary>
    private void ObserveConnection(bool reload)
    {
        if (_disposed) return;

        var catalog = ResolveCatalog();
        _providers.UseCatalog(catalog);
        _model.HasConnection = catalog is not null;
        _database.IsEnabled = catalog is not null;
        _scope.Text = catalog is null
            ? ""
            : "伺服器：目前連線 · 資料庫：" + (_model.Database ?? catalog.ConnectionSource.DatabaseName);
        _scope.ToolTip = catalog is null
            ? ""
            : "範圍固定在目前查詢視窗那台伺服器；連結伺服器要的是四段式名稱那一條路，這一版還沒有索引。";

        if (reload && IsVisible) Changed(immediate: true);
        else UpdateChrome();
    }

    /// <summary>
    /// 目前查詢視窗那條連線的目錄。
    /// </summary>
    /// <remarks>
    /// 只在 UI 執行緒解析（<c>ActiveSqlEditor.Current</c> 有 UI 相依性），而且只交出<b>目錄</b>——
    /// 底下那個 <c>ISqlConnectionSource</c> 的所有權在註冊表，留一份的症狀是換過資料庫之後
    /// 每一輪都以 ObjectDisposedException 收場，而那不是 DbException，降級接不住。
    /// </remarks>
    private SqlMetadataCatalog? ResolveCatalog() => SqlAssistPlatformGuard.Probe<SqlMetadataCatalog?>(
        "取得 SQL Search 的目錄",
        () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = ActiveSqlEditor.Current;
            return view is null ? null : SqlCompletionServices.GetMetadataService(view, _services).PeekCurrentCatalog();
        },
        fallback: null);

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
            Apply(round, results.Hits);
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
        var catalog = ResolveCatalog();
        _providers.UseCatalog(catalog);
        _model.HasConnection = catalog is not null;
    }

    /// <summary>
    /// 換上新結果。
    /// </summary>
    /// <remarks>
    /// 到這裡才清空：先清再等結果的話，每打一個字清單都會閃一次空白，而上一份其實還讀得懂。
    /// 之後分批附加，一次幾十列，讓分組檢視與虛擬化面板有機會分攤版面重算。
    /// </remarks>
    private void Apply(SqlSearchRound round, IReadOnlyList<SearchHit> hits)
    {
        _model.RememberSelection((_list.SelectedItem as SqlSearchRow)?.Key);
        _settleTimer.Stop();
        _rows.Clear();
        _applying = hits;
        _applied = 0;
        AppendBatch(round);
    }

    private void ClearRows()
    {
        _settleTimer.Stop();
        _applying = Array.Empty<SearchHit>();
        _applied = 0;
        if (_rows.Count != 0) _rows.Clear();
        _preview.Select(null);
    }

    private void AppendBatch(SqlSearchRound round)
    {
        if (_disposed || !_model.IsCurrent(round)) return;

        var motion = SqlAssistChrome.MotionEnabled;
        var end = Math.Min(_applied + RowBatch, _applying.Count);

        for (; _applied < end; _applied++)
        {
            var hit = _applying[_applied];
            var label = _categoryLabels.TryGetValue(hit.CategoryId, out var text) ? text : hit.CategoryId;
            _rows.Add(new SqlSearchRow(hit, label) { IsNew = motion });
        }

        if (_applied < _applying.Count)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                SqlAssistPlatformGuard.Run("附加 SQL Search 結果", () => AppendBatch(round))));
            return;
        }

        if (motion && _rows.Count > 0) _settleTimer.Start();
        if (_model.ResolveSelection(_rows.Select(row => row.Key).ToArray(), _list.SelectedItem is not null) is { } index)
        {
            _list.SelectedIndex = index;
        }

        UpdateChrome();
    }

    private void OpenDatabaseMenu()
    {
        _databases.Items.Clear();
        _databases.Items.Add(DatabaseItem(null, "目前連線的資料庫"));

        var snapshot = _providers.HasConnection ? ResolveCatalog()?.CachedSnapshot : null;

        // 只列已經在快取裡的名稱：為了填一個下拉而去查一輪，等於在使用者沒有要求的時候連資料庫。
        if (snapshot is not null)
        {
            foreach (var database in snapshot.Databases) _databases.Items.Add(DatabaseItem(database, database));
        }

        _databases.PlacementTarget = _database;
        _databases.IsOpen = true;
    }

    private MenuItem DatabaseItem(string? database, string label)
    {
        var item = new MenuItem
        {
            Header = label,
            IsCheckable = true,
            IsChecked = string.Equals(_model.Database, database, StringComparison.Ordinal)
        };
        item.Click += (_, _) => Run(() =>
        {
            _model.Database = database;
            ObserveConnection(reload: true);
        });
        return item;
    }

    private void CopyName(SqlSearchRow? row)
    {
        if (row is null) return;
        Clipboard.SetText(row.Path.Length == 0 ? row.Title : row.Path);
        Report("已複製名稱。");
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
            Report("這一筆沒有可以開啟的定義。");
            return;
        }

        // 一次只開一個視窗。查詢加開窗要好幾秒，而那幾秒裡清單照樣可以再雙擊一次；
        // 沒有這一道就是連點兩下開出兩個查詢視窗（F12 那一條由 SqlDefinitionOpener 自己擋）。
        if (_activating) return;
        _activating = true;

        try
        {
            // 先說一句，否則雙擊之後畫面完全沒有動靜。
            Report("正在取得 " + row.Title + " 的定義…", "activating");
            var failure = await SqlSearchActivation.ActivateAsync(row.Hit, _services);
            Report(failure ?? "已在新查詢視窗開啟 " + row.Title + " 的定義。", failure is null ? "activated" : "");
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
    /// 只有兩項，而且都對得上某一條既有路徑：移至定義走 F12 那一條，複製限定名稱走
    /// 預覽那顆按鈕同一份實作。結構預覽<b>不</b>在這裡——它要的是編輯器文字裡的一段錨點，
    /// 工具窗的一列結果沒有那個東西，理由見 <see cref="SqlSearchActivation"/>。
    /// </remarks>
    private ContextMenu CreateRowMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "移至定義", Icon = SqlAssistChrome.CreateIcon(SqlIcon.Open) };
        open.Click += (_, _) => _ = RunAsync(ActivateAsync);
        var copy = new MenuItem { Header = "複製限定名稱", Icon = SqlAssistChrome.CreateIcon(SqlIcon.Copy) };
        // 預覽收起時 _preview.Current 是空的，所以這裡取清單的選取，不是預覽顯示的那一筆。
        copy.Click += (_, _) => Run(() => CopyName(_list.SelectedItem as SqlSearchRow));
        menu.Items.Add(open);
        menu.Items.Add(copy);
        VsThemeBrushes.Apply(menu);

        menu.Opened += (_, _) => SqlAssistPlatformGuard.Run("更新 SQL Search 快捷選單", () =>
        {
            var row = _list.SelectedItem as SqlSearchRow;
            open.IsEnabled = row is not null && SqlSearchActivation.CanActivate(row.Hit);
            // 空字串會畫成一個空的提示框；沒有描述就整個不掛。
            open.ToolTip = row is not null && SqlSearchActivation.Describe(row.Hit) is { Length: > 0 } description
                ? description
                : null;
            copy.IsEnabled = row is not null;
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
