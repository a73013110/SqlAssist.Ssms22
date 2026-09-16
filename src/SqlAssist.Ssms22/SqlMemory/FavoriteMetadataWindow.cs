using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

internal sealed class FavoriteMetadataWindow : DialogWindow
{
    private readonly SqlAssistPackage _package;
    private readonly SqlMemoryRow _row;
    private readonly SqlFavoriteItem? _existing;
    private readonly TextBox _name = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _description = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _server = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _database = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly ComboBox _scope = SqlAssistChrome.CreateMemoryScopeCombo("全域", "伺服器", "資料庫");
    private readonly TextBlock _validation = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly Button _submit;
    private readonly Button _cancel;
    private readonly StackPanel _fields = new();
    private bool _submitting;
    private bool _conflict;

    public FavoriteMetadataWindow(SqlAssistPackage package, SqlMemoryRow row, bool existing)
    {
        _package = package; _row = row; _existing = existing ? row.Favorite : null;
        SqlMemoryActions.ConfigureWindow(this, package, existing ? "編輯收藏資料" : "新增至收藏", 580, 650);
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _cancel = SqlAssistChrome.CreateButton("取消", SqlAssistChrome.DefaultMetrics); _cancel.IsCancel = true;
        _submit = SqlAssistChrome.CreateButton(existing ? "更新收藏" : "新增至收藏", SqlAssistChrome.DefaultMetrics, true); _submit.IsDefault = true;
        actions.Children.Add(_cancel); actions.Children.Add(_submit); footer.Children.Add(actions);
        var fields = _fields;
        _name.MaxLength = 200;
        _description.MaxLength = 2000; _description.AcceptsReturn = true; _description.Height = 96;
        _description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        foreach (var pair in new[] { ("名稱（1–200 字元）", (Control)_name), ("說明（最多 2000 字元）", (Control)_description),
            ("收藏範圍", (Control)_scope), ("伺服器（精確名稱）", (Control)_server), ("資料庫（精確名稱）", (Control)_database) })
        {
            var field = SqlAssistChrome.CreateMemoryField(pair.Item1, pair.Item2);
            field.Margin = new Thickness(0, 0, 0, 8); fields.Children.Add(field);
        }
        fields.Children.Add(_validation);
        root.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = root;
        _name.Text = row.Name.Length > 200 ? row.Name.Substring(0, 200) : row.Name;
        _description.Text = _existing?.Favorite.Description ?? "";
        _scope.SelectedIndex = (int)(_existing?.Favorite.Scope ?? SqlFavoriteScope.Global);
        _server.Text = (_existing?.Favorite.Connection ?? row.History?.Connection)?.Server ?? "";
        _database.Text = (_existing?.Favorite.Connection ?? row.History?.Connection)?.Database ?? "";
        foreach (var field in new[] { _name, _description, _server, _database }) field.TextChanged += (_, _) => Validate();
        _scope.SelectionChanged += (_, _) => Validate();
        _submit.Click += (_, _) => _ = SqlMemoryActions.RunAsync(SubmitAsync, Report);
        Closing += (_, e) => e.Cancel = _submitting;
        Validate();
    }

    private void Validate()
    {
        _server.IsEnabled = _scope.SelectedIndex > 0;
        _database.IsEnabled = _scope.SelectedIndex == 2;
        var message = string.IsNullOrWhiteSpace(_name.Text) ? "名稱不可空白。"
            : _server.IsEnabled && string.IsNullOrWhiteSpace(_server.Text) ? "請輸入伺服器精確名稱。"
            : _database.IsEnabled && string.IsNullOrWhiteSpace(_database.Text) ? "請輸入資料庫精確名稱。" : "";
        _validation.Text = message;
        _submit.IsEnabled = message.Length == 0 && !_submitting && !_conflict;
    }
    private async Task SubmitAsync()
    {
        if (_submitting || !_submit.IsEnabled || _row.RevisionId is not { } revision) return;
        var scope = (SqlFavoriteScope)_scope.SelectedIndex;
        var connection = scope == SqlFavoriteScope.Global ? null
            : new SqlConnectionLabel(_server.Text, scope == SqlFavoriteScope.Database ? _database.Text : "");
        var query = new SqlFavorite(_existing?.Favorite.FavoriteId ?? Guid.NewGuid(), _name.Text, _description.Text,
            revision, scope, connection);
        var write = new SqlFavoriteWrite(query, _existing?.Version);
        _submitting = true; _submit.IsEnabled = _cancel.IsEnabled = false;
        _fields.IsEnabled = false;
        Report("正在儲存收藏…");
        try
        {
            var result = await SqlMemoryHost.Runtime.WriteFavoriteAsync(write, _package.DisposalToken);
            if (result == SqlFavoriteWriteResult.Conflict)
            {
                _conflict = true; Report("收藏已被修改或刪除；請保留輸入並重新整理，不會覆寫他人的修改。"); return;
            }
            _submitting = false; DialogResult = true;
        }
        catch (Exception error)
        {
            // 與 SQL 編輯相同，回應不明時先讓使用者保留輸入，不直接重送新增。
            _conflict = true;
            Report("收藏儲存未確認：" + error.Message + " 請保留輸入並重新整理後確認結果。");
        }
        finally { _submitting = false; _cancel.IsEnabled = _fields.IsEnabled = true; Validate(); }
    }
    private void Report(string message) { _status.Text = message; _status.ToolTip = message; }
}
