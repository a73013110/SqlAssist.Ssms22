using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

internal sealed class SqlMemoryPreview : UserControl, IDisposable
{
    private readonly SqlMemoryItemCommands _commands;
    private readonly Action<string> _reportCommand;
    private readonly SqlReadOnlyViewer _viewer = new();
    private readonly ContentControl _detail = new() { ContentTemplate = SqlAssistChrome.CreateMemoryMetadataTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly SqlLoadingSurface _loading;
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly WrapPanel _actions = new();
    private readonly WrapPanel _tools = new();
    private readonly List<(Button Button, SqlMemoryRowCommand Command)> _rowActions = new();
    private readonly DispatcherTimer _delay;
    private CancellationTokenSource _read = new();
    private SqlMemoryRow? _row;
    private bool _disposed;
    private bool _loaded;
    public FrameworkElement Summary => _detail;

    /// <param name="reportCommand">列操作的結果寫到工具窗的狀態列：刪除後選取會移到下一筆，寫在 Preview 會立刻被清掉。</param>
    public SqlMemoryPreview(SqlMemoryItemCommands commands, Action<string> reportCommand)
    {
        _commands = commands;
        _reportCommand = reportCommand;
        // 左側只放全文專用工具；右側列操作與清單卡片、快捷選單同一份清單、同一個順序與實作。
        _tools.Children.Add(Button(SqlIcon.Copy, "複製全文", () => _viewer.CopyAll()));
        var wrap = Button(SqlIcon.Wrap, "切換 SQL 顯示換行", () => _viewer.SetWrap(!_viewer.Wrap));
        _tools.Children.Add(wrap);
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
        _loading = new SqlLoadingSurface(_viewer);
        Content = SqlAssistChrome.CreateMemoryDetailBody(_loading, _status, _tools, _actions);
        _viewer.ReportError = Report;
        _delay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
        _delay.Tick += (_, _) => { _delay.Stop(); _ = SqlMemoryActions.RunAsync(ReadAsync, Report); };
        Select(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _loading.IsLoading = false; _delay.Stop(); _read.Cancel(); _read.Dispose(); _viewer.Dispose();
    }

    public void Select(SqlMemoryRow? row, bool previewEnabled = true)
    {
        if (_disposed) return;
        _delay.Stop(); _read.Cancel(); _read.Dispose(); _read = new CancellationTokenSource();
        _row = row; _loaded = false;
        _actions.IsEnabled = _tools.IsEnabled = _viewer.IsEnabled = false;
        _detail.Content = row;
        _detail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        _detail.ToolTip = row is null ? "請在清單選取 SQL。" : row.Name + " · " + row.Detail;
        _viewer.SetSql("");
        foreach (var (button, command) in _rowActions)
        {
            button.Visibility = row is not null && command.AppliesTo(row.IsFavorite) ? Visibility.Visible : Visibility.Collapsed;
            var label = command.Action == SqlMemoryRowAction.Delete ? row?.DeleteLabel ?? command.Label : command.Label;
            button.ToolTip = label; AutomationProperties.SetName(button, label);
        }
        Report(row is null ? "請在清單選取 SQL。" : "");
        _loading.IsLoading = row is not null && previewEnabled;
        if (row is not null && previewEnabled) _delay.Start();
    }

    private async Task ReadAsync()
    {
        var row = _row; var token = _read.Token;
        var generation = SqlMemoryHost.Runtime.Generation;
        if (row is null) return;
        try
        {
            var content = await SqlMemoryHost.Runtime.ReadContentAsync(row.ContentId, token);
            if (_disposed || token.IsCancellationRequested || !ReferenceEquals(row, _row) ||
                !SqlMemoryHost.Runtime.IsAvailable || generation != SqlMemoryHost.Runtime.Generation) return;
            if (content is null) { Report("內容已被清理或不存在；請重新整理清單。"); return; }
            _viewer.SetSql(content.SqlText);
            _loaded = true; _actions.IsEnabled = _tools.IsEnabled = _viewer.IsEnabled = true;
            Report(content.Length == 0 ? "這份 SQL 是空白內容。" : "");
        }
        catch (Exception error)
        {
            // 舊讀取的錯誤與舊成功回應一樣，都不能污染目前選取。
            if (!_disposed && !token.IsCancellationRequested && ReferenceEquals(row, _row) &&
                SqlMemoryHost.Runtime.IsAvailable && generation == SqlMemoryHost.Runtime.Generation) Report(SqlMemoryTimeText.Failure("SQL 載入", error));
        }
        finally
        {
            // 舊請求的結束不能關掉新選取的載入效果。
            if (!_disposed && !token.IsCancellationRequested && ReferenceEquals(row, _row)) _loading.IsLoading = false;
        }
    }

    /// <summary>Preview 已讀好全文，編輯與開啟直接沿用，不再讀一次；取消跟著目前選取的讀取生命週期。</summary>
    private void Run(SqlMemoryRowAction action)
    {
        if (_row is not { } row) return;
        var token = _read.Token;
        _actions.IsEnabled = false;
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            try { await _commands.RunAsync(action, row, this, _reportCommand, token, _viewer.Sql); }
            finally { if (!_disposed && ReferenceEquals(row, _row)) _actions.IsEnabled = _loaded; }
        }, _reportCommand);
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
