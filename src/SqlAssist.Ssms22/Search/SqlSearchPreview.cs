using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 選取那一筆的細節：限定名稱、分類與命中片段。
/// </summary>
/// <remarks>
/// 只讀 <c>SearchHit</c> 攤出來的欄位。刻意<b>不</b>拉進 <see cref="SqlReadOnlyViewer"/>：
/// 那一份是為整份 SQL 做的，會連帶帶進編輯器主題與「空 SQL／載入中／已回收」那一整組狀態，
/// 而這裡手上只有一行片段——完整定義由「移至定義」開進新的查詢視窗，不在這個面板裡。
/// </remarks>
internal sealed class SqlSearchPreview : UserControl
{
    private readonly SqlHighlightText _title = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _metadata = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private readonly SqlHighlightText _snippet = new();
    private readonly Border _snippetSurface;
    private readonly TextBlock _empty = SqlAssistChrome.CreateSearchEmptyState();
    private readonly StackPanel _body = new();
    private readonly Button _copy;

    public SqlSearchPreview()
    {
        _title.FontSize = SqlAssistChrome.DefaultMetrics.Title;
        _title.Margin = new Thickness(0, 0, 0, 4);

        _snippet.FontFamily = SqlAssistChrome.CodeFont;
        _snippet.TextWrapping = TextWrapping.Wrap;
        _snippet.TextTrimming = TextTrimming.None;
        _snippet.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        _snippetSurface = SqlAssistChrome.CreateSurface(_snippet);
        _snippetSurface.Padding = new Thickness(8, 6, 8, 6);
        _snippetSurface.Margin = new Thickness(0, 8, 0, 0);

        _copy = SqlAssistChrome.CreateIconButton(SqlIcon.Copy, "複製限定名稱");
        _copy.HorizontalAlignment = HorizontalAlignment.Left;
        _copy.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);

        _body.Children.Add(_title);
        _body.Children.Add(_metadata);
        _body.Children.Add(_snippetSurface);
        _body.Margin = new Thickness(0, 8, 0, 0);

        var toolbar = new WrapPanel();
        toolbar.Children.Add(_copy);
        var scroll = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        var content = new Grid();
        content.Children.Add(scroll);
        content.Children.Add(_empty);
        layout.Children.Add(content);
        Content = layout;
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

    public void Select(SqlSearchRow? row)
    {
        Current = row;
        _copy.IsEnabled = row is not null;

        if (row is null)
        {
            _body.Visibility = Visibility.Collapsed;
            _empty.Text = "選一筆結果看它的位置與命中內容。";
            _empty.Visibility = Visibility.Visible;
            ((TextBlock)Summary).Text = "";
            return;
        }

        _empty.Visibility = Visibility.Collapsed;
        _body.Visibility = Visibility.Visible;
        _title.SourceText = row.Title;
        _title.Spans = row.TitleSpans;
        // 分類與命中種類是兩件事：同一個物件可以同時出現在名稱與定義本文兩組裡。
        _metadata.Text = row.CategoryLabel + " · " + row.GroupLabel + (row.Path.Length == 0 ? "" : " · " + row.Path);
        _snippetSurface.Visibility = row.Snippet.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _snippet.SourceText = row.Snippet;
        _snippet.Spans = row.SnippetSpans;
        ((TextBlock)Summary).Text = row.Path.Length == 0 ? row.Title : row.Path;
        SqlAssistChrome.PlayAppear(_body);
    }
}
