using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core;
using SqlAssist.Core.Matching;

namespace SqlAssist.Ssms22;

internal sealed class SuggestionPopupControl
{
    private readonly Action _commitSelected;
    private readonly Grid _grid;
    private readonly ListBox _listBox;
    private readonly Popup _popup;
    private readonly TextBlock _previewDescription;
    private readonly ColumnDefinition _previewColumn;
    private readonly DockPanel _previewPanel;
    private readonly TextBox _previewText;
    private readonly TextBlock _previewTitle;
    private readonly Border _separator;
    private readonly ColumnDefinition _separatorColumn;
    private IReadOnlyList<SuggestionMatch> _matches = Array.Empty<SuggestionMatch>();

    public SuggestionPopupControl(IWpfTextView textView, Action commitSelected)
    {
        _commitSelected = commitSelected;
        _listBox = CreateListBox();
        _previewTitle = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 8, 10, 2),
            Foreground = VsThemeBrushes.ListForeground
        };
        _previewDescription = new TextBlock
        {
            Margin = new Thickness(10, 0, 10, 8),
            Foreground = VsThemeBrushes.DimForeground
        };
        _previewText = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(0),
            Background = VsThemeBrushes.ListBackground,
            Foreground = VsThemeBrushes.ListForeground,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            IsTabStop = false
        };

        _previewPanel = new DockPanel
        {
            LastChildFill = true,
            Background = VsThemeBrushes.ListBackground
        };
        DockPanel.SetDock(_previewTitle, Dock.Top);
        DockPanel.SetDock(_previewDescription, Dock.Top);
        _previewPanel.Children.Add(_previewTitle);
        _previewPanel.Children.Add(_previewDescription);
        _previewPanel.Children.Add(_previewText);

        _grid = new Grid
        {
            Width = 820,
            Height = 320,
            Background = VsThemeBrushes.ListBackground
        };
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        _separatorColumn = new ColumnDefinition { Width = new GridLength(1) };
        _previewColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        _grid.ColumnDefinitions.Add(_separatorColumn);
        _grid.ColumnDefinitions.Add(_previewColumn);

        _separator = new Border { Background = VsThemeBrushes.Border };
        Grid.SetColumn(_separator, 1);
        Grid.SetColumn(_previewPanel, 2);
        _grid.Children.Add(_listBox);
        _grid.Children.Add(_separator);
        _grid.Children.Add(_previewPanel);

        var root = new Border
        {
            Child = _grid,
            BorderBrush = VsThemeBrushes.Border,
            BorderThickness = new Thickness(1),
            Background = VsThemeBrushes.ListBackground,
            SnapsToDevicePixels = true,
            Focusable = false
        };

        _popup = new Popup
        {
            AllowsTransparency = false,
            Child = root,
            Focusable = false,
            IsHitTestVisible = true,
            Placement = PlacementMode.Relative,
            PlacementTarget = textView.VisualElement,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true
        };
    }

    /// <summary>選取的項目改變時發出，讓呼叫端有機會非同步補上預覽內容。</summary>
    public event EventHandler? SelectionChanged;

    public bool IsOpen => _popup.IsOpen;

    public SqlSuggestion? SelectedSuggestion
    {
        get
        {
            var index = _listBox.SelectedIndex;
            return index >= 0 && index < _matches.Count ? _matches[index].Suggestion : null;
        }
    }

    public void Show(
        IReadOnlyList<SuggestionMatch> matches,
        IWpfTextView textView,
        bool showPreview)
    {
        SetPreviewVisibility(showPreview);
        var selectedText = SelectedSuggestion?.DisplayText;
        _matches = matches;
        _listBox.ItemsSource = matches.Select(CreateRow).ToArray();

        // 重新篩選之後盡量停在原本選取的項目上，避免選取項在打字時跳來跳去。
        var selectedIndex = selectedText is null
            ? 0
            : IndexOf(matches, selectedText);
        _listBox.SelectedIndex = matches.Count == 0 ? -1 : Math.Max(0, selectedIndex);

        Reposition(textView);
        _popup.IsOpen = matches.Count > 0;
        UpdatePreview();
    }

    private static int IndexOf(IReadOnlyList<SuggestionMatch> matches, string displayText)
    {
        for (var index = 0; index < matches.Count; index++)
        {
            if (string.Equals(
                    matches[index].Suggestion.DisplayText,
                    displayText,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private void SetPreviewVisibility(bool visible)
    {
        _grid.Width = visible ? 820 : 350;
        _separatorColumn.Width = visible ? new GridLength(1) : new GridLength(0);
        _previewColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _separator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _previewPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Hide()
    {
        _popup.IsOpen = false;
    }

    public void MoveSelection(int delta)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var current = Math.Max(0, _listBox.SelectedIndex);
        var next = Math.Max(0, Math.Min(_matches.Count - 1, current + delta));
        _listBox.SelectedIndex = next;
        _listBox.ScrollIntoView(_listBox.SelectedItem);
    }

    public void Reposition(IWpfTextView textView)
    {
        _popup.HorizontalOffset = Math.Max(0, textView.Caret.Left - textView.ViewportLeft);
        _popup.VerticalOffset = Math.Max(
            0,
            textView.Caret.Top - textView.ViewportTop + textView.Caret.Height + 2);
    }

    private ListBox CreateListBox()
    {
        var listBox = new ListBox
        {
            Background = VsThemeBrushes.ListBackground,
            Foreground = VsThemeBrushes.ListForeground,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Focusable = false,
            IsTabStop = false,
            Padding = new Thickness(2),
            SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        listBox.SelectionChanged += (_, _) => UpdatePreview();
        listBox.MouseDoubleClick += OnMouseDoubleClick;
        return listBox;
    }

    /// <summary>
    /// 直接產生每一列的視覺元素，而非透過 DataTemplate：命中字元的粗體區段
    /// 是逐項計算出來的 Inline，用樣板反而要多繞一層附加屬性。
    /// </summary>
    private static UIElement CreateRow(SuggestionMatch match)
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = GetIcon(match.Suggestion.Kind),
            FontWeight = FontWeights.SemiBold,
            Foreground = VsThemeBrushes.DimForeground,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        AppendHighlighted(name, match.Suggestion.DisplayText, match.Spans);
        Grid.SetColumn(name, 1);

        var description = new TextBlock
        {
            Text = match.Suggestion.Description,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = VsThemeBrushes.DimForeground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(description, 2);

        panel.Children.Add(icon);
        panel.Children.Add(name);
        panel.Children.Add(description);
        return panel;
    }

    /// <summary>把命中的字元以粗體呈現，其餘維持一般字重。</summary>
    private static void AppendHighlighted(
        TextBlock target,
        string text,
        IReadOnlyList<MatchSpan> spans)
    {
        var position = 0;

        foreach (var span in spans)
        {
            if (span.Start > text.Length || span.End > text.Length)
            {
                break; // 防禦：區段與文字不一致時退回純文字，不要擲例外中斷清單。
            }

            if (span.Start > position)
            {
                target.Inlines.Add(new Run(text.Substring(position, span.Start - position)));
            }

            target.Inlines.Add(new Run(text.Substring(span.Start, span.Length))
            {
                FontWeight = FontWeights.Bold
            });
            position = span.End;
        }

        if (position < text.Length)
        {
            target.Inlines.Add(new Run(text.Substring(position)));
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (SelectedSuggestion is not null)
        {
            _commitSelected();
            eventArgs.Handled = true;
        }
    }

    private void UpdatePreview()
    {
        var selected = SelectedSuggestion;
        _previewTitle.Text = selected?.DisplayText ?? string.Empty;
        _previewDescription.Text = selected?.Description ?? string.Empty;
        _previewText.Text = selected?.Preview ?? string.Empty;
        _previewText.ScrollToHome();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 補上非同步載入完成的預覽內容。
    /// </summary>
    /// <remarks>
    /// 只有在 <paramref name="suggestion"/> 仍然是目前選取項時才套用：
    /// 使用者在載入期間往下移動選取項時，晚到的結果不可以覆蓋新的選取內容。
    /// </remarks>
    public void TrySetPreviewBody(SqlSuggestion suggestion, string body)
    {
        if (!ReferenceEquals(SelectedSuggestion, suggestion))
        {
            return;
        }

        _previewText.Text = body;
        _previewText.ScrollToHome();
    }

    private static string GetIcon(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Keyword => "K",
            SuggestionKind.Snippet => "S",
            SuggestionKind.Table => "T",
            SuggestionKind.View => "V",
            SuggestionKind.Procedure => "P",
            SuggestionKind.Function => "F",
            SuggestionKind.Schema => "D",
            SuggestionKind.Column => "C",
            _ => "•"
        };
    }
}
