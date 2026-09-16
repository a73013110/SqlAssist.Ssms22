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
    private readonly Grid _divider;
    private GridLength _masterHeight = new(3, GridUnitType.Star);
    private GridLength _detailHeight = new(2, GridUnitType.Star);
    public bool IsDetailExpanded { get; private set; } = true;
    public event EventHandler? DetailExpandedChanged;

    public SqlMemorySplitView(UIElement master, UIElement detail, FrameworkElement? summary = null)
    {
        _detail = detail;
        RowDefinitions.Add(new RowDefinition { Height = _masterHeight, MinHeight = 80 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = _detailHeight, MinHeight = 100 });
        Children.Add(master);
        var divider = _divider = new Grid { MinHeight = 30 };
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
            summary.VerticalAlignment = VerticalAlignment.Center;
            // Hidden 允許水平延伸但不畫捲軸；Disabled 會限制內容寬度，無法捲動。
            var metadata = new ScrollViewer
            {
                Content = summary, Margin = new Thickness(8, 6, 4, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false, PanningMode = PanningMode.HorizontalOnly,
                Focusable = true, Background = System.Windows.Media.Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "在資訊列上使用滑鼠滾輪左右捲動；聚焦後可用 ← / →、Home / End。"
            };
            AutomationProperties.SetName(metadata, "預覽資訊（可水平捲動）");
            metadata.PreviewMouseWheel += (_, args) =>
            {
                if (args.Delta == 0) return;
                // 只攔截資訊列內的滾輪；向下往右、向上往左，不帶動 Preview 或 SQL 本文。
                metadata.ScrollToHorizontalOffset(metadata.HorizontalOffset - args.Delta * 0.4);
                args.Handled = true;
            };
            metadata.PreviewKeyDown += (_, args) =>
            {
                if (args.KeyboardDevice.Modifiers != ModifierKeys.None) return;
                switch (args.Key)
                {
                    case Key.Left: metadata.LineLeft(); break;
                    case Key.Right: metadata.LineRight(); break;
                    case Key.Home: metadata.ScrollToLeftEnd(); break;
                    case Key.End: metadata.ScrollToRightEnd(); break;
                    default: return;
                }
                args.Handled = true;
            };
            heading.Children.Add(metadata);
        }
        SetRow(detail, 2); Children.Add(detail);
        UpdateToggle();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // 高度也可能很窄；最小值隨可用高度縮小，不讓 Preview 把清單及收合鈕推到視窗外。
        // 使用實際標頭高度，讓字級與 DPI 變動後仍保留清單及收合鈕的空間。
        _divider.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var usable = Math.Max(0, constraint.Height - _divider.DesiredSize.Height);
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
        var chevron = SqlAssistChrome.CreateChevron(IsDetailExpanded);
        chevron.Margin = new Thickness(0, 0, 6, 0); panel.Children.Add(chevron);
        panel.Children.Add(SqlAssistChrome.CreateMemoryButtonText("預覽")); _toggle.Content = panel;
        _toggle.ToolTip = IsDetailExpanded ? "收合預覽，保留目前選取。" : "展開目前選取的 SQL 預覽。";
        AutomationProperties.SetName(_toggle, IsDetailExpanded ? "收合預覽" : "展開預覽");
    }
}
