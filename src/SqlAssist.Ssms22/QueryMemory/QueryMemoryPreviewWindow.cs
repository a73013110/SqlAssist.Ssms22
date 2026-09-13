using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.QueryMemory;

internal sealed class QueryMemoryPreviewWindow : DialogWindow
{
    private readonly SqlAssistPackage _package;
    private readonly Action _changed;
    private readonly SqlReadOnlyViewer _viewer = new();
    private readonly TextBlock _detail = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly WrapPanel _actions = new();
    private readonly WrapPanel _tools = new();
    private readonly Button _save;
    private readonly Button _edit;
    private readonly Button _metadata;
    private readonly Button _delete;
    private readonly DispatcherTimer _delay;
    private CancellationTokenSource _read = new();
    private QueryMemoryRow? _row;
    private bool _closed;
    private bool _loaded;

    public QueryMemoryPreviewWindow(SqlAssistPackage package, Action changed)
    {
        _package = package; _changed = changed;
        QueryMemoryActions.ConfigureWindow(this, package, "SQL 預覽", 900, 620);
        QueryMemoryPreviewPlacement.Restore(this, package);
        var root = new DockPanel { Margin = new Thickness(16) };
        _detail.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_detail, Dock.Top); root.Children.Add(_detail);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _tools.Children.Add(Button("複製選取", () => _viewer.CopySelection()));
        _tools.Children.Add(Button("複製全文", () => _viewer.CopyAll()));
        var wrap = SqlAssistChrome.CreateButton("顯示換行：關", SqlAssistChrome.DefaultMetrics);
        wrap.Click += (_, _) => QueryMemoryActions.Run(() =>
        {
            _viewer.SetWrap(!_viewer.Wrap); wrap.Content = _viewer.Wrap ? "顯示換行：開" : "顯示換行：關";
        }, Report);
        _tools.Children.Add(wrap); footer.Children.Add(_tools);
        _actions.HorizontalAlignment = HorizontalAlignment.Right;
        _save = Button("加入收藏", () => EditMetadata(false));
        _edit = Button("編輯 SQL", EditSql);
        _metadata = Button("編輯資料", () => EditMetadata(true));
        _delete = Button("刪除收藏", Delete);
        foreach (var button in new[] { _save, _edit, _metadata, _delete }) _actions.Children.Add(button);
        var open = Button("開啟至新查詢", () => QueryMemoryActions.OpenQuery(_package, _viewer.Sql), true);
        open.ToolTip = "沿用目前 SSMS 連線，不切換至歷史連線，也不執行 SQL。";
        _actions.Children.Add(open); footer.Children.Add(_actions);
        _status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_status);
        root.Children.Add(_viewer); Content = root;
        _viewer.ReportError = Report;
        _delay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
        _delay.Tick += (_, _) => { _delay.Stop(); _ = QueryMemoryActions.RunAsync(ReadAsync, Report); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) => SqlAssistPlatformGuard.Run("關閉 SQL Memory 預覽", () =>
        {
            _closed = true; _delay.Stop(); _read.Cancel(); _read.Dispose(); _viewer.Dispose();
            QueryMemoryPreviewPlacement.Save(this, package);
        });
    }

    public void Select(QueryMemoryRow? row)
    {
        if (_closed) return;
        _delay.Stop(); _read.Cancel(); _read.Dispose(); _read = new CancellationTokenSource();
        _row = row; _loaded = false;
        _actions.IsEnabled = _tools.IsEnabled = _viewer.IsEnabled = false;
        Title = row is null ? "SQL 預覽" : row.Name;
        _detail.Text = row?.Detail ?? "請在 SQL Memory 清單選取項目。";
        _detail.ToolTip = _detail.Text;
        _viewer.SetSql("");
        _save.Visibility = row?.Saved is null ? Visibility.Visible : Visibility.Collapsed;
        _edit.Visibility = _metadata.Visibility = _delete.Visibility = row?.Saved is not null ? Visibility.Visible : Visibility.Collapsed;
        _save.IsEnabled = row?.RevisionId is not null;
        _save.ToolTip = row?.RevisionId is null ? "未存檔 Recovery 尚無版本，請先開啟為新查詢；本版不能直接收藏。" : "保存此版本為收藏";
        Report(row is null ? "" : "正在取得 SQL…");
        if (row is not null) _delay.Start();
    }

    private async Task ReadAsync()
    {
        var row = _row; var token = _read.Token;
        var generation = QueryMemoryHost.Generation;
        if (row is null) return;
        try
        {
            var content = await QueryMemoryHost.ReadContentAsync(row.ContentId, token);
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(row, _row) ||
                !QueryMemoryHost.IsAvailable || generation != QueryMemoryHost.Generation) return;
            if (content is null) { Report("內容已被清理或不存在；請重新整理清單。"); return; }
            _viewer.SetSql(content.SqlText);
            _loaded = true; _actions.IsEnabled = _tools.IsEnabled = _viewer.IsEnabled = true;
            Report(content.Length == 0 ? "這份 SQL 是空白內容。" : "");
        }
        catch (Exception error)
        {
            // 舊讀取的錯誤與舊成功回應一樣，都不能污染目前選取。
            if (!_closed && !token.IsCancellationRequested && ReferenceEquals(row, _row)) Report("SQL 載入失敗：" + error.Message);
        }
    }

    private void EditSql()
    {
        if (_row?.Saved is not { } saved) return;
        var dialog = new QueryMemorySqlEditWindow(_package, saved, _viewer.Sql) { Owner = this };
        if (dialog.ShowModal() == true) { _changed(); Report("SQL 已儲存。"); }
    }
    private void EditMetadata(bool existing)
    {
        if (_row is not { RevisionId: not null } row) return;
        var dialog = new QueryMemoryMetadataWindow(_package, row, existing) { Owner = this };
        if (dialog.ShowModal() == true) { _changed(); Report("收藏資料已儲存。"); }
    }
    private void Delete()
    {
        if (_row?.Saved is not { } saved) return;
        if (!SqlAssistConfirmationWindow.Confirm(this, "刪除收藏", $"刪除「{saved.Query.Name}」？",
                "只刪除此收藏，不連帶刪除歷史。", "刪除收藏")) return;
        _actions.IsEnabled = false;
        _ = QueryMemoryActions.RunAsync(async () =>
        {
            try
            {
                var result = await QueryMemoryHost.DeleteSavedQueryAsync(saved.Query.SavedQueryId, saved.Version, _package.DisposalToken);
                if (_closed) return;
                if (result == SavedQueryWriteResult.Conflict) { Report("收藏已被修改或刪除；請重新整理後再操作。"); return; }
                Select(null); _changed(); Report("收藏已刪除；歷史未刪除。");
            }
            finally { if (!_closed) _actions.IsEnabled = _loaded; }
        }, Report);
    }
    private Button Button(string text, Action action, bool primary = false)
    {
        var button = SqlAssistChrome.CreateButton(text, SqlAssistChrome.DefaultMetrics, primary);
        button.Click += (_, _) => QueryMemoryActions.Run(() =>
        {
            if (_loaded && QueryMemoryHost.IsAvailable) action();
        }, Report);
        return button;
    }
    private void Report(string message) { if (!_closed) { _status.Text = message; _status.ToolTip = message; } }
}
