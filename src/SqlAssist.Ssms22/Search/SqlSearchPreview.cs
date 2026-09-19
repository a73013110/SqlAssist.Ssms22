using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 選取那一筆的完整定義，命中位置捲到可見並高亮。
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
    /// <summary>選取停下來多久才去查；與 SQL Memory 的預覽同一個數字。</summary>
    /// <remarks>
    /// 短到放開方向鍵就開始載入，長到連續捲十列只發最後那一輪。
    /// </remarks>
    private static readonly TimeSpan LoadDelay = TimeSpan.FromMilliseconds(220);

    private readonly SqlSearchDefinitionLoader _loader;
    private readonly SqlReadOnlyViewer _viewer = new();
    private readonly SqlLoadingSurface _loading;
    private readonly TextBlock _metadata = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly SqlHighlightText _snippet = new();
    private readonly Border _snippetSurface;
    private readonly TextBlock _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private readonly TextBlock _empty = SqlAssistChrome.CreateSearchEmptyState();
    private readonly Grid _body = new();
    private readonly DockPanel _content;
    private readonly Button _copyName;
    private readonly Button _wrap;
    private readonly DispatcherTimer _delay;
    private CancellationTokenSource _read = new();
    private bool _disposed;

    public SqlSearchPreview(SqlSearchCatalogs catalogs)
    {
        _loader = new SqlSearchDefinitionLoader(catalogs);

        _copyName = SqlAssistChrome.CreateIconButton(SqlIcon.Copy, "複製限定名稱");
        _copyName.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
        // 複製定義本身不另放一顆同圖示的按鈕；唯讀檢視的右鍵選單已經有「複製全文」與
        // 「複製選取」，兩顆 Copy 並排只會讓人先猜哪一顆是哪一個。
        _wrap = SqlAssistChrome.CreateIconButton(SqlIcon.Wrap, "切換 SQL 顯示換行");
        _wrap.Click += (_, _) => Guarded(() => _viewer.SetWrap(!_viewer.Wrap));

        _snippet.FontFamily = SqlAssistChrome.CodeFont;
        _snippet.TextWrapping = TextWrapping.Wrap;
        _snippet.TextTrimming = TextTrimming.None;
        _snippet.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        _snippetSurface = SqlAssistChrome.CreateSurface(_snippet);
        _snippetSurface.Padding = new Thickness(8, 6, 8, 6);
        _snippetSurface.VerticalAlignment = VerticalAlignment.Top;
        _snippetSurface.Visibility = Visibility.Collapsed;

        _viewer.ReportError = Report;
        _loading = new SqlLoadingSurface(_viewer);

        // 三種「現在沒有完整定義可看」疊在同一塊內容上：載入中、沒有定義的來源，
        // 以及什麼都沒選。各占一塊版面的話，窄面板裡的指令碼會被擠到剩兩行。
        _body.Children.Add(_loading);
        _body.Children.Add(_snippetSurface);
        _body.Children.Add(_empty);

        var toolbar = new WrapPanel();
        toolbar.Children.Add(_copyName);
        toolbar.Children.Add(_wrap);

        _metadata.TextWrapping = TextWrapping.NoWrap;
        _metadata.TextTrimming = TextTrimming.CharacterEllipsis;
        _metadata.Margin = new Thickness(0, 0, 0, 8);
        _status.TextWrapping = TextWrapping.Wrap;

        _content = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        _content.Children.Add(toolbar);
        DockPanel.SetDock(_metadata, Dock.Top);
        _content.Children.Add(_metadata);
        DockPanel.SetDock(_status, Dock.Bottom);
        _content.Children.Add(_status);
        _content.Children.Add(_body);
        Content = _content;

        _delay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = LoadDelay };
        _delay.Tick += (_, _) => SqlAssistPlatformGuard.Run("載入 SQL Search 預覽", () =>
        {
            _delay.Stop();
            _ = RunAsync(LoadAsync);
        });

        AutomationProperties.SetName(this, "搜尋結果預覽");
        Select(null);
    }

    /// <summary>主從區抬頭那一行；由分割檢視放在收合鈕旁邊，與 SQL Memory 同一個位置。</summary>
    public FrameworkElement Summary { get; } =
        SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);

    /// <summary>複製限定名稱；實際寫剪貼簿的失敗要看得見，所以交給宿主處理。</summary>
    public event EventHandler? CopyRequested;

    /// <summary>目前顯示的那一筆；沒有選取時 null。</summary>
    public SqlSearchRow? Current { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _delay.Stop();
        _loading.IsLoading = false;
        _read.Cancel();
        _read.Dispose();
        _viewer.Dispose();
    }

    /// <summary>連線換了或使用者按了重新整理；記著的定義可能已經不是現在這台伺服器的。</summary>
    public void InvalidateDefinitions() => _loader.Clear();

    public void Select(SqlSearchRow? row)
    {
        if (_disposed) return;

        // 上一輪的結果不能污染這一次的選取，所以先取消再換狀態。
        _delay.Stop();
        _read.Cancel();
        _read.Dispose();
        _read = new CancellationTokenSource();

        Current = row;
        _copyName.IsEnabled = row is not null;
        _wrap.IsEnabled = false;
        _viewer.SetSql("");
        _loading.IsLoading = false;
        _snippetSurface.Visibility = Visibility.Collapsed;
        Report("");

        if (row is null)
        {
            _metadata.Text = "";
            _metadata.Visibility = Visibility.Collapsed;
            _loading.Visibility = Visibility.Collapsed;
            _empty.Text = "選一筆結果看它的完整定義與命中位置。";
            _empty.Visibility = Visibility.Visible;
            ((TextBlock)Summary).Text = "";
            return;
        }

        _empty.Visibility = Visibility.Collapsed;
        // 分類與命中部位是兩件事：同一個物件可以同時被名稱與定義本文命中。
        _metadata.Text = row.Description;
        _metadata.ToolTip = row.Description;
        _metadata.Visibility = Visibility.Visible;
        ((TextBlock)Summary).Text = row.Title + " · " + row.CategoryLabel + " · " + row.TargetLabel;

        if (row.Hit.ActivatePayload is not SqlCatalogSearchTarget)
        {
            // 沒有目錄物件可以問的來源（之後的片段、SQL Memory）只剩片段可看；
            // 留一塊空白等於讓使用者以為載入卡住了。
            _loading.Visibility = Visibility.Collapsed;
            _snippet.SourceText = row.Snippet;
            _snippet.Spans = row.SnippetSpans;
            _snippetSurface.Visibility = row.Snippet.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _empty.Text = row.Snippet.Length == 0 ? "這一筆沒有可以顯示的定義。" : "";
            _empty.Visibility = row.Snippet.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SqlAssistChrome.PlayAppear(_body);
            return;
        }

        _loading.Visibility = Visibility.Visible;
        _loading.IsLoading = true;
        _delay.Start();
    }

    private async Task LoadAsync()
    {
        var row = Current;
        var token = _read.Token;

        if (_disposed || row is null || row.Hit.ActivatePayload is not SqlCatalogSearchTarget target) return;

        try
        {
            var definition = await _loader.LoadAsync(target, token);

            // 舊請求的成功回應與舊請求的失敗一樣，都不能蓋掉目前這一列。
            if (_disposed || token.IsCancellationRequested || !ReferenceEquals(row, Current)) return;

            if (definition.Failure is { } failure)
            {
                Report(failure);
                return;
            }

            var highlights = SqlSearchDefinitionHighlight.Locate(row.Hit, definition.Script);
            _viewer.SetSql(definition.Script, highlights);
            _wrap.IsEnabled = true;

            // 對不上時不高亮也不捲動，但要說一句：整份定義從頭顯示而沒有任何標記時，
            // 使用者會以為是面板壞了，而不是這一筆的位置對不起來。
            // 比對的是命中原本那幾段，不是清單攤平之後留下來的：攤平會丟掉被切掉的區段，
            // 拿它判斷會在「片段太長」時誤報成對不上。
            Report(highlights.Count != 0 || row.Hit.SnippetSpans.Count == 0
                ? ""
                : "命中位置對不上這一份定義，已顯示完整定義。");
            SqlAssistChrome.PlayAppear(_body);
        }
        finally
        {
            // 舊請求的結束不能關掉新選取的載入效果。
            if (!_disposed && ReferenceEquals(row, Current)) _loading.IsLoading = false;
        }
    }

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
