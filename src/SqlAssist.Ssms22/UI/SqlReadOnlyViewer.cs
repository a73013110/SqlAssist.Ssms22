using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Preview;

namespace SqlAssist.Ssms22.UI;

/// <summary>SQL 唯讀預覽共用表面；原文與呈現文件分離，全文複製不經 WPF 正規化。</summary>
internal sealed class SqlReadOnlyViewer : UserControl, IDisposable
{
    private readonly RichTextBox _viewer = SqlAssistChrome.CreateCodeViewer(SqlAssistChrome.DefaultMetrics);
    private readonly SqlScriptTheme _theme;
    public string Sql { get; private set; } = "";
    public bool Wrap { get; private set; }
    public Action<string>? ReportError { get; set; }

    public SqlReadOnlyViewer()
    {
        _theme = new SqlScriptTheme(ActiveSqlEditor.Current, _viewer);
        Content = _viewer;
        ActiveSqlEditor.Changed += OnEditorChanged;
        _viewer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Copy(CopySelection);
            }
        };
        var menu = new ContextMenu();
        foreach (var entry in new[] { ("複製選取", (Action)CopySelection), ("複製全文", (Action)CopyAll) })
        {
            var item = new MenuItem { Header = entry.Item1 };
            item.Click += (_, _) => Copy(entry.Item2);
            menu.Items.Add(item);
        }
        VsThemeBrushes.Apply(menu);
        _viewer.ContextMenu = menu;
    }

    public void SetSql(string sql)
    {
        Sql = sql;
        _theme.EnsureCurrent();
        _viewer.Document = SqlScriptDocument.Build(sql, _theme.Resources);
        SetWrap(Wrap);
        _viewer.ScrollToHome();
    }
    public void SetWrap(bool wrap)
    {
        Wrap = wrap;
        _viewer.Document.PageWidth = wrap ? double.NaN : 4000;
        _viewer.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }
    public void CopyAll() => Clipboard.SetText(Sql);
    public void CopySelection()
    {
        if (!_viewer.Selection.IsEmpty) Clipboard.SetText(SqlScriptDocument.ReadOriginalSelection(_viewer, Sql));
    }
    private void Copy(Action action)
    {
        // 剪貼簿可能被其他程序占用；必須通知使用者，避免以為下一次貼上是新 SQL。
        try { action(); }
        catch (Exception error) { ReportError?.Invoke("複製 SQL 失敗：" + error.Message); }
    }
    private void OnEditorChanged(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Run("更新SQL 唯讀預覽主題來源", () => _theme.SetView(ActiveSqlEditor.Current));
    public void Dispose() { ActiveSqlEditor.Changed -= OnEditorChanged; _theme.Dispose(); }
}
