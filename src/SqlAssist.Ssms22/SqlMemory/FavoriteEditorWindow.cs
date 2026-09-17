using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>收藏編輯器：名稱、標註、說明與 SQL 在同一個對話框，一次儲存。</summary>
/// <remarks>
/// 新增與編輯共用。資料與 SQL 分成兩個對話框時，兩次儲存各自比對版本，第一次成功就讓第二次必然衝突，
/// 中間失敗還會只改到一半；這裡只呼叫一次 <see cref="ISqlFavoriteStore.SaveFavoriteAsync"/>。
/// SQL 沒改就引用原本的版本（新增時是來源紀錄的版本），改了才讓收藏自己建一份，不為了改名字多出版本。
/// 版本歷史仍是獨立對話框：瀏覽與回溯沒有「儲存」這一步，放進同一個頁尾會分不清按鈕作用在哪一頁。
/// </remarks>
internal sealed class FavoriteEditorWindow : DialogWindow
{
    private readonly SqlAssistPackage _package;
    private readonly SqlFavoriteItem? _existing;
    private readonly Guid? _sourceRevisionId;
    private readonly (string Name, string Description, string Server, string Database) _initial;
    private readonly TextBox _name = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _description = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly TextBox _server = SqlConnectionTagInput.CreateInput();
    private readonly TextBox _database = SqlConnectionTagInput.CreateInput();
    private readonly SqlTextEditor _editor;
    private readonly SqlScriptTheme _theme;
    private readonly TextBlock _sqlSummary = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly Button _submit;
    private readonly Button _cancel;
    private readonly Grid _form = new();
    private bool _submitting;
    private bool _committed;
    private bool _conflict;

    private FavoriteEditorWindow(SqlAssistPackage package, SqlFavoriteItem? existing, Guid? sourceRevisionId, string sql,
        string name, string? description, string? server, string? database, string summary)
    {
        _package = package; _existing = existing; _sourceRevisionId = sourceRevisionId;
        _editor = new SqlTextEditor(sql);
        // 與唯讀預覽同一份外觀來源：跟著查詢視窗的字型與分類色，對話框開著時查詢視窗不會換。
        _theme = new SqlScriptTheme(ActiveSqlEditor.Current, _editor);
        _theme.Updated += (_, _) => _editor.RefreshColors();
        SqlMemoryActions.ConfigureWindow(this, package, existing is null ? "新增至收藏" : "編輯收藏 — " + existing.Favorite.Name, 960, 700);
        MinWidth = 640; MinHeight = 520;

        var root = new DockPanel { Margin = SqlAssistChrome.DialogPadding };
        var context = SqlAssistChrome.CreateMetadataText(summary, SqlAssistChrome.DefaultMetrics);
        context.ToolTip = summary; context.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(context, Dock.Top); root.Children.Add(context);

        // 兩欄等寬、欄距 18、列距 12：名稱與說明佔滿，標註並排，「使用目前連線」貼在標註列尾端。
        _form.Margin = new Thickness(0, 0, 0, 16);
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(18), new GridLength(1, GridUnitType.Star),
            new GridLength(8), GridLength.Auto })
            _form.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        for (var row = 0; row < 5; row++)
            _form.RowDefinitions.Add(new RowDefinition { Height = row % 2 == 1 ? new GridLength(12) : GridLength.Auto });
        _name.MaxLength = 200;
        Place(SqlAssistChrome.CreateMemoryField("名稱", _name, _name), 0, 0, 5);
        Place(SqlAssistChrome.CreateMemoryField("伺服器", TagBar(SqlIcon.Server, _server, "伺服器", databases: false), _server), 2, 0);
        Place(SqlAssistChrome.CreateMemoryField("資料庫", TagBar(SqlIcon.Database, _database, "資料庫", databases: true), _database), 2, 2);
        var connection = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
        connection.Template = SqlAssistChrome.CreateGhostButtonTemplate();
        connection.Content = SqlAssistChrome.CreateMemoryLabel(SqlIcon.Connection, "使用目前連線");
        connection.ToolTip = "以目前作用中查詢視窗的伺服器與資料庫填入標註；不切換連線。";
        connection.MinHeight = 30; connection.VerticalAlignment = VerticalAlignment.Bottom;
        connection.Click += (_, _) => SqlMemoryActions.Run(UseCurrentConnection, Report);
        Place(connection, 2, 4);
        _description.MaxLength = 2000; _description.AcceptsReturn = true; _description.TextWrapping = TextWrapping.Wrap;
        _description.MinHeight = 52; _description.MaxHeight = 88;
        _description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Place(SqlAssistChrome.CreateMemoryField("說明（選填）", _description, _description), 4, 0, 5);
        DockPanel.SetDock(_form, Dock.Top); root.Children.Add(_form);

        _cancel = SqlAssistChrome.CreateButton("取消", SqlAssistChrome.DefaultMetrics); _cancel.IsCancel = true;
        _submit = SqlAssistChrome.CreateButton(existing is null ? "新增至收藏" : "儲存", SqlAssistChrome.DefaultMetrics, true);
        _submit.IsDefault = true;
        _submit.ToolTip = "儲存（Ctrl+S）";
        _status.TextWrapping = TextWrapping.Wrap;
        var footer = SqlAssistChrome.CreateDialogFooter(_status, _cancel, _submit);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var sqlHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(sqlHeader, Dock.Top);
        var sqlLabel = SqlAssistChrome.CreateLabel("SQL", SqlAssistChrome.DefaultMetrics); sqlLabel.Margin = default;
        DockPanel.SetDock(sqlLabel, Dock.Left); sqlHeader.Children.Add(sqlLabel);
        _sqlSummary.HorizontalAlignment = HorizontalAlignment.Right; _sqlSummary.VerticalAlignment = VerticalAlignment.Bottom;
        sqlHeader.Children.Add(_sqlSummary);
        var sqlSection = new DockPanel();
        sqlSection.Children.Add(sqlHeader); sqlSection.Children.Add(_editor);
        root.Children.Add(sqlSection);
        Content = root;

        _name.Text = name.Length > 200 ? name.Substring(0, 200) : name;
        _description.Text = description ?? "";
        _server.Text = server ?? ""; _database.Text = database ?? "";
        _initial = (_name.Text, _description.Text, _server.Text, _database.Text);
        foreach (var field in new[] { _name, _description, _server, _database }) field.TextChanged += (_, _) => Validate();
        _editor.Changed += (_, _) => Validate();
        _submit.Click += (_, _) => _ = SqlMemoryActions.RunAsync(SubmitAsync, Report);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.S || e.KeyboardDevice.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            _ = SqlMemoryActions.RunAsync(SubmitAsync, Report);
        };
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
        Closing += OnClosing;
        Closed += (_, _) => _theme.Dispose();
        Validate();
    }

    /// <summary>編輯既有收藏；<paramref name="sql"/> 是目前版本的全文。</summary>
    public static bool Edit(SqlAssistPackage package, SqlFavoriteItem item, string sql) =>
        new FavoriteEditorWindow(package, item, null, sql, item.Favorite.Name, item.Favorite.Description,
            item.Favorite.Server, item.Favorite.Database,
            item.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm") + " 更新 · 修改 SQL 儲存後會另存新版本，可在版本歷史回溯。")
            .ShowModal() == true;

    /// <summary>新增收藏。</summary>
    /// <param name="revisionId">來源紀錄已有的版本；SQL 沒改就直接引用它，不複製內容。</param>
    /// <param name="summary">淡色單行，說明收進來的是什麼；使用者只有在這裡看得出選取範圍對不對。</param>
    public static bool Create(SqlAssistPackage package, string sql, string name, SqlConnectionLabel? connection,
        Guid? revisionId, string summary) =>
        new FavoriteEditorWindow(package, null, revisionId, sql, name, null, connection?.Server, connection?.Database, summary)
            .ShowModal() == true;

    private bool IsDirty => _editor.IsModified ||
        (_name.Text, _description.Text, _server.Text, _database.Text) != _initial;

    private void Place(UIElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span);
        _form.Children.Add(element);
    }

    /// <summary>標註欄位：最近使用的 History 連線在前，只在收藏標註出現過的名稱補在後面。</summary>
    private Border TagBar(SqlIcon icon, TextBox input, string label, bool databases) =>
        SqlConnectionTagInput.CreateBar(_package, icon, input, label, databases,
            () => databases && _server.Text.Trim() is { Length: > 0 } text ? text : null, includeFavorites: true, Report);

    private void UseCurrentConnection()
    {
        if (SqlWindowConnections.ReadActive(_package) is not { } connection || string.IsNullOrEmpty(connection.Server))
        {
            Report("目前沒有已連線的 SQL 查詢視窗；標註未變更。");
            return;
        }
        _server.Text = connection.Server;
        _database.Text = connection.Database;
        Report("");
    }

    private void Validate()
    {
        _sqlSummary.Text = _editor.Summary + (_editor.IsModified ? " · 已修改" : "");
        var message = string.IsNullOrWhiteSpace(_name.Text) ? "名稱不可空白。"
            : string.IsNullOrWhiteSpace(_editor.Text) ? "SQL 不可空白。" : "";
        if (!_submitting && !_conflict) Report(message);
        _submit.IsEnabled = message.Length == 0 && !_submitting && !_conflict && (_existing is null || IsDirty);
    }

    private async Task SubmitAsync()
    {
        if (_submitting || !_submit.IsEnabled) return;
        // SQL 沒改就引用既有版本：編輯時是目前版本，新增時是來源紀錄的版本；否則收藏自己建一份新版本。
        var reference = _existing?.Favorite.CurrentRevisionId ?? _sourceRevisionId;
        var sql = reference is not null && !_editor.IsModified ? null : _editor.Text;
        var favorite = new SqlFavorite(_existing?.Favorite.FavoriteId ?? Guid.NewGuid(), _name.Text.Trim(), _description.Text,
            sql is null ? reference!.Value : Guid.NewGuid(), _server.Text, _database.Text);
        var save = new SqlFavoriteSave(favorite, _existing?.Version, DateTimeOffset.UtcNow, sql);
        SetSubmitting(true);
        Report("正在儲存收藏…");
        try
        {
            if (await SqlMemoryHost.Runtime.SaveFavoriteAsync(save, _package.DisposalToken) == SqlFavoriteWriteResult.Conflict)
            {
                _conflict = true;
                Report("收藏已被修改或移除，未覆寫。請先複製需要保留的內容，再取消並重新整理清單。");
                return;
            }
            _committed = true;
            SetSubmitting(false);
            DialogResult = true;
        }
        catch (Exception error)
        {
            // 回應不明時不盲目重送非冪等的儲存；保留輸入，重新整理後才能確認結果。
            _conflict = true;
            Report("儲存未確認：" + error.Message + " 請先複製需要保留的內容，再取消並重新整理；不會自動重送。");
        }
        finally
        {
            if (!_committed) SetSubmitting(false);
        }
    }

    private void SetSubmitting(bool submitting)
    {
        _submitting = submitting;
        _form.IsEnabled = !submitting; _editor.IsReadOnly = submitting; _cancel.IsEnabled = !submitting;
        Validate();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        args.Cancel = _submitting;
        if (_submitting || _committed || !IsDirty) return;
        SqlMemoryActions.Run(() => args.Cancel = !SqlAssistConfirmationWindow.Confirm(this,
            "捨棄變更", "尚有未儲存的變更。", "捨棄後無法回復本次編輯；收藏不會改變。", "捨棄變更"),
            message => { args.Cancel = true; Report(message); });
    }

    private void Report(string message) { _status.Text = message; _status.ToolTip = message.Length == 0 ? null : message; }
}
