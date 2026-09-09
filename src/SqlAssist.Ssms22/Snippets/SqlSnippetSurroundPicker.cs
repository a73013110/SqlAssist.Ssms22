using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>非模態包夾選擇器；搜尋不碰編輯器，選取範圍由呼叫端追蹤。</summary>
internal sealed class SqlSnippetSurroundPicker
{
    private static readonly object PropertyKey = new();
    private readonly IWpfTextView _view;
    private readonly Popup _popup;
    private readonly SqlSnippetSurroundPanel _panel;
    private readonly Action<SqlSnippet> _chosen;
    private bool _closed;
    private bool _filtering;
    private bool _restoreFocus;

    private SqlSnippetSurroundPicker(IWpfTextView view, IReadOnlyList<SqlSnippet> snippets,
        int selectedIndex, SqlSnippetSurroundSelection selection, Action<SqlSnippet> chosen)
    {
        _view = view;
        _chosen = chosen;
        _panel = new SqlSnippetSurroundPanel(snippets, selectedIndex, selection);
        _panel.SearchBox.TextChanged += OnSearchChanged;
        _panel.List.SelectionChanged += OnSelectionChanged;
        _panel.List.MouseDoubleClick += OnDoubleClick;
        _panel.ApplyButton.Click += OnApply;
        _panel.CancelButton.Click += OnCancel;
        _panel.PreviewKeyDown += OnKeyDown;

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(SqlAssistChrome.InnerRadius + 1),
            Width = 720,
            Height = 440,
            SnapsToDevicePixels = true,
            Child = _panel
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(Border.BorderBrushProperty, ThemeBrush.Border);
        VsThemeBrushes.Apply(frame);
        _popup = new Popup
        {
            Child = frame,
            AllowsTransparency = true,
            Focusable = true,
            PopupAnimation = PopupAnimation.None,
            Placement = PlacementMode.Relative,
            PlacementTarget = view.VisualElement,
            StaysOpen = false
        };
    }

    public static void Show(IWpfTextView view, SnapshotPoint anchor, IReadOnlyList<SqlSnippet> snippets,
        int selectedIndex, SqlSnippetSurroundSelection selection, Action<SqlSnippet> chosen)
    {
        // 每個編輯器只保留一份；重複叫用不留下仍可套用舊範圍的清單。
        if (view.Properties.TryGetProperty(PropertyKey, out SqlSnippetSurroundPicker previous))
        {
            previous.Close(restoreFocus: false);
        }

        var picker = new SqlSnippetSurroundPicker(view, snippets, selectedIndex, selection, chosen);
        view.Properties.AddProperty(PropertyKey, picker);
        try
        {
            picker.Open(anchor);
        }
        finally
        {
            if (!picker._popup.IsOpen)
            {
                picker.OnClosed(null, EventArgs.Empty);
            }
        }
    }

    private void Open(SnapshotPoint anchor)
    {
        var (left, top) = ResolveOffset(anchor);
        // 高 DPI 或較小螢幕也要看得到頁尾；沿用預覽的螢幕／DPI 唯一出處。
        SqlAssistPlatformGuard.Probe("限制包夾清單尺寸", () =>
        {
            var screenPoint = _view.VisualElement.PointToScreen(new Point(left, top));
            if (NativeScreen.TryGetWorkArea(screenPoint) is { } workArea && _popup.Child is FrameworkElement frame)
            {
                var fromDevice = NativeScreen.GetTransformFromDevice(_view.VisualElement);
                frame.Width = Math.Max(1, Math.Min(720, workArea.Width * fromDevice.M11 - 16));
                frame.Height = Math.Max(1, Math.Min(440, workArea.Height * fromDevice.M22 - 16));
            }
        });
        _popup.HorizontalOffset = left;
        _popup.VerticalOffset = top;
        _popup.Closed += OnClosed;
        _view.Closed += OnViewClosed;
        _popup.IsOpen = true;
        NativeScreen.SetNoTopmost(_panel);
        _panel.UpdateLayout();
        if (_panel.List.SelectedItem is not null)
        {
            _panel.List.ScrollIntoView(_panel.List.SelectedItem);
        }

        Keyboard.Focus(_panel.SearchBox);
    }

    private (double Left, double Top) ResolveOffset(SnapshotPoint anchor)
    {
        var caret = (Left: _view.Caret.Left, Top: _view.Caret.Bottom);
        var (x, y) = SqlAssistPlatformGuard.Probe("取得包夾清單的錨點座標", () =>
        {
            var line = _view.TextViewLines?.GetTextViewLineContainingBufferPosition(anchor);
            return line is null ? caret : (Left: line.GetCharacterBounds(anchor).Left, Top: line.Bottom);
        }, caret);
        return (Math.Max(0, x - _view.ViewportLeft), Math.Max(0, y - _view.ViewportTop));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("搜尋包夾片段", () =>
        {
            _filtering = true;
            try
            {
                _panel.Filter(_panel.SearchBox.Text);
            }
            finally
            {
                _filtering = false;
            }
        });
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_filtering)
        {
            SqlAssistPlatformGuard.Run("更新包夾預覽", _panel.UpdatePreview);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("處理包夾清單按鍵", () =>
        {
            if (eventArgs.Key == Key.Escape)
            {
                eventArgs.Handled = true;
                Close(restoreFocus: true);
            }
            else if (eventArgs.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
                     !_panel.CancelButton.IsKeyboardFocusWithin)
            {
                eventArgs.Handled = true;
                Accept();
            }
            else if (_panel.SearchBox.IsKeyboardFocusWithin &&
                     (eventArgs.Key == Key.Down || eventArgs.Key == Key.Up))
            {
                eventArgs.Handled = true;
                var count = _panel.List.Items.Count;
                if (count > 0)
                {
                    _panel.List.SelectedIndex = Math.Min(count - 1,
                        Math.Max(0, _panel.List.SelectedIndex + (eventArgs.Key == Key.Down ? 1 : -1)));
                }
            }
            else if (eventArgs.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                eventArgs.Handled = true;
                _panel.SearchBox.Focus();
                _panel.SearchBox.SelectAll();
            }
        });
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("套用包夾片段", () =>
        {
            // 雙擊捲軸或空白處不是確認；單擊只選取，讓滑鼠使用者也能先看預覽。
            if (eventArgs.ChangedButton == MouseButton.Left && eventArgs.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(_panel.List, source) is ListBoxItem)
            {
                eventArgs.Handled = true;
                Accept();
            }
        });
    }

    private void OnApply(object sender, RoutedEventArgs eventArgs) =>
        SqlAssistPlatformGuard.Run("套用包夾片段", Accept);

    private void OnCancel(object sender, RoutedEventArgs eventArgs) =>
        SqlAssistPlatformGuard.Run("取消包夾", () => Close(restoreFocus: true));

    private void Accept()
    {
        if (_closed || _panel.SelectedSnippet is not { } snippet)
        {
            return;
        }

        // 原生引擎啟動前先關閉 Popup 並還回編輯器焦點。
        Close(restoreFocus: true);
        _chosen(snippet);
    }

    private void Close(bool restoreFocus)
    {
        if (_closed)
        {
            return;
        }

        _restoreFocus = restoreFocus;
        _closed = true;
        RemoveCurrent();
        _popup.IsOpen = false;
    }

    private void OnViewClosed(object sender, EventArgs eventArgs) =>
        SqlAssistPlatformGuard.Run("關閉包夾清單", () => Close(restoreFocus: false));

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("釋放包夾清單", () =>
        {
            _closed = true;
            _popup.Closed -= OnClosed;
            _view.Closed -= OnViewClosed;
            RemoveCurrent();
            // 點到別的查詢頁或工具窗時不搶回焦點；只有明確套用／取消才還給原編輯器。
            if (_restoreFocus && !_view.IsClosed)
            {
                _view.VisualElement.Focus();
            }
        });
    }

    private void RemoveCurrent()
    {
        // Popup.Closed 可能晚於下一份清單開啟，舊事件不能移除新清單的登記。
        if (_view.Properties.TryGetProperty(PropertyKey, out SqlSnippetSurroundPicker current) && ReferenceEquals(current, this))
        {
            _view.Properties.RemoveProperty(PropertyKey);
        }
    }
}
