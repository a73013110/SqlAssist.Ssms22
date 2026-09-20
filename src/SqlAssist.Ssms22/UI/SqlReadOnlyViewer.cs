using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlAssist.Core.Matching;
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
        // 不換行時這裡會有水平捲軸，而拖那條軌道是停靠面板裡唯一的左右捲動方式。
        SqlAssistChrome.ApplyShiftWheelPan(_viewer);
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

    public void SetSql(string sql) => SetSql(sql, null);

    /// <param name="highlights">
    /// 要標出來並捲到可見的區段，索引落在 <paramref name="sql"/> 上；null 或空的就整份從頭顯示。
    /// 位置對不上時<b>傳空的</b>，不要塞一組猜的——畫錯位置的高亮看起來像是比對錯了。
    /// </param>
    public void SetSql(string sql, IReadOnlyList<MatchSpan>? highlights)
    {
        Sql = sql;
        _theme.EnsureCurrent();
        var document = SqlScriptDocument.Build(sql, _theme.Resources, highlights, out var anchor);
        _viewer.Document = document;
        SetWrap(Wrap);
        _viewer.ScrollToHome();
        if (anchor is null) return;

        // 版面還沒算完就捲動會落在錯的位置；排到這一輪版面之後再捲，並確認文件還是剛才那一份
        // ——使用者可能在這中間又換了一列。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            SqlAssistPlatformGuard.Run("捲到 SQL 命中位置", () =>
            {
                if (ReferenceEquals(_viewer.Document, document)) anchor.BringIntoView();
            })));
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
