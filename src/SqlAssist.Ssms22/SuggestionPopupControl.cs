using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core;

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
    private IReadOnlyList<SuggestionRow> _rows = Array.Empty<SuggestionRow>();

    public SuggestionPopupControl(IWpfTextView textView, Action commitSelected)
    {
        _commitSelected = commitSelected;
        _listBox = CreateListBox();
        _previewTitle = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 8, 10, 2),
            Foreground = SystemColors.WindowTextBrush
        };
        _previewDescription = new TextBlock
        {
            Margin = new Thickness(10, 0, 10, 8),
            Foreground = SystemColors.GrayTextBrush
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
            Background = SystemColors.WindowBrush,
            Foreground = SystemColors.WindowTextBrush,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            IsTabStop = false
        };

        _previewPanel = new DockPanel
        {
            LastChildFill = true,
            Background = SystemColors.WindowBrush
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
            Background = SystemColors.WindowBrush
        };
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        _separatorColumn = new ColumnDefinition { Width = new GridLength(1) };
        _previewColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        _grid.ColumnDefinitions.Add(_separatorColumn);
        _grid.ColumnDefinitions.Add(_previewColumn);

        _separator = new Border { Background = SystemColors.ActiveBorderBrush };
        Grid.SetColumn(_separator, 1);
        Grid.SetColumn(_previewPanel, 2);
        _grid.Children.Add(_listBox);
        _grid.Children.Add(_separator);
        _grid.Children.Add(_previewPanel);

        var root = new Border
        {
            Child = _grid,
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            Background = SystemColors.WindowBrush,
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

    public bool IsOpen => _popup.IsOpen;

    public SqlSuggestion? SelectedSuggestion =>
        (_listBox.SelectedItem as SuggestionRow)?.Suggestion;

    public void Show(
        IReadOnlyList<SqlSuggestion> suggestions,
        IWpfTextView textView,
        bool showPreview)
    {
        SetPreviewVisibility(showPreview);
        var selectedText = SelectedSuggestion?.DisplayText;
        _rows = suggestions.Select(item => new SuggestionRow(item)).ToArray();
        _listBox.ItemsSource = _rows;

        var selectedIndex = selectedText is null
            ? 0
            : Math.Max(0, _rows.ToList().FindIndex(row =>
                string.Equals(row.Suggestion.DisplayText, selectedText, StringComparison.OrdinalIgnoreCase)));
        _listBox.SelectedIndex = selectedIndex;

        Reposition(textView);
        _popup.IsOpen = _rows.Count > 0;
        UpdatePreview();
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
        if (_rows.Count == 0)
        {
            return;
        }

        var current = Math.Max(0, _listBox.SelectedIndex);
        var next = Math.Max(0, Math.Min(_rows.Count - 1, current + delta));
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
            Background = SystemColors.WindowBrush,
            BorderThickness = new Thickness(0),
            DisplayMemberPath = nameof(SuggestionRow.DisplayLine),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Focusable = false,
            IsTabStop = false,
            Padding = new Thickness(2),
            SelectionMode = SelectionMode.Single
        };
        listBox.SelectionChanged += (_, _) => UpdatePreview();
        listBox.MouseDoubleClick += OnMouseDoubleClick;
        return listBox;
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
    }

    private sealed class SuggestionRow
    {
        public SuggestionRow(SqlSuggestion suggestion)
        {
            Suggestion = suggestion;
            DisplayLine = $"{GetIcon(suggestion.Kind)}  {suggestion.DisplayText}    {suggestion.Description}";
        }

        public string DisplayLine { get; }

        public SqlSuggestion Suggestion { get; }

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
                _ => "•"
            };
        }
    }
}
