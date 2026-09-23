using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlAssist.Core.Matching;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 選取那一筆的完整定義，每一處命中都高亮，並可以一處一處走過去。
/// </summary>
/// <remarks>
/// 只讀 <c>SearchHit</c> 攤出來的欄位加上酬載指向的那個物件；指令碼本身走
/// <see cref="SqlSearchDefinitionLoader"/>，也就是 F12 那一條既有路徑的同一組出處，
/// 這裡一個字的 T-SQL 都不自己組。
///
/// 呈現用共用的 <see cref="SqlReadOnlyViewer"/>，語法著色與字型跟著 SSMS 編輯器走，
/// 方便與查詢視窗並排比對。
///
/// <b>選取變更一定要去彈跳加取消。</b>使用者用方向鍵在清單上捲過去時，每一列都發一輪
/// 第三／四層查詢會把中繼資料連線打滿，而那幾十輪裡他只看了最後一列。
/// </remarks>
internal sealed class SqlSearchPreview : UserControl, IDisposable
{
    private readonly SqlSearchDefinitionLoader _loader;
    private readonly SqlReadOnlyViewer _viewer = new();
    private readonly SqlStateSurface _surface;
    private readonly SqlHighlightText _snippet = new();
    private readonly Border _snippetSurface;
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly Grid _body = new();
    private readonly DockPanel _content;
    private readonly Button _copyScript;
    private readonly ToggleButton _wrap;
    private readonly SqlMatchNavigation _matches;
    private readonly WrapPanel _toolbar = new();
    private readonly SqlSelectionLoader<SqlSearchRow> _selection;
    private bool _disposed;

    public SqlSearchPreview(SqlSearchCatalogs catalogs)
    {
        _loader = new SqlSearchDefinitionLoader(catalogs);

        // 這一顆複製的是畫面上這一份定義，不是名稱：使用者按預覽裡的複製，要的是
        // 他正在看的那段結構描述；名稱在清單的右鍵選單上（「複製名稱」），那裡才是
        // 「這一列是什麼」的位置。與 SQL Memory 預覽的「複製全文」同一顆、同一個位置。
        _copyScript = SqlAssistChrome.CreateIconButton(SqlIcon.Copy, "複製定義");
        _copyScript.Click += (_, _) => Guarded(() => _viewer.CopyAll());
        // 換行是一個維持著的狀態不是一次動作，所以是開關不是按鈕：按完之後工具列上看得出
        // 現在是開著的，理由見 CreateIconToggle。
        _wrap = SqlAssistChrome.CreateIconToggle(SqlIcon.Wrap, "SQL 顯示換行");
        _wrap.Checked += (_, _) => Guarded(() => _viewer.SetWrap(true));
        _wrap.Unchecked += (_, _) => Guarded(() => _viewer.SetWrap(false));

        _snippet.FontFamily = SqlAssistChrome.CodeFont;
        _snippet.TextWrapping = TextWrapping.Wrap;
        _snippet.TextTrimming = TextTrimming.None;
        _snippet.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        _snippetSurface = SqlAssistChrome.CreateSurface(_snippet);
        _snippetSurface.Padding = new Thickness(8, 6, 8, 6);
        _snippetSurface.VerticalAlignment = VerticalAlignment.Top;
        _snippetSurface.Visibility = Visibility.Collapsed;

        _viewer.ReportError = Report;
        _matches = new SqlMatchNavigation(_viewer);
        _surface = new SqlStateSurface(_viewer);

        // 「現在沒有完整定義可看」全部疊在同一塊內容上：載入中、沒有定義的來源、
        // 讀不到的定義，以及什麼都沒選。各占一塊版面的話，窄面板裡的指令碼會被擠到剩兩行。
        _body.Children.Add(_surface);
        _body.Children.Add(_snippetSurface);

        // 導覽與界線排在最前面，排法與 SQL Memory 預覽同一份（SqlMatchNavigation）。
        foreach (var item in _matches.ToolbarItems) _toolbar.Children.Add(item);
        _toolbar.Children.Add(_copyScript);
        _toolbar.Children.Add(_wrap);

        _status.TextWrapping = TextWrapping.Wrap;

        // 內容第一列直接是工具列：這一筆是什麼，全部交給主從區抬頭上那一列膠囊，
        // 內容裡不再放一份只換了排列順序的同樣文字。
        _content = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        _content.Children.Add(_toolbar);
        DockPanel.SetDock(_status, Dock.Bottom);
        _content.Children.Add(_status);
        _content.Children.Add(_body);
        Content = _content;

        _selection = new SqlSelectionLoader<SqlSearchRow>(Dispatcher, SqlAssistChrome.Debounce.Preview,
            (row, token) => SqlAssistPlatformGuard.Run("載入 SQL Search 預覽", () => _ = RunAsync(() => LoadAsync(row, token))));

        AutomationProperties.SetName(this, "搜尋結果預覽");
        Select(null);
    }

    /// <summary>
    /// 主從區抬頭那一列；由分割檢視放在收合鈕旁邊，與 SQL Memory 同一個位置與同一種膠囊。
    /// </summary>
    /// <remarks>
    /// 交出去的是一個吃 <see cref="SqlSearchRow"/> 的 <see cref="ContentControl"/>，不是一段字：
    /// 樣板只有 <c>SqlAssistChrome.CreateSearchMetadataTemplate</c> 一份，與清單列同一個順序，
    /// 兩邊各排各的話，使用者在清單上選一筆、眼睛移到這一列，同一組事實卻換了位置。
    /// </remarks>
    public FrameworkElement Summary { get; } = new ContentControl
    {
        ContentTemplate = SqlAssistChrome.CreateSearchMetadataTemplate(),
        Focusable = false,
        VerticalAlignment = VerticalAlignment.Center,
        // 還沒選時整列收起來：樣板照樣會把膠囊的底畫出來，而綁不到值的那一顆就是抬頭上
        // 那一塊沒有字的灰色方塊——看起來像是有一筆結果，只是它的名稱沒載進來。
        Visibility = Visibility.Collapsed
    };

    /// <summary>目前顯示的那一筆；沒有選取時 null。</summary>
    public SqlSearchRow? Current => _selection.Current;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.State = SqlSurfaceState.None;
        _selection.Dispose();
        _viewer.Dispose();
    }

    /// <summary>使用者按了重新整理；記著的定義可能已經改過。</summary>
    /// <remarks>
    /// 換範圍<b>不</b>清：快取鍵是那一筆自己的伺服器、資料庫與編號（見
    /// <see cref="SqlSearchDefinitionLoader"/>），別台同號的物件本來就對不上同一個鍵。
    /// </remarks>
    public void InvalidateDefinitions() => _loader.Clear();

    public void Select(SqlSearchRow? row)
    {
        if (_disposed) return;

        // 上一輪的結果不能污染這一次的選取，所以先取消再換狀態。
        _selection.Select(row);
        // 沒有選取就沒有東西可以複製或換行：停用的兩顆圖示浮在一塊空白上方，說不出
        // 「這裡本來會有什麼」，而那一句已經由狀態表面說了。不適用的操作一律收起。
        _toolbar.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        Summary.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        // 兩顆都跟著「有沒有一份定義在畫面上」：只提供片段的來源複製不出結構描述，
        // 而一顆複製得到半句話的按鈕比沒有那一顆更難解釋。定義載進來才開。
        _copyScript.IsEnabled = false;
        _wrap.IsEnabled = false;
        _matches.Clear();
        _surface.State = SqlSurfaceState.None;
        _snippetSurface.Visibility = Visibility.Collapsed;
        Report("");

        // 抬頭那一列直接吃這一筆；分類與命中部位是兩件事，同一個物件可以同時被名稱與本文命中，
        // 所以兩顆膠囊各自出現，不併成一句話。
        ((ContentControl)Summary).Content = row;

        if (row is null)
        {
            // 收起唯讀檢視本身，狀態表面留著：空的檢視在說明文字後面會露出一塊編輯區底色。
            _viewer.Visibility = Visibility.Collapsed;
            _surface.State = SqlSurfaceState.Empty("尚未選取", "選一筆結果看它的完整定義與命中位置。");
            return;
        }

        if (SqlSearchActivation.DefinitionOf(row.Hit) is null)
        {
            // 沒有目錄物件可以問的來源（之後的片段、SQL Memory）只剩片段可看；
            // 留一塊空白等於讓使用者以為載入卡住了。
            _viewer.Visibility = Visibility.Collapsed;
            _snippet.SourceText = row.Snippet;
            _snippet.Spans = row.SnippetSpans;
            _snippetSurface.Visibility = row.Snippet.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _surface.State = row.Snippet.Length == 0
                ? SqlSurfaceState.Empty("沒有可以顯示的定義", "這一筆的來源只提供片段。")
                : SqlSurfaceState.None;
            SqlAssistChrome.PlayAppear(_body);
            return;
        }

        _viewer.Visibility = Visibility.Visible;
        _surface.State = SqlSurfaceState.Loading;
        _selection.Load();
    }

    private async Task LoadAsync(SqlSearchRow row, CancellationToken token)
    {
        if (!_selection.IsCurrent(row, token) || SqlSearchActivation.DefinitionOf(row.Hit) is not { } target) return;

        try
        {
            var definition = await _loader.LoadAsync(target, token);

            // 舊請求的成功回應與舊請求的失敗一樣，都不能蓋掉目前這一列。
            if (!_selection.IsCurrent(row, token)) return;

            if (definition.Failure is { } failure)
            {
                // 讀不到與權限不足在這裡分不開（伺服器兩種都只回「查不到」），所以走同一個
                // 出口，由 provider 自己那一句說明可能的原因。
                _viewer.Visibility = Visibility.Collapsed;
                _surface.State = SqlSurfaceState.Unreadable(failure);
                return;
            }

            var highlights = SqlSearchDefinitionHighlight.Locate(row.Hit, definition.Script);
            _matches.Show(definition.Script, highlights);
            _copyScript.IsEnabled = _wrap.IsEnabled = true;

            // 對不上時不高亮也不捲動，但要說一句：整份定義從頭顯示而沒有任何標記時，
            // 使用者會以為是面板壞了，而不是這一筆的位置對不起來。
            // 比對的是命中原本那幾段，不是清單攤平之後留下來的：攤平會丟掉被切掉的區段，
            // 拿它判斷會在「片段太長」時誤報成對不上。
            Report(highlights.Notice(row.Hit.SnippetSpans.Count == 0 ? null : Unmatched));
            SqlAssistChrome.PlayAppear(_body);
        }
        finally
        {
            // 舊請求的結束不能關掉新選取的載入效果，也不能蓋掉這一輪剛寫上去的讀不到。
            if (_selection.IsCurrent(row, token) && _surface.IsLoading) _surface.State = SqlSurfaceState.None;
        }
    }

    /// <summary>命中位置對不上這一份定義時的那一句；少標了的那一句在 <see cref="MatchHighlightSet.Notice"/>，兩個預覽共用。</summary>
    private const string Unmatched = "命中位置對不上這一份定義，已顯示完整定義。";

    private void Report(string message)
    {
        if (_disposed) return;
        _status.Text = message;
        _status.ToolTip = message.Length == 0 ? null : message;
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // 使用者自己按的按鈕失敗要看得見，所以這裡不是 SqlAssistPlatformGuard 而是回到狀態列。
    private void Guarded(Action action) => _ = RunAsync(() => { action(); return Task.CompletedTask; });

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // 取消是換一列的正常流程，不是失敗。
        }
        catch (Exception error)
        {
            SqlAssistDiagnostics.WriteAlways("SQL Search 預覽失敗：" + error.Message);
            Report(error.Message);
        }
    }
}
