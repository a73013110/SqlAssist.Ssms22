using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 收藏的版本歷史：左（窄窗時上）是版本時間軸，右（下）是選中版本與目前版本的差異或全文。
/// </summary>
/// <remarks>
/// 從收藏列操作開啟的單一對話框：原生 Titlebar、單一頁尾，不另開浮動預覽，也沒有自己的位置設定。
/// 讀取與比對都在背景；切換選取時取消舊讀取，選取識別與宿主世代一起擋住晚到的結果。
/// 回溯經 <see cref="SqlFavoriteRevisionCommands"/> 走既有的改 SQL 路徑，成功後重讀收藏與時間軸，
/// 新版本以揭露動畫出現在最上面並被選取。
/// </remarks>
internal sealed class FavoriteRevisionsWindow : DialogWindow
{
    private const double WideLayoutWidth = 760;
    private const int ContentCacheLimit = 16;

    private readonly SqlFavoriteRevisionCommands _commands;
    private readonly SqlFavoriteRevisionTimeline _timeline;
    private readonly ObservableCollection<SqlFavoriteRevisionRow> _rows = new();
    private readonly SqlFavoriteRevisionList _list = new();
    private readonly SqlMemoryPager _pager = new();
    private readonly SqlStateSurface _timelineLoading;
    private readonly SqlTextDiffView _diff = new();
    private readonly SqlReadOnlyViewer _viewer = new();
    private readonly SqlStateSurface _contentLoading;
    private readonly SqlPillSelector _mode = new(("差異", SqlIcon.Compare), ("全文", SqlIcon.Preview));
    private readonly TextBlock _comparison = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _notice = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _placeholder = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly WrapPanel _actions = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly List<(Button Button, SqlFavoriteRevisionCommand Command)> _actionButtons = new();
    private readonly List<(MenuItem Item, SqlFavoriteRevisionCommand Command)> _menuItems = new();
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly Grid _body = new();
    private readonly FrameworkElement _timelinePane;
    private readonly FrameworkElement _detailPane;
    private readonly DispatcherTimer _delay;
    private readonly DispatcherTimer _settle;
    private readonly Dictionary<string, SqlContent> _contents = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _retainedLimit;
    private CancellationTokenSource _read = new();
    private SqlFavoriteItem _favorite;
    private SqlFavoriteRevisionRow? _selected;
    private string? _selectedSql;
    private bool _wide;
    private bool _changed;
    private bool _closed;

    private FavoriteRevisionsWindow(SqlAssistPackage package, SqlFavoriteItem favorite)
    {
        _favorite = favorite;
        _commands = new SqlFavoriteRevisionCommands(package);
        _retainedLimit = SqlAssistSettingsStore.Current.SqlMemoryMaxFavoriteRevisions;
        _timeline = new SqlFavoriteRevisionTimeline(favorite.Favorite.FavoriteId, favorite.ContentId, _retainedLimit);
        SqlMemoryActions.ConfigureWindow(this, package, "版本歷史 — " + favorite.Favorite.Name, 1040, 680);

        var root = new DockPanel { Margin = SqlAssistChrome.DialogPadding };
        var retention = SqlAssistChrome.CreateMetadataText(
            "每個收藏只保留最近 " + _retainedLimit.ToString(CultureInfo.InvariantCulture) +
            " 版，更舊的版本會被回收；回溯會另存一筆新版本，不改寫或刪除任何版本。", SqlAssistChrome.DefaultMetrics);
        retention.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(retention, Dock.Top); root.Children.Add(retention);

        // 檢視型對話框：回溯等動作都在內容工具列，頁尾只有結束對話框的關閉，它就是主要動作。
        var close = SqlAssistChrome.CreateButton("關閉", SqlAssistChrome.DefaultMetrics, primary: true);
        close.IsCancel = true; close.IsDefault = true;
        var footer = SqlAssistChrome.CreateDialogFooter(_status, close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        _list.SetRowsSource(_rows, _pager);
        _list.ContextMenu = CreateContextMenu();
        AutomationProperties.SetName(_list, "版本時間軸");
        _timelineLoading = new SqlStateSurface(_list);
        _timelinePane = _timelineLoading;

        _contentLoading = new SqlStateSurface(CreateContent());
        _detailPane = CreateDetailPane(_contentLoading);
        _body.Children.Add(_timelinePane); _body.Children.Add(_detailPane);
        root.Children.Add(_body);
        Content = root;

        _delay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
        _delay.Tick += (_, _) => { _delay.Stop(); _ = SqlMemoryActions.RunAsync(ReadSelectionAsync, Report); };
        _settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = SqlAssistChrome.CardEnterDuration + TimeSpan.FromMilliseconds(60)
        };
        _settle.Tick += (_, _) => SqlMemoryActions.Run(() =>
        {
            _settle.Stop();
            foreach (var row in _rows) row.IsNew = false;
        }, Report);

        _list.SelectionChanged += (_, _) => SqlMemoryActions.Run(() => Select(_list.SelectedItem as SqlFavoriteRevisionRow), Report);
        _list.RowActionRequested += action => SqlMemoryActions.Run(() => Run(action), Report);
        _list.OpenRequested += (_, _) => SqlMemoryActions.Run(() => Run(SqlFavoriteRevisionAction.Open), Report);
        _list.LoadMoreRequested += (_, _) => _ = SqlMemoryActions.RunAsync(LoadMoreAsync, Report);
        _pager.LoadMoreRequested += (_, _) => _ = SqlMemoryActions.RunAsync(LoadMoreAsync, Report);
        _mode.SelectionChanged += (_, _) => SqlMemoryActions.Run(ShowMode, Report);
        _commands.FavoriteChanged += () => _ = SqlMemoryActions.RunAsync(ReloadFavoriteAsync, Report);
        _body.SizeChanged += (_, _) => SqlMemoryActions.Run(UpdateLayoutMode, Report);
        Loaded += (_, _) => SqlMemoryActions.Run(() => _list.Focus(), Report);
        Closing += OnClosing;
        Closed += (_, _) => Dispose();

        UpdateLayoutMode();
        Select(null);
        _ = SqlMemoryActions.RunAsync(() => LoadFirstPageAsync(selectFirst: true), Report);
    }

    /// <returns>收藏在這段期間是否可能改變（回溯、版本衝突或回應不明）；呼叫端據此重讀清單列。</returns>
    public static bool Show(SqlAssistPackage package, SqlFavoriteItem favorite)
    {
        var window = new FavoriteRevisionsWindow(package, favorite);
        window.ShowModal();
        return window._changed;
    }

    private FrameworkElement CreateDetailPane(UIElement content)
    {
        var pane = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        DockPanel.SetDock(toolbar, Dock.Top); pane.Children.Add(toolbar);
        foreach (var command in SqlFavoriteRevisionCommand.All)
        {
            // 全文模式已由左側「全文」膠囊切換；面板不再放一顆同義的預覽按鈕。
            if (command.Action == SqlFavoriteRevisionAction.Preview) continue;
            var action = command.Action;
            var button = SqlAssistChrome.CreateIconButton(command.Icon, command.Label, command.Tone);
            if (command.IsSeparated) button.Margin = new Thickness(6, 0, 0, 0);
            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += (_, _) => SqlMemoryActions.Run(() => Run(action), Report);
            _actionButtons.Add((button, command)); _actions.Children.Add(button);
        }
        DockPanel.SetDock(_actions, Dock.Right); toolbar.Children.Add(_actions);
        AutomationProperties.SetName(_mode, "呈現方式");
        DockPanel.SetDock(_mode, Dock.Left); toolbar.Children.Add(_mode);
        _comparison.Margin = new Thickness(8, 0, 8, 0);
        toolbar.Children.Add(_comparison);

        _notice.Margin = new Thickness(0, 0, 0, 8);
        _notice.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_notice, Dock.Top); pane.Children.Add(_notice);
        pane.Children.Add(SqlAssistChrome.CreateSurface(content));
        return pane;
    }

    private UIElement CreateContent()
    {
        var host = new Grid();
        host.Children.Add(_diff);
        _viewer.Visibility = Visibility.Collapsed;
        _viewer.ReportError = Report;
        host.Children.Add(_viewer);
        _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.TextAlignment = TextAlignment.Center;
        _placeholder.Margin = new Thickness(16);
        host.Children.Add(_placeholder);
        return host;
    }

    /// <summary>快捷選單與列按鈕同一份清單；不適用的項目收起，不能執行的停用並保留說明。</summary>
    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        foreach (var command in SqlFavoriteRevisionCommand.All)
        {
            if (command.IsSeparated) menu.Items.Add(new Separator());
            var action = command.Action;
            var item = new MenuItem { Header = command.Label, Icon = SqlAssistChrome.CreateIcon(command.Icon) };
            item.Click += (_, _) => SqlMemoryActions.Run(() => Run(action), Report);
            menu.Items.Add(item); _menuItems.Add((item, command));
        }
        VsThemeBrushes.Apply(menu);
        menu.Opened += (_, _) => SqlMemoryActions.Run(() =>
        {
            var row = _list.SelectedItem as SqlFavoriteRevisionRow;
            foreach (var (item, command) in _menuItems)
            {
                item.Visibility = row is null || (command.HiddenOnCurrent && row.IsCurrent) ? Visibility.Collapsed : Visibility.Visible;
                item.IsEnabled = !_commands.IsBusy && SqlFavoriteRevisionCommands.CanRun(command.Action, row);
                if (command.Action == SqlFavoriteRevisionAction.Revert) item.Header = row?.RevertLabel ?? command.Label;
            }
        }, Report);
        return menu;
    }

    /// <summary>寬窗左右並排；窄窗改上下疊放，時間軸在上。不隨寬度來回交換閱讀方向以外的東西。</summary>
    private void UpdateLayoutMode()
    {
        var wide = _body.ActualWidth <= 0 || _body.ActualWidth >= WideLayoutWidth;
        if (wide == _wide && _body.ColumnDefinitions.Count + _body.RowDefinitions.Count > 0) return;
        _wide = wide;
        _body.ColumnDefinitions.Clear(); _body.RowDefinitions.Clear();
        if (wide)
        {
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 240 });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });
            _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 160 });
        }
        Grid.SetColumn(_detailPane, wide ? 2 : 0); Grid.SetRow(_detailPane, wide ? 0 : 2);
        Grid.SetColumn(_timelinePane, 0); Grid.SetRow(_timelinePane, 0);
    }

    private async Task LoadFirstPageAsync(bool selectFirst)
    {
        if (_closed) return;
        var request = _timeline.BeginFirstPage(out var generation);
        var hostGeneration = SqlMemoryHost.Runtime.Generation;
        _timelineLoading.IsLoading = _rows.Count == 0;
        UpdateFooter();
        try
        {
            var page = await SqlMemoryHost.Runtime.ReadFavoriteRevisionsAsync(request, _lifetime.Token);
            if (!IsCurrentHost(hostGeneration) || _timeline.Accept(generation, page) is not { } appeared) return;
            var previous = _rows.ToDictionary(row => row.Item.RevisionId);
            var selectedId = (_list.SelectedItem as SqlFavoriteRevisionRow)?.Item.RevisionId;
            var motion = SqlAssistChrome.MotionEnabled;
            var fresh = new HashSet<Guid>(appeared.Select(item => item.RevisionId));
            _rows.Clear();
            foreach (var item in _timeline.Items)
            {
                var row = new SqlFavoriteRevisionRow(item) { IsNew = motion && fresh.Contains(item.RevisionId) };
                if (previous.TryGetValue(item.RevisionId, out var old)) row.ContentMissing = old.ContentMissing;
                _rows.Add(row);
            }
            Settle(motion && fresh.Count > 0);
            UpdateRows();
            var index = selectFirst ? 0 : _rows.ToList().FindIndex(row => row.Item.RevisionId == selectedId);
            _list.SelectedIndex = _rows.Count == 0 ? -1 : Math.Max(0, index);
            if (_rows.Count == 0) Select(null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _timeline.Fail(generation);
            if (IsCurrentHost(hostGeneration)) Report(SqlMemoryTimeText.Failure("載入版本", error));
        }
        finally
        {
            if (!_closed) { _timelineLoading.IsLoading = false; UpdateFooter(); }
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_closed || _timeline.BeginNextPage(out var generation) is not { } request) return;
        var hostGeneration = SqlMemoryHost.Runtime.Generation;
        UpdateFooter();
        try
        {
            var page = await SqlMemoryHost.Runtime.ReadFavoriteRevisionsAsync(request, _lifetime.Token);
            if (!IsCurrentHost(hostGeneration) || _timeline.Accept(generation, page) is not { } appeared) return;
            var motion = SqlAssistChrome.MotionEnabled;
            foreach (var item in appeared) _rows.Add(new SqlFavoriteRevisionRow(item) { IsNew = motion });
            Settle(motion && appeared.Count > 0);
            UpdateRows();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _timeline.Fail(generation);
            if (IsCurrentHost(hostGeneration)) Report(SqlMemoryTimeText.Failure("載入更多版本", error));
        }
        finally
        {
            if (!_closed) UpdateFooter();
        }
    }

    /// <summary>回溯或衝突之後：先重讀收藏拿到新的 CAS 基準與目前內容，再以新世代重讀時間軸並選取最上面一列。</summary>
    private async Task ReloadFavoriteAsync()
    {
        if (_closed) return;
        _changed = true;
        var hostGeneration = SqlMemoryHost.Runtime.Generation;
        var favorite = await SqlMemoryHost.Runtime.ReadFavoriteAsync(_favorite.Favorite.FavoriteId, _lifetime.Token);
        if (!IsCurrentHost(hostGeneration)) return;
        if (favorite is null)
        {
            _rows.Clear(); Select(null);
            _timeline.BeginFirstPage(out var generation);
            _timeline.Accept(generation, new SqlMemoryPage<SqlFavoriteRevisionItem>(Array.Empty<SqlFavoriteRevisionItem>(), null));
            UpdateFooter();
            Report("收藏已被移除；關閉後清單會同步更新。");
            return;
        }
        _favorite = favorite;
        _timeline.UseCurrentContent(favorite.ContentId);
        await LoadFirstPageAsync(selectFirst: true);
    }

    private void Select(SqlFavoriteRevisionRow? row)
    {
        if (_closed) return;
        _delay.Stop(); _read.Cancel(); _read.Dispose(); _read = new CancellationTokenSource();
        _selected = row; _selectedSql = null;
        _diff.Clear(); _viewer.SetSql("");
        _comparison.Text = ""; ShowNotice("");
        UpdateActions();
        _placeholder.Text = row is null ? "請在時間軸選取版本。" : "";
        _placeholder.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        _contentLoading.IsLoading = row is not null;
        if (row is not null) _delay.Start();
    }

    private async Task ReadSelectionAsync()
    {
        var row = _selected; var token = _read.Token;
        var hostGeneration = SqlMemoryHost.Runtime.Generation;
        if (row is null) return;
        bool Stale() => _closed || token.IsCancellationRequested || !ReferenceEquals(row, _selected) || !IsCurrentHost(hostGeneration);
        try
        {
            var content = await ReadContentAsync(row.Item.ContentId, token);
            if (Stale()) return;
            if (content is null)
            {
                row.ContentMissing = true; UpdateRows();
                ShowPlaceholder("此版本的內容已被維護清理，無法預覽、比對或回溯。");
                return;
            }
            _selectedSql = content.SqlText;
            UpdateActions();

            var comparison = _timeline.ComparisonFor(row.Item);
            string baseSql = content.SqlText, targetSql = content.SqlText;
            if (comparison is not null)
            {
                var otherId = comparison.BaseContentId == row.Item.ContentId ? comparison.TargetContentId : comparison.BaseContentId;
                var other = otherId == row.Item.ContentId ? content : await ReadContentAsync(otherId, token);
                if (Stale()) return;
                if (other is null)
                {
                    ShowNotice("比較對象的內容已被清理；只能檢視此版本全文。");
                    _mode.SelectedIndex = 1; ShowMode();
                    return;
                }
                baseSql = comparison.BaseContentId == row.Item.ContentId ? content.SqlText : other.SqlText;
                targetSql = comparison.TargetContentId == row.Item.ContentId ? content.SqlText : other.SqlText;
            }

            // 比對可能要數十毫秒；放到背景，上限與降級在 Core 決定，UI 執行緒只接結果。
            var result = await Task.Run(() => SqlTextDiff.Compute(baseSql, targetSql, token), token);
            if (Stale()) return;
            _comparison.Text = comparison is null
                ? "最早保留的版本；沒有可比較的前一版"
                : comparison.Description + " · +" + result.Added.ToString(CultureInfo.InvariantCulture) +
                  " -" + result.Removed.ToString(CultureInfo.InvariantCulture);
            ShowNotice(Describe(result, comparison is null));
            _diff.Show(result);
            ShowMode();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (!Stale()) Report(SqlMemoryTimeText.Failure("讀取版本", error));
        }
        finally
        {
            if (!_closed && !token.IsCancellationRequested && ReferenceEquals(row, _selected)) _contentLoading.IsLoading = false;
        }
    }

    private static string Describe(SqlTextDiffResult result, bool earliest) =>
        earliest ? "" :
        result.Fallback == SqlTextDiffFallback.TooLarge ? "SQL 太大，未逐行比對；以整段取代呈現差異。" :
        result.Fallback == SqlTextDiffFallback.TooManyChanges ? "差異過多，未逐行比對；以整段取代呈現差異。" :
        result.TextsEqual ? "內容與比較對象完全相同。" :
        result.OnlyLineEndingsDiffer ? "只有換行字元或結尾換行不同；逐行內容相同。" : "";

    /// <summary>差異與全文共用同一塊內容表面；切換時只換顯示，不重讀儲存。</summary>
    private void ShowMode()
    {
        var full = _mode.SelectedIndex == 1;
        var ready = _selectedSql is not null && _placeholder.Visibility != Visibility.Visible;
        _diff.Visibility = !full && ready ? Visibility.Visible : Visibility.Collapsed;
        _viewer.Visibility = full && ready ? Visibility.Visible : Visibility.Collapsed;
        if (full && ready && !ReferenceEquals(_viewer.Sql, _selectedSql))
        {
            _viewer.SetSql(_selectedSql!);
            SqlAssistChrome.PlayAppear(_viewer);
        }
    }

    private void ShowPlaceholder(string message)
    {
        _placeholder.Text = message; _placeholder.Visibility = Visibility.Visible;
        _diff.Visibility = _viewer.Visibility = Visibility.Collapsed;
        UpdateActions();
    }

    private void ShowNotice(string message)
    {
        _notice.Text = message;
        _notice.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Run(SqlFavoriteRevisionAction action)
    {
        if (_closed || _list.SelectedItem is not SqlFavoriteRevisionRow row) return;
        if (action == SqlFavoriteRevisionAction.Preview)
        {
            _mode.SelectedIndex = 1; ShowMode();
            return;
        }
        var token = _lifetime.Token;
        var loaded = ReferenceEquals(row, _selected) ? _selectedSql : null;
        _actions.IsEnabled = false;
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            try { await _commands.RunAsync(action, row, _favorite, _retainedLimit, this, Report, token, loaded); }
            finally { if (!_closed) { _actions.IsEnabled = true; UpdateActions(); } }
        }, Report);
    }

    private void UpdateRows()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.IsFirst = i == 0;
            row.IsLast = i == _rows.Count - 1 && !_timeline.HasMore;
            row.CanRevert = !row.ContentMissing && _timeline.CanRevert(row.Item);
        }
        UpdateActions();
        UpdateFooter();
    }

    private void UpdateActions()
    {
        var row = _selected;
        foreach (var (button, command) in _actionButtons)
        {
            button.Visibility = row is null || (command.HiddenOnCurrent && row.IsCurrent) ? Visibility.Collapsed : Visibility.Visible;
            button.IsEnabled = _selectedSql is not null && !_commands.IsBusy && SqlFavoriteRevisionCommands.CanRun(command.Action, row);
            var label = command.Action == SqlFavoriteRevisionAction.Revert && row is not null ? row.RevertLabel : command.Label;
            button.ToolTip = label; AutomationProperties.SetName(button, label);
        }
        _mode.IsEnabled = row is not null && !row.ContentMissing;
    }

    private void UpdateFooter()
    {
        _pager.Update(_timeline.Footer());
        _list.CanAutoLoadMore = _timeline.HasMore && !_timeline.IsLoading;
    }

    private void Settle(bool start)
    {
        if (!start) return;
        _settle.Stop(); _settle.Start();
    }

    /// <summary>目前版本會被反覆拿來比較；只快取讀到的內容，已清理（null）不快取，下一次仍會重讀確認。</summary>
    private async Task<SqlContent?> ReadContentAsync(string contentId, CancellationToken token)
    {
        if (_contents.TryGetValue(contentId, out var cached)) return cached;
        var content = await SqlMemoryHost.Runtime.ReadContentAsync(contentId, token);
        if (content is null) return null;
        if (_contents.Count >= ContentCacheLimit) _contents.Clear();
        _contents[contentId] = content;
        return content;
    }

    private bool IsCurrentHost(long generation) =>
        !_closed && SqlMemoryHost.Runtime.IsAvailable && generation == SqlMemoryHost.Runtime.Generation;

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        // 回溯提交中關窗會失去結果回報；等它回來再關。
        if (!_commands.IsBusy) return;
        args.Cancel = true;
        Report("正在回溯，請等候完成再關閉。");
    }

    private void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _delay.Stop(); _settle.Stop();
        _read.Cancel(); _read.Dispose();
        // 只取消不處置：已派送的隔離呼叫可能還握著這個 token 的註冊。
        _lifetime.Cancel();
        _contentLoading.IsLoading = _timelineLoading.IsLoading = false;
        _viewer.Dispose();
    }

    private void Report(string message)
    {
        if (_closed) return;
        _status.Text = message; _status.ToolTip = message;
    }
}
