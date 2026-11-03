using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Notifications;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Dictionary;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Dictionary;

/// <summary>
/// 資料字典工具窗的內容：範圍、名稱過濾、主從與資料字典兩種檢視、說明編輯與套用。
/// </summary>
/// <remarks>
/// 狀態都在 <see cref="SqlDictionaryBrowserModel"/>，這裡只接線：範圍列與 SQL Search 同一份
/// （<see cref="SqlCatalogScopeBar"/>），工具列同一份（<see cref="SqlScopeToolbar"/>），主從區同一份
/// （<see cref="MasterDetailView"/>）。換檢視只換排法，過濾與編輯不動。
///
/// 寫回只有一條路：套用 → 預覽整份指令碼 → 執行（一個交易、先比對再寫）。預覽裡可以改成拿到查詢視窗
/// 自己執行；兩條路收尾都一樣是重新載入，伺服器上已經是那一句的就不再算變更。
/// </remarks>
internal sealed class SqlDictionaryBrowser : UserControl, IDisposable
{
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    private readonly IServiceProvider _services;
    private readonly SqlDictionaryBrowserModel _model;
    private readonly SqlCatalogScopeBar _scope;
    private readonly TextBox _filter = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SqlMatchToggles _matchToggles = new();
    private readonly SqlPillSelector _views = new(DictionaryText.ViewTables, DictionaryText.ViewColumns);
    private readonly SqlFilterFlyout _conditions = new(DictionaryText.Conditions, SqlIcon.Filter);
    private readonly Button _refresh = SqlAssistChrome.CreateIconButton(SqlIcon.Refresh, DictionaryText.Refresh);
    private readonly DataGrid _objects;
    private readonly DataGrid _detail;
    private readonly DataGrid _columns;
    private readonly MasterDetailView _tablesView;
    private readonly SqlStateSurface _surface;
    private readonly TextBlock _summary = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly Button _revert = SqlAssistChrome.CreateButton(DictionaryText.Revert, SqlAssistChrome.DefaultMetrics);
    private readonly Button _apply = SqlAssistChrome.CreateButton(DictionaryText.Apply, SqlAssistChrome.DefaultMetrics, primary: true);
    private readonly DispatcherTimer _filterTimer;
    private IReadOnlyList<SqlDictionaryObjectRow> _objectRows = Array.Empty<SqlDictionaryObjectRow>();
    private IReadOnlyList<SqlDictionaryColumnRow> _detailRows = Array.Empty<SqlDictionaryColumnRow>();
    private IReadOnlyList<SqlDictionaryColumnRow> _columnRows = Array.Empty<SqlDictionaryColumnRow>();
    private CancellationTokenSource _load = new();
    private SqlDictionaryMatchTargets _matchTargets = SqlDictionaryMatchTargets.None;
    private bool _onlyMissing;
    private bool _onlyInconsistent;
    private bool _excludeViews;
    private string _databaseKey = "";
    private bool _loading;
    private bool _failed;
    private bool _applying;
    private bool _disposed;

    /// <param name="model">
    /// 換語言重建時接手上一份的模型：還沒套用的說明變更、檢視與過濾條件跟著走。範圍不跟——
    /// 伺服器的選取記在範圍列自己那一份裡，新的一份照開窗的規則重新套用查詢視窗那一條。
    /// </param>
    public SqlDictionaryBrowser(IServiceProvider services, SqlDictionaryBrowserModel? model = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _model = model ?? new SqlDictionaryBrowserModel();
        _model.Scope.ClearServers();
        _scope = new SqlCatalogScopeBar(services, _model.Scope, new SqlCatalogScopeOptions(
            MultipleDatabases: false, DictionaryText.DatabaseOptionDescription, "", NotificationKind.Dictionary));
        _scope.Changed += (_, _) => Run(Reload);
        _model.EditsChanged += OnEditsChanged;

        VsThemeBrushes.Apply(this);
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = Metrics.Body;
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        SetResourceReference(ForegroundProperty, ThemeBrush.WindowForeground);
        MinWidth = 300;

        _objects = CreateObjectGrid();
        _detail = CreateColumnGrid(showOwner: false, DictionaryText.ColumnsName);
        _columns = CreateColumnGrid(showOwner: true, DictionaryText.ColumnsName);

        var root = new DockPanel { Margin = new Thickness(SqlAssistChrome.Spacing.Group) };
        var toolbar = CreateToolbar();
        toolbar.Margin = new Thickness(0, 0, 0, SqlAssistChrome.Spacing.Group);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var footer = CreateFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _tablesView = new MasterDetailView(_objects, _detail, labels: new MasterDetailLabels(
            DictionaryText.DetailTitle, DictionaryText.ExpandDetail, DictionaryText.ExpandDetailToolTip,
            DictionaryText.CollapseDetail, DictionaryText.CollapseDetailToolTip, DictionaryText.DetailStrip));
        var views = new Grid();
        views.Children.Add(_tablesView);
        views.Children.Add(_columns);
        // 載入、還沒選、讀不到與沒有符合的疊在同一塊上：兩種檢視共用一個「現在沒東西可看」。
        _surface = new SqlStateSurface(views);
        _surface.ActionRequested += (_, _) => Run(() =>
        {
            if (_model.Scope.Servers.Count == 0) _scope.OpenServers();
            else _scope.OpenDatabases();
        });
        root.Children.Add(_surface);
        Content = root;

        _filterTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SqlAssistChrome.Debounce.Search };
        _filterTimer.Tick += (_, _) => Run(() => { _filterTimer.Stop(); FilterChanged(); });

        PreviewKeyDown += (_, e) => Run(() =>
        {
            if (e.Key != Key.F || e.KeyboardDevice.Modifiers != ModifierKeys.Control) return;
            _filter.Focus();
            e.Handled = true;
        });

        IsVisibleChanged += (_, _) => SqlAssistPlatformGuard.Run("切換資料字典可見度", () =>
        {
            if (!IsVisible) return;
            if (_model.Dictionary is null && !_loading) Run(Reload);
            _ = RunAsync(_scope.EnsureServerAsync);
        });

        RestoreFilter();
        ShowView();
        Reload();
    }

    /// <summary>換語言重建時交給新的一份。</summary>
    internal SqlDictionaryBrowserModel Model => _model;

    /// <summary>把焦點放到過濾框；命令帶使用者過來時多半是要找某個資料行。</summary>
    public void FocusFilter() => _filter.Focus();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.EditsChanged -= OnEditsChanged;
        _filterTimer.Stop();
        _load.Cancel();
        _load.Dispose();
        _scope.Dispose();
    }

    /// <summary>
    /// 工具列：上層是範圍、檢視與條件三群，下層是名稱過濾那一列。
    /// </summary>
    /// <remarks>
    /// 檢視是常駐的膠囊而不是下拉：兩種檢視來回切是比較同名資料行的主要動作。
    /// 條件收在一顆下拉裡：五個勾選攤開會佔掉一整列，而它們大部分時間都沒勾。
    /// </remarks>
    private SqlScopeToolbar CreateToolbar()
    {
        var tips = new[] { DictionaryText.ViewTablesToolTip, DictionaryText.ViewColumnsToolTip };
        for (var i = 0; i < _views.Children.Count && i < tips.Length; i++) ((FrameworkElement)_views.Children[i]).ToolTip = tips[i];
        _views.SelectionChanged += (_, _) => Run(() =>
        {
            _model.View = _views.SelectedIndex == 1 ? SqlDictionaryView.Columns : SqlDictionaryView.Tables;
            ShowView();
            Render();
        });

        VsThemeBrushes.Apply(_conditions);
        _conditions.OptionsRequested += (_, _) => Run(FillConditions);
        UpdateConditionSummary();

        var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, CommonText.ClearSearch);
        clear.IsEnabled = false;
        clear.Click += (_, _) => Run(() => { _filter.Clear(); _filter.Focus(); });
        _filter.ToolTip = DictionaryText.FilterToolTip;
        AutomationProperties.SetName(_filter, DictionaryText.FilterName);
        _filter.TextChanged += (_, _) => Run(() =>
        {
            clear.IsEnabled = _filter.Text.Length > 0;
            _filterTimer.Stop();
            _filterTimer.Start();
        });
        _matchToggles.Changed += (_, _) => Run(FilterChanged);
        _refresh.Click += (_, _) => Run(() =>
        {
            _scope.Refresh();
            Reload();
        });

        var bar = SqlAssistChrome.CreateInputBar(SqlIcon.Search, _filter, clear, _matchToggles.Buttons.ToArray());
        return new SqlScopeToolbar(
            new SqlInputRow(bar, _refresh),
            new[]
            {
                _scope.Controls.ToArray(), new FrameworkElement[] { _views }, new FrameworkElement[] { _conditions },
            });
    }

    private void OnEditsChanged(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Run("更新資料字典頁尾", UpdateFooter);

    /// <summary>接手的模型帶著過濾條件時，工具列照它擺回去；都勾等於都沒勾，擺成沒勾。</summary>
    private void RestoreFilter()
    {
        var filter = _model.Filter;
        _matchTargets = filter.Targets == SqlDictionaryMatchTargets.Both ? SqlDictionaryMatchTargets.None : filter.Targets;
        _onlyMissing = filter.OnlyMissingDescriptions;
        _onlyInconsistent = filter.OnlyInconsistent;
        _excludeViews = !filter.IncludeViews;
        _matchToggles.Options = filter.Options;
        _filter.Text = filter.Text;
        _filterTimer.Stop();
        _views.SelectedIndex = _model.View == SqlDictionaryView.Columns ? 1 : 0;
        UpdateConditionSummary();
    }

    /// <summary>條件下拉：比對哪一種名稱、只看哪幾種資料行；沒勾就是不限。</summary>
    private void FillConditions()
    {
        SqlFilterOption Option(string label, string toolTip, bool selected, Action<bool> set) =>
            new(label, toolTip, selected, value => Run(() =>
            {
                set(value);
                UpdateConditionSummary();
                FilterChanged();
            }));

        _conditions.SetOptions(new[]
        {
            new SqlFilterGroup(DictionaryText.ConditionsMatchGroup, new[]
            {
                Option(DictionaryText.MatchObjects, DictionaryText.MatchObjectsToolTip,
                    _matchTargets.HasFlag(SqlDictionaryMatchTargets.Objects), value => SetTarget(SqlDictionaryMatchTargets.Objects, value)),
                Option(DictionaryText.MatchColumns, DictionaryText.MatchColumnsToolTip,
                    _matchTargets.HasFlag(SqlDictionaryMatchTargets.Columns), value => SetTarget(SqlDictionaryMatchTargets.Columns, value)),
            }),
            new SqlFilterGroup(DictionaryText.ConditionsShowGroup, new[]
            {
                Option(DictionaryText.OnlyMissing, DictionaryText.OnlyMissingToolTip, _onlyMissing, value => _onlyMissing = value),
                Option(DictionaryText.OnlyInconsistent, DictionaryText.OnlyInconsistentToolTip, _onlyInconsistent, value => _onlyInconsistent = value),
                Option(DictionaryText.ExcludeViews, DictionaryText.ExcludeViewsToolTip, _excludeViews, value => _excludeViews = value),
            }),
        });
    }

    private void SetTarget(SqlDictionaryMatchTargets target, bool selected) =>
        _matchTargets = selected ? _matchTargets | target : _matchTargets & ~target;

    private void UpdateConditionSummary()
    {
        var labels = new List<string>();
        if (_matchTargets.HasFlag(SqlDictionaryMatchTargets.Objects)) labels.Add(DictionaryText.MatchObjects);
        if (_matchTargets.HasFlag(SqlDictionaryMatchTargets.Columns)) labels.Add(DictionaryText.MatchColumns);
        if (_onlyMissing) labels.Add(DictionaryText.OnlyMissing);
        if (_onlyInconsistent) labels.Add(DictionaryText.OnlyInconsistent);
        if (_excludeViews) labels.Add(DictionaryText.ExcludeViews);

        _conditions.UpdateSummary(
            labels.Count switch { 0 => "", 1 => labels[0], _ => DictionaryText.ConditionCount(labels.Count) },
            string.Join(CommonText.ListSeparator, labels));
        _conditions.HasSelection = labels.Count > 0;
    }

    /// <summary>頁尾：左邊數量與還沒套用的變更，右邊復原與套用（主要動作）。</summary>
    private DockPanel CreateFooter()
    {
        _revert.ToolTip = DictionaryText.RevertToolTip;
        _revert.Click += (_, _) => Run(_model.RevertAll);
        _apply.Click += (_, _) => _ = RunAsync(ApplyAsync);

        var footer = SqlAssistChrome.CreateDialogFooter(_summary, _revert, _apply);
        footer.Margin = new Thickness(0, SqlAssistChrome.Spacing.Group, 0, 0);
        UpdateFooter();
        return footer;
    }

    private DataGrid CreateObjectGrid()
    {
        var grid = CreateGrid(DictionaryText.ObjectsName);
        grid.Columns.Add(MarkColumn());
        grid.Columns.Add(TextColumn(DictionaryText.ObjectHeader, nameof(SqlDictionaryObjectRow.Name), 180));
        grid.Columns.Add(TextColumn(CommonText.Kind, nameof(SqlDictionaryObjectRow.Kind), 70));
        grid.Columns.Add(TextColumn(DictionaryText.CoverageHeader, nameof(SqlDictionaryObjectRow.Coverage), 70,
            headerToolTip: DictionaryText.CoverageHeaderToolTip, alignment: TextAlignment.Right));
        grid.Columns.Add(DescriptionColumn(DictionaryText.TableDescription, suggestion: false));

        grid.SelectionChanged += (_, _) => SqlAssistPlatformGuard.Run("切換資料字典資料表", RenderDetail);

        var revert = MenuItem(DictionaryText.RevertSelected, () =>
            _model.Revert(SelectedObjects().SelectMany(row => row.Object.Columns.Select(row.Object.TargetOf).Prepend(row.Target))));
        var copy = MenuItem(DictionaryText.CopyNames, () => CopyNames(SelectedObjects().Select(row => row.Target)));
        grid.ContextMenu = CreateMenu(revert, copy);
        grid.ContextMenu.Opened += (_, _) => revert.IsEnabled = SelectedObjects().Any(row => row.HasChanges);
        grid.PreviewKeyDown += (_, e) => Run(() =>
        {
            if (e.Key != Key.Delete || IsEditing(grid)) return;
            _model.SetDescriptions(SelectedObjects().Select(row => new KeyValuePair<SqlDescriptionTarget, string>(row.Target, "")).ToList());
            e.Handled = true;
        });
        return grid;
    }

    /// <summary>資料行清單；主從的明細與資料字典檢視是同一種，只差要不要列出資料表。</summary>
    private DataGrid CreateColumnGrid(bool showOwner, string name)
    {
        var grid = CreateGrid(name);
        grid.Columns.Add(MarkColumn());
        if (showOwner) grid.Columns.Add(TextColumn(DictionaryText.ObjectHeader, nameof(SqlDictionaryColumnRow.ObjectName), 150));
        grid.Columns.Add(TextColumn(DictionaryText.ColumnHeader, nameof(SqlDictionaryColumnRow.Name), 140));
        grid.Columns.Add(TextColumn(DictionaryText.TypeHeader, nameof(SqlDictionaryColumnRow.DataType), 110));
        grid.Columns.Add(TextColumn(DictionaryText.NullableHeader, nameof(SqlDictionaryColumnRow.Nullability), 50));
        grid.Columns.Add(TextColumn(DictionaryText.TraitsHeader, nameof(SqlDictionaryColumnRow.Traits), 90));
        grid.Columns.Add(SameNameColumn());
        grid.Columns.Add(DescriptionColumn(CommonText.Description, suggestion: true));

        IReadOnlyList<SqlDictionaryColumnRow> Rows() => ReferenceEquals(grid, _columns) ? _columnRows : _detailRows;
        List<SqlDictionaryColumnRow> Selected() => grid.SelectedItems.OfType<SqlDictionaryColumnRow>().ToList();

        var suggest = MenuItem(DictionaryText.ApplySuggestion, () => Suggest(Selected()), DictionaryText.ApplySuggestionToolTip);
        var paste = MenuItem(DictionaryText.PasteDescriptions, () => Paste(grid, Rows()), DictionaryText.PasteDescriptionsToolTip);
        var revert = MenuItem(DictionaryText.RevertSelected, () => _model.Revert(Selected().Select(row => row.Target)));
        var copy = MenuItem(DictionaryText.CopyNames, () => CopyNames(Selected().Select(row => row.Target)));
        grid.ContextMenu = CreateMenu(suggest, paste, revert, copy);
        grid.ContextMenu.Opened += (_, _) =>
        {
            var selected = Selected();
            suggest.IsEnabled = selected.Any(row => row.Suggestion is not null);
            revert.IsEnabled = selected.Any(row => row.IsChanged);
            paste.IsEnabled = Rows().Count > 0;
        };

        // 貼上與刪除只在不是編輯中時接手：編輯中的那一格是一般的文字框，Ctrl+V 與 Delete 照它的意思。
        grid.PreviewKeyDown += (_, e) => Run(() =>
        {
            if (IsEditing(grid)) return;
            if (e.Key == Key.V && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                Paste(grid, Rows());
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && e.KeyboardDevice.Modifiers == ModifierKeys.None)
            {
                _model.SetDescriptions(Selected().Select(row => new KeyValuePair<SqlDescriptionTarget, string>(row.Target, "")).ToList());
                e.Handled = true;
            }
        });
        return grid;
    }

    private static DataGrid CreateGrid(string name)
    {
        var grid = SqlAssistChrome.CreateDataGrid(Metrics);
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.CanUserReorderColumns = false;
        grid.CanUserSortColumns = false;
        grid.EnableRowVirtualization = true;
        grid.EnableColumnVirtualization = true;
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        AutomationProperties.SetName(grid, name);
        return grid;
    }

    private static DataGridTextColumn TextColumn(string header, string path, double width,
        string? headerToolTip = null, TextAlignment alignment = TextAlignment.Left) =>
        new()
        {
            Header = headerToolTip is null ? header : new TextBlock { Text = header, ToolTip = headerToolTip },
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            ElementStyle = SqlAssistChrome.CreateCellTextStyle(alignment),
            Width = new DataGridLength(width),
        };

    /// <summary>還沒套用的記號；強調色，一眼看得出這一頁哪幾格動過。</summary>
    private static DataGridTextColumn MarkColumn()
    {
        var style = new Style(typeof(TextBlock), SqlAssistChrome.CreateCellTextStyle(TextAlignment.Center));
        style.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.LinkForeground));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, DictionaryText.ChangedToolTip));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        return new DataGridTextColumn
        {
            Binding = new Binding(nameof(SqlDictionaryColumnRow.ChangeMark)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            ElementStyle = style,
            Width = new DataGridLength(20),
            CanUserResize = false,
        };
    }

    /// <summary>同名那一格：不一致時換警示色，停留時列出型別與每一種寫法。</summary>
    private static DataGridTextColumn SameNameColumn()
    {
        var style = new Style(typeof(TextBlock), SqlAssistChrome.CreateCellTextStyle());
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(SqlDictionaryColumnRow.SameNameDetail))));
        var inconsistent = new DataTrigger { Binding = new Binding(nameof(SqlDictionaryColumnRow.IsInconsistent)), Value = true };
        inconsistent.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.MeterWarning));
        style.Triggers.Add(inconsistent);

        return new DataGridTextColumn
        {
            Header = new TextBlock { Text = DictionaryText.SameNameHeader, ToolTip = DictionaryText.SameNameHeaderToolTip },
            Binding = new Binding(nameof(SqlDictionaryColumnRow.SameName)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            ElementStyle = style,
            Width = new DataGridLength(120),
        };
    }

    private static DataGridTextColumn DescriptionColumn(string header, bool suggestion)
    {
        var hint = new Style(typeof(TextBlock), SqlAssistChrome.CreateCellTextStyle());
        hint.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.DimForeground));
        hint.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
        hint.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));

        return new SqlDescriptionColumn
        {
            Header = header,
            Binding = new Binding(nameof(SqlDictionaryColumnRow.Description)) { Mode = BindingMode.TwoWay },
            ElementStyle = SqlAssistChrome.CreateCellTextStyle(),
            EditingElementStyle = SqlAssistChrome.CreateCellEditorStyle(),
            HintStyle = suggestion ? hint : null,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 160,
        };
    }

    private static MenuItem MenuItem(string header, Action action, string? toolTip = null)
    {
        var item = new MenuItem { Header = header, ToolTip = toolTip };
        item.Click += (_, _) => SqlAssistPlatformGuard.Run("資料字典選單：" + header, action);
        return item;
    }

    private static ContextMenu CreateMenu(params MenuItem[] items)
    {
        var menu = new ContextMenu();
        VsThemeBrushes.Apply(menu);
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    private static bool IsEditing(DataGrid grid) =>
        Keyboard.FocusedElement is TextBox box && grid.IsAncestorOf(box);

    private IEnumerable<SqlDictionaryObjectRow> SelectedObjects() => _objects.SelectedItems.OfType<SqlDictionaryObjectRow>();

    /// <summary>
    /// 範圍換了或按了重新整理：換成那一個資料庫的編輯，在背景重讀一份字典。
    /// </summary>
    /// <remarks>
    /// 上一輪還沒回來就取消：換資料庫之後回來的那一份是上一個資料庫的。
    /// 同一個資料庫重讀時編輯留著，伺服器上已經是那一句的由模型收掉。
    /// </remarks>
    private void Reload()
    {
        if (_disposed) return;

        _load.Cancel();
        _load.Dispose();
        _load = new CancellationTokenSource();

        var connection = _scope.Observe();
        var server = _scope.Selection.ServerName;
        var database = _model.Scope.Databases.Count == 0 ? null : _model.Scope.Databases[0];
        var key = (server ?? "") + "\u001F" + (database ?? "");
        if (!string.Equals(key, _databaseKey, StringComparison.OrdinalIgnoreCase))
        {
            _databaseKey = key;
            _model.UseDatabase(server, database);
        }

        // 選了資料庫卻解析不到目錄：那一台連不上，與讀不到同一個說法；還沒選的情況由模型先擋下。
        var catalog = _scope.ResolveDatabaseCatalog(connection);
        _failed = catalog is null;
        _loading = catalog is not null;
        Render();
        if (catalog is not null) _ = RunAsync(() => LoadAsync(catalog, _load.Token));
    }

    private async Task LoadAsync(SqlMetadataCatalog catalog, CancellationToken token)
    {
        var source = catalog.ConnectionSource;
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.LoadingDataDictionary,
            NotificationKind.Dictionary, NotificationOrigin.User, NotificationLevel.Info, source.DatabaseName);

        SqlDataDictionary? dictionary;
        try
        {
            dictionary = await Task.Run(() => SqlDataDictionaryLoader.TryLoad(source, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            notification.Cancel();
            return;
        }

        if (_disposed || token.IsCancellationRequested)
        {
            notification.Cancel();
            return;
        }

        _loading = false;
        _failed = dictionary is null;
        if (dictionary is null) notification.Fail();
        else notification.Report(DictionaryText.Loaded(dictionary.Objects.Count, dictionary.ColumnCount));

        _model.Load(dictionary);
        Render();
    }

    private void FilterChanged()
    {
        _model.Filter = new SqlDictionaryFilter(_filter.Text, _matchToggles.Options, _matchTargets,
            _onlyMissing, _onlyInconsistent, includeViews: !_excludeViews);
        Render();
    }

    private void ShowView()
    {
        var columns = _model.View == SqlDictionaryView.Columns;
        _tablesView.Visibility = columns ? Visibility.Collapsed : Visibility.Visible;
        _columns.Visibility = columns ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>照目前的過濾與檢視重排清單；主清單選的那一張照目標留著。</summary>
    /// <remarks>看不到的那一種檢視不排：資料字典檢視要把幾萬個資料行照名稱排一次，切過去時才做。</remarks>
    private void Render()
    {
        var selected = (_objects.SelectedItem as SqlDictionaryObjectRow)?.Target;
        int visible;

        if (_model.View == SqlDictionaryView.Tables)
        {
            _objectRows = _model.MatchingObjects();
            _objects.ItemsSource = _objectRows;
            _objects.SelectedItem = _objectRows.FirstOrDefault(row => Equals(row.Target, selected)) ?? _objectRows.FirstOrDefault();
            RenderDetail();
            visible = _objectRows.Count;
        }
        else
        {
            _columnRows = _model.MatchingColumns();
            _columns.ItemsSource = _columnRows;
            visible = _columnRows.Count;
        }

        _surface.State = _model.Surface(_loading, _failed, visible);
        UpdateFooter();
    }

    private void RenderDetail()
    {
        _detailRows = _objects.SelectedItem is SqlDictionaryObjectRow row
            ? _model.ColumnsOf(row.Object)
            : Array.Empty<SqlDictionaryColumnRow>();
        _detail.ItemsSource = _detailRows;
    }

    private void UpdateFooter()
    {
        var dictionary = _model.Dictionary;
        _summary.Text = dictionary is null ? "" : _model.FooterSummary(dictionary.Objects.Count, dictionary.ColumnCount);

        var edits = _model.Edits;
        _revert.IsEnabled = edits.Count > 0 && !_applying;
        _apply.IsEnabled = edits.Count > 0 && !edits.HasTooLong && !_applying && dictionary is not null;
        _apply.ToolTip = edits.HasTooLong ? DictionaryText.ApplyTooLong(SqlDescriptionValue.MaxLength) : DictionaryText.ApplyToolTip;
    }

    /// <summary>
    /// 從剪貼簿貼一張表；Excel 複製出來的 TSV 照 <see cref="SqlDescriptionPaste"/> 的規則填。
    /// </summary>
    private void Paste(DataGrid grid, IReadOnlyList<SqlDictionaryColumnRow> rows)
    {
        if (rows.Count == 0) return;

        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (ExternalException exception)
        {
            Notify(NotificationCatalog.PastingDescriptions, NotificationStatus.Failed, message: exception.Message);
            return;
        }

        var start = grid.CurrentItem is SqlDictionaryColumnRow current ? IndexOf(rows, current) : grid.SelectedIndex;
        var (applied, skipped) = _model.Paste(rows, Math.Max(start, 0), text);
        Notify(NotificationCatalog.PastingDescriptions, NotificationStatus.Succeeded, message: DictionaryText.Pasted(applied, skipped));
    }

    private static int IndexOf(IReadOnlyList<SqlDictionaryColumnRow> rows, SqlDictionaryColumnRow row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row)) return i;
        }

        return -1;
    }

    private void Suggest(IReadOnlyList<SqlDictionaryColumnRow> rows)
    {
        var count = _model.ApplySuggestions(rows);
        Notify(NotificationCatalog.RunningDictionaryAction, NotificationStatus.Succeeded, message: DictionaryText.Suggested(count));
    }

    /// <summary>複製選定那幾列的完整名稱，一行一個；貼到試算表寫好說明之後可以整份貼回來。</summary>
    private void CopyNames(IEnumerable<SqlDescriptionTarget> targets)
    {
        var text = string.Join("\r\n", targets.Select(target => target.ToString()));
        if (text.Length == 0) return;

        _ = RunAsync(async () =>
        {
            if (await SqlClipboard.WriteTextAsync(text).ConfigureAwait(true) is { } failure)
                Notify(NotificationCatalog.RunningDictionaryAction, NotificationStatus.Failed, message: failure);
        });
    }

    /// <summary>
    /// 套用：預覽整份指令碼，確認後在一個交易裡寫回；也可以改成拿到查詢視窗。
    /// </summary>
    /// <remarks>
    /// 變更在打開預覽時就定下來：預覽看到的與執行的必須是同一份。
    /// 寫回之後一律重新載入——成功的那幾項由模型照新載入的值收掉；被擋下時把伺服器上現在的值換成原值，
    /// 讓使用者看過之後再按一次。
    /// </remarks>
    private async Task ApplyAsync()
    {
        var dictionary = _model.Dictionary;
        var changes = _model.Edits.Changes;
        if (dictionary is null || changes.Count == 0 || _model.Edits.HasTooLong) return;

        var server = _scope.Selection.ServerName ?? "";
        var database = dictionary.DatabaseName;
        var script = SqlDescriptionScript.Build(database, changes, Environment.NewLine);
        var choice = new SqlDictionaryApplyWindow(DictionaryText.ApplySummary(changes.Count, server, database), script).Ask();

        if (choice == SqlDictionaryApplyChoice.OpenInQueryWindow)
        {
            OpenScript(script, database);
            return;
        }

        if (choice != SqlDictionaryApplyChoice.Execute) return;
        if (_scope.ResolveDatabaseCatalog(_scope.Observe()) is not { } catalog) return;

        _applying = true;
        UpdateFooter();
        try
        {
            await WriteAsync(catalog, dictionary, changes).ConfigureAwait(true);
        }
        finally
        {
            _applying = false;
            UpdateFooter();
        }
    }

    private async Task WriteAsync(SqlMetadataCatalog catalog, SqlDataDictionary dictionary, IReadOnlyList<SqlDescriptionChange> changes)
    {
        var source = catalog.ConnectionSource;
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.ApplyingDescriptions,
            NotificationKind.Dictionary, NotificationOrigin.User, NotificationLevel.Info, dictionary.DatabaseName);

        var result = await Task.Run(() => SqlDescriptionWriter.Write(source, changes, CancellationToken.None)).ConfigureAwait(true);
        if (_disposed) return;

        switch (result.Status)
        {
            case SqlDescriptionWriteStatus.Applied:
                notification.Report(DictionaryText.Applied(changes.Count));
                // 補全與預覽快取的是改之前的說明；只丟改到的那幾個物件。
                foreach (var item in dictionary.Objects.Where(item => changes.Any(change => SameObject(change.Target, item))))
                    catalog.InvalidateObject(item.ObjectId);
                Reload();
                break;

            case SqlDescriptionWriteStatus.Conflicted:
                notification.Degrade();
                notification.Report(DictionaryText.Conflicted(result.Conflicts.Count));
                _model.Rebase(result.Conflicts);
                Reload();
                break;

            default:
                notification.Fail();
                Notify(NotificationCatalog.ApplyingDescriptions, NotificationStatus.Failed, dictionary.DatabaseName, result.Message ?? "");
                break;
        }
    }

    private static bool SameObject(SqlDescriptionTarget target, SqlDictionaryObject item) =>
        target.ObjectKind == item.Kind
        && string.Equals(target.SchemaName, item.SchemaName, StringComparison.Ordinal)
        && string.Equals(target.ObjectName, item.Name, StringComparison.Ordinal);

    /// <summary>
    /// 把指令碼寫進新的查詢視窗；字典那一台與查詢視窗不是同一台時開未連線的視窗，免得在別台上執行。
    /// </summary>
    private void OpenScript(string script, string database)
    {
        var view = ActiveSqlEditor.Current;
        var selection = _scope.Selection;
        var unconnected = view is null || !SqlCatalogSelection.IsSameServer(selection.ServerName, selection.ActiveEditorServerName());
        var failure = SqlDefinitionScript.WriteToNewWindow(_services, new SqlObjectScriptText(script, 0), database,
            DictionaryText.ScriptName, view is null ? "" : ActiveSqlEditor.GetDocumentName(view), unconnected);
        if (failure is not null) Notify(NotificationCatalog.RunningDictionaryAction, NotificationStatus.Failed, database, failure);
    }

    private void Notify(string title, NotificationStatus status, string subject = "", string message = "") =>
        NotificationCenter.Default.Post(title, NotificationKind.Dictionary, NotificationOrigin.User, NotificationLevel.Info,
            status, subject.Length == 0 ? _model.Dictionary?.DatabaseName ?? "" : subject, message: message);

    // 使用者主動觸發的失敗要看得見，所以這裡不是 SqlAssistPlatformGuard 而是送通知。
    private void Run(Action action) => _ = RunAsync(() => { action(); return Task.CompletedTask; });

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SqlAssistDiagnostics.WriteAlways("資料字典操作失敗：" + error.Message);
            if (!_disposed) Notify(NotificationCatalog.RunningDictionaryAction, NotificationStatus.Failed, message: error.Message);
        }
    }

    /// <summary>
    /// 說明欄：一般的文字欄，空著而同名的寫法一致時，淡色斜體列出可以照抄的那一句。
    /// </summary>
    /// <remarks>
    /// 只換顯示那一半：編輯、確認與 Esc 取消照 <see cref="DataGridTextColumn"/> 原本的規則。
    /// 建議疊在原本的文字上而不是換掉它：它不是值，複製這一格不該帶走它。
    /// </remarks>
    private sealed class SqlDescriptionColumn : DataGridTextColumn
    {
        public Style? HintStyle { get; set; }

        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
        {
            var text = base.GenerateElement(cell, dataItem);
            if (HintStyle is null) return text;

            var hint = new TextBlock { Style = HintStyle };
            hint.SetBinding(TextBlock.TextProperty, new Binding(nameof(SqlDictionaryColumnRow.Suggestion)) { Mode = BindingMode.OneWay });
            var layers = new Grid();
            layers.Children.Add(text);
            layers.Children.Add(hint);
            return layers;
        }
    }
}
