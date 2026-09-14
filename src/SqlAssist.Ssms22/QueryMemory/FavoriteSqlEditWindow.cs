using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.QueryMemory;

internal sealed class FavoriteSqlEditWindow : DialogWindow
{
    private readonly SqlAssistPackage _package;
    private readonly FavoriteQueryEntry _entry;
    private readonly SqlTextEditor _editor;
    private readonly Button _apply;
    private readonly Button _cancel;
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private bool _applying;
    private bool _committed;
    private bool _conflict;

    public FavoriteSqlEditWindow(SqlAssistPackage package, FavoriteQueryEntry entry, string sql)
    {
        _package = package; _entry = entry; _editor = new SqlTextEditor(sql);
        QueryMemoryActions.ConfigureWindow(this, package, "編輯 SQL — " + entry.Query.Name, 900, 620);
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _cancel = SqlAssistChrome.CreateButton("取消", SqlAssistChrome.DefaultMetrics);
        _cancel.IsCancel = true;
        _apply = SqlAssistChrome.CreateButton("更新 SQL", SqlAssistChrome.DefaultMetrics, true);
        _apply.IsDefault = true; _apply.IsEnabled = false;
        actions.Children.Add(_cancel); actions.Children.Add(_apply); footer.Children.Add(actions);
        root.Children.Add(_editor); Content = root;
        _editor.Changed += (_, _) => _apply.IsEnabled = _editor.IsModified && !_applying && !_conflict;
        _apply.Click += (_, _) => _ = QueryMemoryActions.RunAsync(ApplyChangesAsync, Report);
        Closing += OnClosing;
    }

    private async Task ApplyChangesAsync()
    {
        if (_applying || _conflict || !_editor.IsModified) return;
        _applying = true; _apply.IsEnabled = _cancel.IsEnabled = false; _editor.IsReadOnly = true;
        Report("正在儲存 SQL…");
        try
        {
            var edit = new FavoriteQueryEdit(_entry.Query.FavoriteQueryId, _entry.Version, Guid.NewGuid(), _editor.Text, DateTimeOffset.UtcNow);
            var result = await QueryMemoryHost.Runtime.EditFavoriteQuerySqlAsync(edit, _package.DisposalToken);
            if (result == FavoriteQueryWriteResult.Conflict)
            {
                _conflict = true;
                Report("收藏已被修改或刪除，未覆寫。請先複製編輯內容，再取消並重新整理清單。");
                return;
            }
            _committed = true; _applying = false; DialogResult = true;
        }
        catch (Exception error)
        {
            // 回應不明時不盲目重送非冪等編輯；先保留文字，重新讀取版本後才能再編輯。
            _conflict = true;
            Report("儲存未確認：" + error.Message + " 請先複製 SQL，再取消並重新整理；不會自動重送。");
        }
        finally
        {
            _applying = false; _editor.IsReadOnly = false; _cancel.IsEnabled = true;
            _apply.IsEnabled = _editor.IsModified && !_conflict;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        args.Cancel = _applying;
        if (_applying || _committed || !_editor.IsModified) return;
        QueryMemoryActions.Run(() => args.Cancel = !SqlAssistConfirmationWindow.Confirm(this,
            "捨棄 SQL 變更", "尚有未儲存的 SQL。", "捨棄後無法回復本次編輯；收藏不會改變。", "捨棄變更"),
            message => { args.Cancel = true; Report(message); });
    }
    private void Report(string message) { _status.Text = message; _status.ToolTip = message; }
}
