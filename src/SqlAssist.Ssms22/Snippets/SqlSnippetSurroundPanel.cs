using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>不依賴 SSMS 的包夾內容面板；事件與視窗生命週期由 Picker 接線。</summary>
internal sealed class SqlSnippetSurroundPanel : Grid
{
    private const int PreviewLimit = 16000;
    private readonly IReadOnlyList<SqlSnippet> _snippets;
    private readonly SqlSnippetSurroundSelection _selection;
    private readonly SqlSnippet? _preferred;
    private readonly TextBlock _count;
    private readonly TextBlock _previewHint;
    private SqlSnippet? _previewed;

    public SqlSnippetSurroundPanel(IReadOnlyList<SqlSnippet> snippets, int selectedIndex,
        SqlSnippetSurroundSelection selection)
    {
        _snippets = snippets;
        _selection = selection;
        _preferred = snippets.Count == 0 ? null : snippets[Math.Min(Math.Max(selectedIndex, 0), snippets.Count - 1)];
        var metrics = SqlAssistChrome.DefaultMetrics;
        // 右下角讓給調整大小的握把，其餘沿用浮動內容的外距。
        Margin = new Thickness(12, 12, 12, 12);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        // 標題列已經說了這是哪個工具；第一列只放新的資訊：實際會被包住的是哪些內容。
        header.Children.Add(SqlAssistChrome.CreateMetadataText(Describe(selection), metrics));
        SearchBox = SqlAssistChrome.CreateTextBox(metrics);
        SearchBox.Margin = new Thickness(0, 8, 0, 4);
        SearchBox.ToolTip = "搜尋捷徑、標題或說明；空白分隔多個關鍵字";
        AutomationProperties.SetName(SearchBox, "搜尋片段：捷徑、標題或說明");
        var label = SqlAssistChrome.CreateMetadataText("搜尋捷徑、標題或說明", metrics);
        header.Children.Add(label);
        header.Children.Add(SearchBox);
        _count = SqlAssistChrome.CreateStatusText(metrics);
        header.Children.Add(_count);
        Children.Add(header);

        var body = new Grid();
        // 清單只放捷徑與標題，寬度夠讀就好；剩下的都留給預覽，那才是要看完的內容。
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        List = new ListBox
        {
            BorderThickness = new Thickness(0),
            ItemContainerStyle = SqlAssistChrome.CreateListItemStyle(metrics),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsTextSearchEnabled = false
        }.WithTheme(Control.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(Control.ForegroundProperty, ThemeBrush.ListForeground);
        AutomationProperties.SetName(List, "可包夾片段");
        ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        body.Children.Add(List);

        var detail = new DockPanel();
        _previewHint = SqlAssistChrome.CreateMetadataText("套用預覽", metrics);
        _previewHint.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_previewHint, Dock.Top);
        detail.Children.Add(_previewHint);
        Preview = SqlAssistChrome.CreateCodeViewer(metrics);
        Preview.ToolTip = "反白處是選取的 SQL，淡色是片段新增的外框";
        AutomationProperties.SetName(Preview, "包夾後 SQL 預覽");
        detail.Children.Add(Preview);
        Grid.SetColumn(detail, 2);
        body.Children.Add(detail);
        Grid.SetRow(body, 1);
        Children.Add(body);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        CancelButton = SqlAssistChrome.CreateButton("取消", metrics);
        ApplyButton = SqlAssistChrome.CreateButton("套用", metrics, primary: true);
        CancelButton.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(ApplyButton);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(SqlAssistChrome.CreateMetadataText("↑↓ 選擇 · Enter 套用 · Esc 取消", metrics));
        Grid.SetRow(footer, 2);
        Children.Add(footer);
        Filter(string.Empty);
    }

    public TextBox SearchBox { get; }
    public ListBox List { get; }
    public RichTextBox Preview { get; }

    /// <summary>目前預覽的純文字；上色不改內容，測試與診斷都看這一份。</summary>
    public string PreviewText { get; private set; } = string.Empty;
    public Button ApplyButton { get; }
    public Button CancelButton { get; }
    public SqlSnippet? SelectedSnippet => (List.SelectedItem as ListBoxItem)?.Tag as SqlSnippet;

    public void Filter(string query)
    {
        var previous = string.IsNullOrWhiteSpace(query) ? _preferred : SelectedSnippet;
        var candidates = SqlSnippetSearch.Filter(_snippets, query);
        List.Items.Clear();
        foreach (var snippet in candidates)
        {
            List.Items.Add(CreateItem(snippet));
        }

        // 完整捷徑優先，避免輸入 ifb 後 Enter 卻仍套用說明裡剛好提到 ifb 的另一筆。
        var chosen = candidates.FirstOrDefault(item => string.Equals(item.Shortcut, query.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(item => ReferenceEquals(item, previous))
            ?? candidates.FirstOrDefault();
        List.SelectedItem = List.Items.Cast<ListBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, chosen));
        _count.Text = candidates.Count == 0 ? "沒有相符片段；請修改或清除搜尋" : $"{candidates.Count} / {_snippets.Count} 個可包夾片段";
        UpdatePreview();
    }

    public void UpdatePreview()
    {
        var snippet = SelectedSnippet;
        ApplyButton.IsEnabled = snippet is not null;
        // 篩選會重建容器；即使仍選同一筆，也要讓 Enter 即將套用的項目留在畫面內。
        if (List.SelectedItem is not null)
        {
            List.ScrollIntoView(List.SelectedItem);
        }

        if (ReferenceEquals(snippet, _previewed))
        {
            return;
        }

        _previewed = snippet;
        if (snippet is null)
        {
            PreviewText = string.Empty;
            SqlAssistChrome.SetCode(Preview, string.Empty, Array.Empty<Inline>());
            _previewHint.Text = "套用預覽";
            return;
        }

        // 使用真正的展開結果，不以 Replace 模擬欄位、跳脫或縮排。
        var expanded = snippet.WithSurroundText(_selection.Text);
        var render = expanded.Expansion.Render("\n", _selection.BaseIndent);
        var text = _selection.BaseIndent + render.Text;
        var truncated = text.Length > PreviewLimit;
        PreviewText = truncated ? text.Substring(0, PreviewLimit) : text;

        // 前面補的基準縮排也要算進位置；由 Core 一次算好，不在這裡用字串比對找。
        var start = render.HasSurround ? render.SurroundOffset + _selection.BaseIndent.Length : -1;
        SqlAssistChrome.SetCode(Preview, PreviewText, Highlight(PreviewText, start, render.SurroundLength));
        _previewHint.Text = truncated
            ? "預覽已截短；套用仍保留完整 SQL"
            : expanded.ExpansionMode == SqlSnippetExpansionMode.TabStops
                ? $"套用後以 Tab 填寫 {expanded.Expansion.Fields.Count} 個欄位"
                : "套用後游標移至結尾落點";
    }

    /// <summary>
    /// 把預覽分成「選取的 SQL」與「片段新增的外框」兩種呈現。
    /// </summary>
    /// <remarks>
    /// 新增的部分用淡色而不是強調色：強調色在這個介面裡是焦點與選取的意思，
    /// 而且真正該一眼看到的是「我的 SQL 被放到哪裡」，不是外框本身。
    /// 範圍超出截短後的長度時只標到看得見的地方，不動實際插入的內容。
    /// </remarks>
    private static IEnumerable<Inline> Highlight(string text, int start, int length)
    {
        var from = Math.Max(0, Math.Min(start, text.Length));
        var to = start < 0 ? from : Math.Max(from, Math.Min(start + length, text.Length));
        if (start < 0 || to == from)
        {
            yield return Frame(text);
            yield break;
        }

        yield return Frame(text.Substring(0, from));
        foreach (var line in SelectedLines(text.Substring(from, to - from)))
        {
            yield return line;
        }

        yield return Frame(text.Substring(to));
    }

    /// <remarks>
    /// 每一行的前導空白留在外框那一色。整行從行首開始上底色的話，會像連縮排都被選走，
    /// 而第一行的縮排本來就屬於樣板，兩者對不齊看起來更像畫錯。
    /// </remarks>
    private static IEnumerable<Inline> SelectedLines(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            // 預覽一律以 \n 繪製；換行本身跟著上一段走，不另外開一段。
            var lineEnd = text.IndexOf('\n', index);
            var stop = lineEnd < 0 ? text.Length : lineEnd + 1;
            var content = index;
            while (content < stop && (text[content] == ' ' || text[content] == '\t'))
            {
                content++;
            }

            if (content > index)
            {
                yield return Frame(text.Substring(index, content - index));
            }

            if (stop > content)
            {
                yield return Selected(text.Substring(content, stop - content));
            }

            index = stop;
        }
    }

    private static Run Frame(string text) =>
        new Run(text).WithTheme(TextElement.ForegroundProperty, ThemeBrush.DimForeground);

    private static Run Selected(string text) =>
        new Run(text)
            .WithTheme(TextElement.ForegroundProperty, ThemeBrush.ListForeground)
            .WithTheme(TextElement.BackgroundProperty, ThemeBrush.BadgeBackground);

    /// <summary>被包住的內容有多少；跨行擴成整行時要先講，那是使用者沒有選到的部分。</summary>
    private static string Describe(SqlSnippetSurroundSelection selection)
    {
        var summary = $"包住 {selection.LineCount} 行 · {selection.Text.Length} 個字元，不會執行 SQL";
        return selection.ExpandedToLines ? $"跨行選取已擴成整行；請先確認預覽 · {summary}" : summary;
    }

    private static ListBoxItem CreateItem(SqlSnippet snippet)
    {
        var metrics = SqlAssistChrome.DefaultMetrics;
        var row = new StackPanel();
        var heading = new DockPanel();
        var badge = SqlAssistChrome.CreateBadge(snippet.Shortcut, metrics);
        badge.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(badge, Dock.Left);
        heading.Children.Add(badge);
        heading.Children.Add(new TextBlock
        {
            Text = snippet.Title,
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = metrics.Body,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(heading);
        var description = SqlAssistChrome.CreateMetadataText(snippet.Description, metrics);
        description.Margin = new Thickness(0, 4, 0, 0);
        row.Children.Add(description);
        var item = new ListBoxItem { Content = row, Tag = snippet, ToolTip = $"{snippet.Shortcut} — {snippet.Title}\n{snippet.Description}" };
        AutomationProperties.SetName(item, $"{snippet.Shortcut} {snippet.Title} {snippet.Description}");
        return item;
    }
}
