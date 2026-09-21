using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private IReadOnlyList<IReadOnlyList<Run>> _matches = Array.Empty<IReadOnlyList<Run>>();
    private int _current = -1;
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

    /// <summary>這一份指令碼上標了幾處命中；沒有高亮時是 0。</summary>
    public int MatchCount => _matches.Count;

    public void SetSql(string sql) => SetSql(sql, null);

    /// <param name="highlights">
    /// 要標出來的區段，索引落在 <paramref name="sql"/> 上；null 或空的就整份從頭顯示。
    /// 位置對不上時<b>傳空的</b>，不要塞一組猜的——畫錯位置的高亮看起來像是比對錯了。
    /// </param>
    /// <remarks>
    /// 這裡<b>不</b>捲動也不挑「目前」是哪一處：那是 <see cref="ShowMatch"/> 的事。
    /// 兩件事併在一起的那一版，呼叫端要嘛每次重設內容都被迫捲到第一處，要嘛得為了換一個
    /// 目前位置而重建整份文件。
    /// </remarks>
    public void SetSql(string sql, IReadOnlyList<MatchSpan>? highlights)
    {
        Sql = sql;
        _theme.EnsureCurrent();
        _viewer.Document = SqlScriptDocument.Build(sql, _theme.Resources, highlights, out _matches);
        _current = -1;
        SetWrap(Wrap);
        _viewer.ScrollToHome();
    }

    /// <summary>
    /// 把第 <paramref name="index"/> 處命中換成「目前」的樣子並捲到可見。
    /// </summary>
    /// <remarks>
    /// 只改那兩處的樣式，不重建文件：重建一次幾千行的指令碼會讓每按一次「下一個命中」
    /// 都掉影格，而且選取與捲動位置會一起被丟掉。
    /// </remarks>
    /// <param name="scroll">false 只換樣式；重設內容時的第一次由呼叫端決定要不要跟著捲。</param>
    public void ShowMatch(int index, bool scroll = true)
    {
        if (index < 0 || index >= _matches.Count) return;

        if (_current >= 0 && _current < _matches.Count) SqlScriptDocument.SetCurrentMatch(_matches[_current], current: false);
        _current = index;
        SqlScriptDocument.SetCurrentMatch(_matches[index], current: true);

        if (!scroll || _matches[index].Count == 0) return;

        var document = _viewer.Document;
        var anchor = _matches[index][0];

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
