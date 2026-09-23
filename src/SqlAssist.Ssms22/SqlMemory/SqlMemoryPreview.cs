using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlAssist.Core.Matching;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

internal sealed class SqlMemoryPreview : UserControl, IDisposable
{
    private readonly SqlMemoryItemCommands _commands;
    private readonly Action<string> _reportCommand;
    private readonly SqlReadOnlyViewer _viewer = new();
    private readonly ContentControl _detail = new() { ContentTemplate = SqlAssistChrome.CreateMemoryMetadataTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly SqlStateSurface _surface;
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly WrapPanel _actions = new();
    private readonly WrapPanel _tools = new();
    private readonly SqlMatchNavigation _matches;
    private readonly List<(Button Button, SqlMemoryRowCommand Command)> _rowActions = new();
    private readonly SqlSelectionLoader<SqlMemoryRow> _loader;
    private bool _disposed;
    private bool _loaded;
    private TextMatcher? _matcher;
    public FrameworkElement Summary => _detail;

    /// <param name="reportCommand">列操作的結果寫到工具窗的狀態列：刪除後選取會移到下一筆，寫在 Preview 會立刻被清掉。</param>
    public SqlMemoryPreview(SqlMemoryItemCommands commands, Action<string> reportCommand)
    {
        _commands = commands;
        _reportCommand = reportCommand;
        // 左側只放全文專用工具；右側列操作與清單卡片、快捷選單同一份清單、同一個順序與實作。
        // 命中導覽與界線排在最前面，與 SQL Search 預覽同一份（SqlMatchNavigation）。
        _matches = new SqlMatchNavigation(_viewer);
        foreach (var item in _matches.ToolbarItems) _tools.Children.Add(item);
        _tools.Children.Add(Button(SqlIcon.Copy, "複製全文", () => _viewer.CopyAll()));
        // 換行是一個維持著的狀態，不是一次動作，所以與 SQL Search 預覽同一顆開關：
        // 按完之後工具列上看得出現在是開著的，理由見 SqlAssistChrome.CreateIconToggle。
        _tools.Children.Add(Toggle(SqlIcon.Wrap, "SQL 顯示換行", _viewer.SetWrap));
        foreach (var command in SqlMemoryRowCommand.All)
        {
            // 複製已由左側的「複製全文」涵蓋，右側不再放第二顆同義按鈕。
            if (command.Action is SqlMemoryRowAction.Copy) continue;
            var action = command.Action;
            var button = Button(command.Icon, command.Label, () => Run(action), command.Tone);
            // 開啟是這個面板的主要動作；只換靜止底色，位置仍跟著共用順序排在最前。
            if (action == SqlMemoryRowAction.Open) button.Template = SqlAssistChrome.CreatePrimaryButtonTemplate();
            if (command.IsSeparated) button.Margin = new Thickness(6, 0, 0, 0);
            _rowActions.Add((button, command)); _actions.Children.Add(button);
        }
        _surface = new SqlStateSurface(_viewer);
        Content = SqlAssistChrome.CreateMemoryDetailBody(_surface, _status, _tools, _actions);
        _viewer.ReportError = Report;
        _loader = new SqlSelectionLoader<SqlMemoryRow>(Dispatcher, SqlAssistChrome.Debounce.Preview,
            (row, token) => _ = SqlMemoryActions.RunAsync(() => ReadAsync(row, token), Report));
        Select(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _surface.State = SqlSurfaceState.None; _loader.Dispose(); _viewer.Dispose();
    }

    /// <param name="matcher">
    /// 清單這一輪的比對器（<see cref="SqlMemoryQuery.Matcher"/>）；沒有搜尋字時 null，就不標也不顯示導覽。
    /// 換搜尋字或比對選項會作廢清單並重選一次，所以這裡不必自己觀察搜尋框。
    /// </param>
    public void Select(SqlMemoryRow? row, bool previewEnabled = true, TextMatcher? matcher = null)
    {
        if (_disposed) return;
        _loader.Select(row); _loaded = false;
        _matcher = row is null ? null : matcher;
        _actions.IsEnabled = _tools.IsEnabled = _viewer.IsEnabled = false;
        _detail.Content = row;
        _detail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        _detail.ToolTip = row is null ? "請在清單選取 SQL。" : row.Name + " · " + row.Detail;
        _matches.Clear();
        foreach (var (button, command) in _rowActions)
        {
            button.Visibility = row is not null && command.AppliesTo(row.IsFavorite) ? Visibility.Visible : Visibility.Collapsed;
            var label = command.Action == SqlMemoryRowAction.Delete ? row?.DeleteLabel ?? command.Label : command.Label;
            button.ToolTip = label; AutomationProperties.SetName(button, label);
        }
        Report("");
        // 「還沒選」與「讀不到」是同一塊表面上的兩種狀態；寫在狀態列的話，刪一列之後
        // 那一句會被列操作的結果蓋掉，而畫面上仍是一塊空白。
        _viewer.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        _surface.State = row is null
            ? SqlSurfaceState.Empty("尚未選取", "在清單選一筆 SQL 看它的全文。")
            : previewEnabled ? SqlSurfaceState.Loading : SqlSurfaceState.None;
        if (previewEnabled) _loader.Load();
    }

    private async Task ReadAsync(SqlMemoryRow row, CancellationToken token)
    {
        var generation = SqlMemoryHost.Runtime.Generation;
        try
        {
            var content = await SqlMemoryHost.Runtime.ReadContentAsync(row.ContentId, token);
            if (!_loader.IsCurrent(row, token) ||
                !SqlMemoryHost.Runtime.IsAvailable || generation != SqlMemoryHost.Runtime.Generation) return;
            if (content is null)
            {
                _viewer.Visibility = Visibility.Collapsed;
                _surface.State = SqlSurfaceState.Unreadable("內容已被清理或不存在；請重新整理清單。");
                return;
            }
            Show(content.SqlText, row.IsFavorite);
            _loaded = true; _actions.IsEnabled = _tools.IsEnabled = _viewer.IsEnabled = true;
            // 空白內容也是一種「沒有東西可讀」，跟其他三種走同一塊表面；寫在狀態列的話，
            // 使用者看到的是一塊空的唯讀檢視配一行小字。
            // 新的擷取不再留下空白列（SqlContent.IsBlank），但清理之前的舊資料仍在，
            // 所以這塊表面留著——而且比對的是同一份判斷，不是只看長度為零。
            _surface.State = SqlContent.IsBlank(content.SqlText)
                ? SqlSurfaceState.Empty("這份 SQL 是空白內容", "仍然可以開啟或刪除它。")
                : SqlSurfaceState.None;
        }
        catch (Exception error)
        {
            // 舊讀取的錯誤與舊成功回應一樣，都不能污染目前選取。
            if (_loader.IsCurrent(row, token) &&
                SqlMemoryHost.Runtime.IsAvailable && generation == SqlMemoryHost.Runtime.Generation)
            {
                _viewer.Visibility = Visibility.Collapsed;
                _surface.State = SqlSurfaceState.Unreadable(SqlMemoryTimeText.Failure("SQL 載入", error));
            }
        }
        finally
        {
            // 舊請求的結束不能關掉新選取的載入效果，也不能蓋掉這一輪剛寫上去的讀不到。
            if (_loader.IsCurrent(row, token) && _surface.IsLoading) _surface.State = SqlSurfaceState.None;
        }
    }

    /// <summary>收藏只靠名稱或說明命中時的那一句；少標了的那一句在 <see cref="MatchHighlightSet.Notice"/>，兩個預覽共用。</summary>
    /// <remarks>
    /// 收藏比對的是名稱、說明與 SQL 的聯集，這一筆的 SQL 上一處都沒有；不說的話，一份沒有任何標記的
    /// 全文看起來像是高亮壞了。History 只比對 SQL，不會用到這一句。
    /// </remarks>
    private const string OutsideSql = "SQL 內文沒有命中；這一筆是名稱或說明符合。";

    /// <summary>把讀回來的全文放上檢視，用清單那一輪的比對器標出每一處。</summary>
    private void Show(string sql, bool favorite)
    {
        var highlights = MatchHighlights.Locate(_matcher, sql);
        _matches.Show(sql, highlights);
        Report(highlights.Notice(favorite && _matcher is not null ? OutsideSql : null));
    }

    /// <summary>Preview 已讀好全文，編輯與開啟直接沿用，不再讀一次；取消跟著目前選取的讀取生命週期。</summary>
    private void Run(SqlMemoryRowAction action)
    {
        if (_loader.Current is not { } row) return;
        var token = _loader.Token;
        _actions.IsEnabled = false;
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            try { await _commands.RunAsync(action, row, this, _reportCommand, token, _viewer.Sql); }
            finally { if (_loader.IsCurrent(row, token)) _actions.IsEnabled = _loaded; }
        }, _reportCommand);
    }

    /// <summary>維持著的狀態用開關。</summary>
    /// <remarks>
    /// 不走 <see cref="Button"/> 那道「沒載進來就不動作」的守門：那是給碰得到執行階段的動作用的，
    /// 而換行只改顯示。吃掉那一次的症狀是按鈕留在開著的樣子，內容卻沒有換行——一個開關說謊
    /// 比一顆按了沒事的按鈕更難發現。按不按得動由 <c>_tools.IsEnabled</c> 決定，與其餘工具相同。
    /// </remarks>
    private ToggleButton Toggle(SqlIcon icon, string text, Action<bool> apply)
    {
        var toggle = SqlAssistChrome.CreateIconToggle(icon, text);
        toggle.Checked += (_, _) => SqlMemoryActions.Run(() => apply(true), Report);
        toggle.Unchecked += (_, _) => SqlMemoryActions.Run(() => apply(false), Report);
        return toggle;
    }

    private Button Button(SqlIcon icon, string text, Action action, SqlActionTone tone = SqlActionTone.Neutral)
    {
        var button = SqlAssistChrome.CreateIconButton(icon, text, tone);
        button.Click += (_, _) => SqlMemoryActions.Run(() =>
        {
            if (_loaded && SqlMemoryHost.Runtime.IsAvailable) action();
        }, Report);
        return button;
    }

    private void Report(string message)
    {
        if (_disposed) return;
        _status.Text = message; _status.ToolTip = message;
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
