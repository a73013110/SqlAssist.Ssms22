using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>
/// 「用哪一個片段包住選取範圍」的清單。
/// </summary>
/// <remarks>
/// <b>沒有做成建議清單的一種。</b>非同步補全的 session 是綁在游標與一個
/// <c>ApplicableToSpan</c> 上的，而這裡有一段使用者自己拉出來的選取範圍要保住——
/// 平台會在提交時把那個範圍換掉，於是要包的內容在選片段的過程中就先不見了。
///
/// <b>也沒有做成對話框。</b>這是編輯到一半按一下的動作，模態視窗會把焦點與畫面
/// 都換掉；片段管理員那種對話框範本留給「坐下來設定一次」的場合。
///
/// 樣式一律取自 <see cref="SqlAssistChrome"/>，配色繫結
/// <see cref="VsThemeBrushes"/> 的動態資源，因此深淺主題與高對比都跟著殼層走。
/// </remarks>
internal sealed class SqlSnippetSurroundPicker
{
    private const double MinimumWidth = 340;
    private const double MaximumWidth = 560;
    private const double MaximumListHeight = 320;

    private readonly IWpfTextView _view;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly Action<SqlSnippet> _chosen;
    private bool _closed;

    private SqlSnippetSurroundPicker(
        IWpfTextView view,
        IReadOnlyList<SqlSnippet> snippets,
        int selectedIndex,
        Action<SqlSnippet> chosen)
    {
        _view = view;
        _chosen = chosen;

        var metrics = SqlAssistChrome.DefaultMetrics;
        _list = CreateList(snippets, selectedIndex, metrics);

        var layout = new StackPanel { Margin = new Thickness(8) };
        layout.Children.Add(_list);
        layout.Children.Add(new TextBlock
        {
            Text = "Enter 套用 · Esc 取消 · 直接輸入捷徑首字可跳到該筆",
            Margin = new Thickness(10, 8, 10, 2),
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = metrics.Caption
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground));

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(SqlAssistChrome.InnerRadius + 1),
            MinWidth = MinimumWidth,
            MaxWidth = MaximumWidth,
            SnapsToDevicePixels = true,
            Child = layout
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(Border.BorderBrushProperty, ThemeBrush.Border);

        VsThemeBrushes.Apply(frame);

        _popup = new Popup
        {
            Child = frame,
            AllowsTransparency = true,

            // 可取得焦點是必要的：這個 Popup 有自己的 HWND，不設的話方向鍵與
            // Enter 會留在編輯器那邊，症狀是清單開著卻只能用滑鼠。
            Focusable = true,
            PopupAnimation = PopupAnimation.None,
            Placement = PlacementMode.Relative,
            PlacementTarget = view.VisualElement,

            // 焦點離開就關：選片段的過程中使用者點回編輯器，代表他放棄了這一次，
            // 而留著一個看得見卻不再回應鍵盤的清單比直接關掉更難理解。
            StaysOpen = false
        };
    }

    /// <summary>在選取範圍的起點旁開一份清單；使用者選定時回呼。</summary>
    /// <remarks>
    /// 回呼而不是回傳選取結果：清單是非模態的，答案要等使用者按鍵之後才有，
    /// 而呼叫端此時已經把選取範圍存成追蹤範圍，等得起。
    /// </remarks>
    /// <param name="selectedIndex">
    /// 預先選起來的那一筆，由 <see cref="SqlSnippetSurroundHistory"/> 決定。
    /// 這裡只負責夾範圍：清單順序是設定檔的順序，而「第一筆」不該因為別的分類
    /// 多了一筆可包夾的片段就換人。
    /// </param>
    public static void Show(
        IWpfTextView view,
        SnapshotPoint anchor,
        IReadOnlyList<SqlSnippet> snippets,
        int selectedIndex,
        Action<SqlSnippet> chosen)
    {
        var picker = new SqlSnippetSurroundPicker(view, snippets, selectedIndex, chosen);
        picker.Open(anchor);
    }

    private void Open(SnapshotPoint anchor)
    {
        var (left, top) = ResolveOffset(anchor);
        _popup.HorizontalOffset = left;
        _popup.VerticalOffset = top;
        _popup.Closed += OnClosed;
        _view.Closed += OnViewClosed;
        _popup.IsOpen = true;

        // 開啟之後才拿得到容器；焦點要落在項目上，方向鍵才由清單處理而不是編輯器。
        _list.UpdateLayout();

        // 預選的那一筆不見得在第一頁：清單超過高度就會捲動，而選在畫面外
        // 等於使用者看不出 Enter 會拿到什麼。
        if (_list.SelectedItem is not null)
        {
            _list.ScrollIntoView(_list.SelectedItem);
            _list.UpdateLayout();
        }

        Keyboard.Focus(_list.SelectedItem as ListBoxItem ?? (IInputElement)_list);
    }

    private ListBox CreateList(
        IReadOnlyList<SqlSnippet> snippets,
        int selectedIndex,
        SqlAssistChrome.Metrics metrics)
    {
        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            MaxHeight = MaximumListHeight,
            ItemContainerStyle = SqlAssistChrome.CreateListItemStyle(metrics),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        foreach (var snippet in snippets)
        {
            var item = new ListBoxItem
            {
                Content = CreateRow(snippet, metrics),
                Tag = snippet,
                ToolTip = string.IsNullOrWhiteSpace(snippet.Description) ? null : snippet.Description
            };

            // 掛在項目上而不是清單上：掛在清單上時，點到清單的空白處也會套用
            // 目前選取的那一筆，而使用者在那裡按下去的意思通常是「先不要」。
            item.MouseLeftButtonUp += OnItemClicked;
            list.Items.Add(item);
        }

        list.SelectedIndex = list.Items.Count == 0
            ? -1
            : Math.Min(Math.Max(selectedIndex, 0), list.Items.Count - 1);
        list.PreviewKeyDown += OnListKeyDown;
        list.PreviewTextInput += OnListTextInput;
        return list;
    }

    /// <summary>一列：捷徑徽章、標題，說明靠右淡色。</summary>
    private static UIElement CreateRow(SqlSnippet snippet, SqlAssistChrome.Metrics metrics)
    {
        var row = new DockPanel { LastChildFill = true };
        var badge = SqlAssistChrome.CreateBadge(snippet.Shortcut, metrics);
        badge.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);

        var description = new TextBlock
        {
            Text = snippet.Description,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = metrics.Caption
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        DockPanel.SetDock(description, Dock.Right);
        row.Children.Add(description);

        row.Children.Add(new TextBlock
        {
            Text = snippet.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = SqlAssistChrome.InterfaceFont,
            FontSize = metrics.Body
        });

        return row;
    }

    /// <summary>把清單放在選取範圍起點的下一行。</summary>
    /// <remarks>
    /// 取不到字元邊界（該行還沒排版、範圍捲出畫面）時退回游標位置：清單少對齊
    /// 幾個像素不影響使用，而為此不開清單才是真的壞掉。
    /// </remarks>
    private (double Left, double Top) ResolveOffset(SnapshotPoint anchor)
    {
        var caret = (Left: _view.Caret.Left, Top: _view.Caret.Bottom);
        var (x, y) = SqlAssistPlatformGuard.Probe(
            "取得包夾清單的錨點座標",
            () =>
            {
                var line = _view.TextViewLines?.GetTextViewLineContainingBufferPosition(anchor);

                return line is null
                    ? caret
                    : (Left: line.GetCharacterBounds(anchor).Left, Top: line.Bottom);
            },
            caret);

        return (x - _view.ViewportLeft, y - _view.ViewportTop);
    }

    private void OnListKeyDown(object sender, KeyEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("處理包夾清單按鍵", () =>
        {
            switch (eventArgs.Key)
            {
                case Key.Escape:
                    eventArgs.Handled = true;
                    Close();
                    break;

                case Key.Enter:
                case Key.Tab:
                    eventArgs.Handled = true;
                    Accept();
                    break;
            }
        });
    }

    /// <summary>輸入捷徑的第一個字就跳到那一筆，與原生清單的行為一致。</summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("處理包夾清單輸入", () =>
        {
            if (eventArgs.Text.Length != 1)
            {
                return;
            }

            // 從目前選取的下一筆開始找，同一個字母連按就在相符的幾筆之間輪替。
            for (var step = 1; step <= _list.Items.Count; step++)
            {
                var index = (_list.SelectedIndex + step) % _list.Items.Count;

                if (_list.Items[index] is ListBoxItem { Tag: SqlSnippet snippet } item &&
                    snippet.Shortcut.StartsWith(eventArgs.Text, StringComparison.OrdinalIgnoreCase))
                {
                    _list.SelectedIndex = index;
                    _list.ScrollIntoView(item);
                    item.Focus();
                    break;
                }
            }

            eventArgs.Handled = true;
        });
    }

    private void OnItemClicked(object sender, MouseButtonEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("處理包夾清單點選", () =>
        {
            if (sender is ListBoxItem item)
            {
                _list.SelectedItem = item;
                eventArgs.Handled = true;
                Accept();
            }
        });
    }

    private void Accept()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: SqlSnippet snippet })
        {
            return;
        }

        // 先關再插入：插入會動到緩衝區與焦點，而還開著的 Popup 會在那期間
        // 收到自己的 StaysOpen 關閉，順序顛倒時就是「插進去了但清單留在畫面上」。
        Close();
        _chosen(snippet);
    }

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _popup.IsOpen = false;
    }

    private void OnViewClosed(object sender, EventArgs eventArgs) => Close();

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _closed = true;
        _popup.Closed -= OnClosed;
        _view.Closed -= OnViewClosed;

        // 焦點在清單上，不還回去的話使用者要自己點一次編輯器才能繼續打字。
        if (!_view.IsClosed)
        {
            _view.VisualElement.Focus();
        }
    }
}
