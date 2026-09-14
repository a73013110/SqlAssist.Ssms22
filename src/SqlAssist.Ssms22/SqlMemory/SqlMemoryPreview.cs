using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

internal sealed class SqlMemoryPreview : UserControl, IDisposable
{
    private readonly SqlAssistPackage _package;
    private readonly Action _changed;
    private readonly SqlReadOnlyViewer _viewer = new();
    private readonly TextBlock _detail = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly WrapPanel _actions = new();
    private readonly WrapPanel _tools = new();
    private readonly Button _addFavorite;
    private readonly Button _edit;
    private readonly Button _metadata;
    private readonly Button _removeFavorite;
    private readonly DispatcherTimer _delay;
    private CancellationTokenSource _read = new();
    private SqlMemoryRow? _row;
    private bool _disposed;
    private bool _loaded;
    public TextBlock Summary => _detail;

    public SqlMemoryPreview(SqlAssistPackage package, Action changed)
    {
        _package = package; _changed = changed;
        _tools.Children.Add(Button("Copy", "複製全文", () => _viewer.CopyAll()));
        var wrap = Button("Wrap", "切換 SQL 顯示換行", () => _viewer.SetWrap(!_viewer.Wrap));
        _tools.Children.Add(wrap);
        _addFavorite = Button("Favorite", "Add to Favorites", () => EditMetadata(false));
        _edit = Button("Edit", "編輯 SQL", EditSql);
        _metadata = Button("Settings", "編輯收藏資料", () => EditMetadata(true));
        _removeFavorite = Button("Remove", "Remove from Favorites", RemoveFavorite);
        foreach (var button in new[] { _addFavorite, _edit, _metadata, _removeFavorite }) _actions.Children.Add(button);
        var open = Button("Open", "開啟至新 Query（不執行 SQL）", () => SqlMemoryActions.OpenQuery(_package, _viewer.Sql));
        open.Template = SqlAssistChrome.CreatePrimaryButtonTemplate();
        _actions.Children.Add(open);
        Content = SqlAssistChrome.CreateQueryDetailBody(_viewer, _status, _tools, _actions);
        _viewer.ReportError = Report;
        _delay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
        _delay.Tick += (_, _) => { _delay.Stop(); _ = SqlMemoryActions.RunAsync(ReadAsync, Report); };
        Select(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _delay.Stop(); _read.Cancel(); _read.Dispose(); _viewer.Dispose();
    }

    public void Select(SqlMemoryRow? row, bool previewEnabled = true)
    {
        if (_disposed) return;
        _delay.Stop(); _read.Cancel(); _read.Dispose(); _read = new CancellationTokenSource();
        _row = row; _loaded = false;
        _actions.IsEnabled = _tools.IsEnabled = _viewer.IsEnabled = false;
        _detail.Text = row is null ? "請在清單選取 SQL。" : row.Name + " · " + row.Detail;
        _detail.ToolTip = _detail.Text;
        _viewer.SetSql("");
        _addFavorite.Visibility = row?.Favorite is null ? Visibility.Visible : Visibility.Collapsed;
        _edit.Visibility = _metadata.Visibility = _removeFavorite.Visibility = row?.Favorite is not null ? Visibility.Visible : Visibility.Collapsed;
        _addFavorite.IsEnabled = row?.RevisionId is not null;
        _addFavorite.ToolTip = row?.RevisionId is null ? "未存檔 Recovery 尚無版本，請先開啟為新查詢；本版不能直接收藏。" : "Add to Favorites";
        Report(row is null || !previewEnabled ? "" : "正在取得 SQL…");
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
    }

    private void EditSql()
    {
        if (_row?.Favorite is not { } favorite) return;
        var dialog = new FavoriteSqlEditWindow(_package, favorite, _viewer.Sql);
        if (dialog.ShowModal() == true) { _changed(); Report("SQL 已儲存。"); }
    }
    private void EditMetadata(bool existing)
    {
        if (_row is not { RevisionId: not null } row) return;
        var dialog = new FavoriteMetadataWindow(_package, row, existing);
        if (dialog.ShowModal() == true) { _changed(); Report("收藏資料已儲存。"); }
    }
    private void RemoveFavorite()
    {
        if (_row?.Favorite is not { } favorite) return;
        var selected = _row;
        var generation = SqlMemoryHost.Runtime.Generation;
        var token = _read.Token;
        if (!SqlAssistConfirmationWindow.Confirm(Window.GetWindow(this), "Remove from Favorites", $"刪除「{favorite.Favorite.Name}」？",
                "只刪除此收藏，不連帶刪除歷史。", "Remove from Favorites")) return;
        _actions.IsEnabled = false;
        _ = SqlMemoryActions.RunAsync(async () =>
        {
            try
            {
                var result = await SqlMemoryHost.Runtime.DeleteFavoriteAsync(favorite.Favorite.FavoriteId, favorite.Version, token);
                if (_disposed || token.IsCancellationRequested || !ReferenceEquals(selected, _row) ||
                    !SqlMemoryHost.Runtime.IsAvailable || generation != SqlMemoryHost.Runtime.Generation) return;
                if (result == SqlFavoriteWriteResult.Conflict) { Report("收藏已被修改或刪除；請重新整理後再操作。"); return; }
                Select(null); _changed(); Report("收藏已刪除；歷史未刪除。");
            }
            catch (Exception error)
            {
                // 切頁或停用後，已派送的移除可以完成，但不能蓋掉另一筆預覽。
                if (!_disposed && !token.IsCancellationRequested && ReferenceEquals(selected, _row) &&
                    SqlMemoryHost.Runtime.IsAvailable && generation == SqlMemoryHost.Runtime.Generation) Report("移除收藏未確認：" + error.Message);
            }
            finally { if (!_disposed && ReferenceEquals(selected, _row)) _actions.IsEnabled = _loaded; }
        }, Report);
    }
    private Button Button(string icon, string text, Action action)
    {
        var button = SqlAssistChrome.CreateQueryIconButton(icon, text);
        button.Click += (_, _) => SqlMemoryActions.Run(() =>
        {
            if (_loaded && SqlMemoryHost.Runtime.IsAvailable) action();
        }, Report);
        return button;
    }
    private void Report(string message) { if (!_disposed) { _status.Text = message; _status.ToolTip = message; } }
}
