using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>停駐工具窗的上下主從區；收合保留拖曳比例，不建立第二個預覽視窗。</summary>
internal sealed class SqlMemorySplitView : Grid
{
    private readonly UIElement _detail;
    private readonly GridSplitter _splitter;
    private readonly Button _toggle;
    private GridLength _masterHeight = new(3, GridUnitType.Star);
    private GridLength _detailHeight = new(2, GridUnitType.Star);
    public bool IsDetailExpanded { get; private set; } = true;
    public event EventHandler? DetailExpandedChanged;

    public SqlMemorySplitView(UIElement master, UIElement detail, TextBlock? summary = null)
    {
        _detail = detail;
        RowDefinitions.Add(new RowDefinition { Height = _masterHeight, MinHeight = 80 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = _detailHeight, MinHeight = 100 });
        Children.Add(master);
        var divider = new Grid { Height = 30 };
        SetRow(divider, 1); Children.Add(divider);
        _splitter = new GridSplitter
        {
            Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
            ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            KeyboardIncrement = 16, DragIncrement = 1, Focusable = true, Cursor = Cursors.SizeNS,
            ToolTip = "拖曳調整預覽高度；聚焦後使用 ↑ / ↓。"
        };
        // Splitter 必須是主 Grid 的直接子層，PreviousAndNext 才會調整主從兩列。
        SetRow(_splitter, 1); Children.Add(_splitter);
        var splitterStyle = new Style(typeof(GridSplitter));
        splitterStyle.Setters.Add(ThemeResourceSet.Setter(BackgroundProperty, ThemeBrush.Hairline));
        var focus = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(ThemeResourceSet.Setter(BackgroundProperty, ThemeBrush.AccentBorder));
        splitterStyle.Triggers.Add(focus); _splitter.Style = splitterStyle;
        AutomationProperties.SetName(_splitter, "調整 SQL 預覽高度");
        _toggle = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
        _toggle.Padding = new Thickness(6, 0, 6, 0);
        _toggle.Margin = new Thickness(0, 6, 0, 0);
        _toggle.HorizontalAlignment = HorizontalAlignment.Left;
        _toggle.Click += (_, _) => SetDetailExpanded(!IsDetailExpanded);
        var heading = new DockPanel(); divider.Children.Add(heading);
        DockPanel.SetDock(_toggle, Dock.Left); heading.Children.Add(_toggle);
        if (summary is not null)
        {
            summary.Margin = new Thickness(8, 6, 4, 0);
            summary.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(summary);
        }
        SetRow(detail, 2); Children.Add(detail);
        UpdateToggle();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // 高度也可能很窄；最小值隨可用高度縮小，不讓 Preview 把清單及收合鈕推到視窗外。
        var usable = Math.Max(0, constraint.Height - 30);
        RowDefinitions[0].MinHeight = Math.Min(80, usable * 0.45);
        RowDefinitions[2].MinHeight = IsDetailExpanded ? Math.Min(100, usable * 0.55) : 0;
        return base.MeasureOverride(constraint);
    }

    public void SetDetailExpanded(bool expanded)
    {
        if (expanded == IsDetailExpanded) return;
        if (!expanded)
        {
            _masterHeight = RowDefinitions[0].Height;
            _detailHeight = RowDefinitions[2].Height;
        }
        IsDetailExpanded = expanded;
        RowDefinitions[0].Height = expanded ? _masterHeight : new GridLength(1, GridUnitType.Star);
        RowDefinitions[2].MinHeight = expanded ? 100 : 0;
        RowDefinitions[2].Height = expanded ? _detailHeight : new GridLength(0);
        _detail.Visibility = _splitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateToggle();
        DetailExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateToggle()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = SqlAssistChrome.CreateQueryButtonIcon("Chevron");
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        icon.RenderTransform = new System.Windows.Media.RotateTransform(IsDetailExpanded ? 0 : -90);
        icon.Margin = new Thickness(0, 0, 6, 0); panel.Children.Add(icon);
        panel.Children.Add(SqlAssistChrome.CreateQueryButtonText("Preview")); _toggle.Content = panel;
        _toggle.ToolTip = IsDetailExpanded ? "收合預覽，保留目前選取。" : "展開目前選取的 SQL 預覽。";
        AutomationProperties.SetName(_toggle, IsDetailExpanded ? "收合 Preview" : "展開 Preview");
    }
}
