using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>停駐工具窗的主從區；收合保留拖曳比例，不建立第二個預覽視窗。</summary>
/// <remarks>
/// 預設是上下分割。工具窗停在右側時窄、停在下方時寬，而寬版面下上下分割會讓清單只剩幾列高，
/// 旁邊卻空著一半；<see cref="SqlMemorySplitView(UIElement, UIElement, FrameworkElement?, double?)"/>
/// 的 <c>sideBySideWidth</c> 開啟自動轉向，<b>不傳就完全維持原行為</b>——SQL Memory 的
/// 上下分割是既有驗收過的版面，不由這一次的搜尋版面順手改掉。
/// </remarks>
internal sealed class SqlMemorySplitView : Grid
{
    private readonly UIElement _master;
    private readonly UIElement _detail;
    private readonly GridSplitter _splitter;
    private readonly Button _toggle;
    private readonly Grid _divider;
    private readonly double? _sideBySideWidth;

    /// <summary>分隔線的厚度；兩個方向共用同一個數字，握把不因轉向變粗變細。</summary>
    private const double SplitterThickness = 5;

    /// <summary>左右分割時任一邊的最小寬度；比這窄的清單一列都讀不完。</summary>
    private const double MinPaneWidth = 220;

    /// <summary>上下分割時的兩段比例；轉向後換回來仍是使用者拖過的那一份。</summary>
    private GridLength _masterHeight = new(3, GridUnitType.Star);
    private GridLength _detailHeight = new(2, GridUnitType.Star);

    /// <summary>左右分割時的兩段比例；與上下那一份分開記，換向不會把另一邊的拖曳結果洗掉。</summary>
    private GridLength _masterWidth = new(3, GridUnitType.Star);
    private GridLength _detailWidth = new(2, GridUnitType.Star);

    public bool IsDetailExpanded { get; private set; } = true;
    public event EventHandler? DetailExpandedChanged;

    /// <summary>目前是不是左右分割；版面回歸測試以它驗門檻。</summary>
    public bool IsSideBySide { get; private set; }

    /// <summary>轉向了；宿主據此更新自動化名稱之類跟著方向走的東西。</summary>
    public event EventHandler? OrientationChanged;

    /// <param name="sideBySideWidth">
    /// 寬到這個 DIP 就自動轉成左右分割；null 表示永遠上下分割（SQL Memory 的既有行為）。
    /// </param>
    public SqlMemorySplitView(UIElement master, UIElement detail, FrameworkElement? summary = null,
        double? sideBySideWidth = null)
    {
        if (sideBySideWidth is <= 0) throw new ArgumentOutOfRangeException(nameof(sideBySideWidth));

        _master = master;
        _detail = detail;
        _sideBySideWidth = sideBySideWidth;
        RowDefinitions.Add(new RowDefinition { Height = _masterHeight, MinHeight = 80 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = _detailHeight, MinHeight = 100 });
        ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(master);
        var divider = _divider = new Grid { MinHeight = 30 };
        SetRow(divider, 1); Children.Add(divider);
        _splitter = new GridSplitter
        {
            Height = SplitterThickness, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
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
        // 轉向要在量測時決定：等到排版才換，這一輪已經照舊方向量過一次，畫面會閃一下舊版面。
        if (_sideBySideWidth is { } threshold && !double.IsInfinity(constraint.Width))
        {
            SetSideBySide(constraint.Width >= threshold);
        }

        if (IsSideBySide)
        {
            // 與上下分割同一條規則，只是換成寬度：最小值隨可用寬度縮小，不讓 Preview 把清單推到視窗外。
            var usableWidth = Math.Max(0, constraint.Width - SplitterThickness);
            ColumnDefinitions[0].MinWidth = Math.Min(MinPaneWidth, usableWidth * 0.45);
            ColumnDefinitions[2].MinWidth = IsDetailExpanded ? Math.Min(MinPaneWidth, usableWidth * 0.55) : 0;
            return base.MeasureOverride(constraint);
        }

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
        if (!expanded) RememberProportions();
        IsDetailExpanded = expanded;
        ApplyProportions();
        _detail.Visibility = _splitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateToggle();
        DetailExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <remarks>
    /// 兩個方向的比例分開記，而且轉向前先把目前這一份收回來：不收的話，使用者在上下分割拖過的
    /// 比例會在轉去左右再轉回來之後回到預設值，而他沒有動過任何東西。
    /// </remarks>
    private void SetSideBySide(bool sideBySide)
    {
        if (sideBySide == IsSideBySide) return;

        RememberProportions();
        IsSideBySide = sideBySide;

        RowDefinitions.Clear();
        ColumnDefinitions.Clear();

        if (sideBySide)
        {
            // 抬頭移到兩塊內容上方並橫跨整列。留在中間那一欄的話，收合鈕與資訊列會被擠進
            // 一條 5 DIP 寬的分隔欄裡；它們是整個主從區的抬頭，本來就不屬於分隔線。
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = _masterWidth, MinWidth = MinPaneWidth });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = _detailWidth, MinWidth = MinPaneWidth });

            SetRow(_divider, 0); SetColumn(_divider, 0); SetColumnSpan(_divider, 3);
            SetRow(_master, 1); SetColumn(_master, 0); SetColumnSpan(_master, 1);
            SetRow(_splitter, 1); SetColumn(_splitter, 1); SetColumnSpan(_splitter, 1);
            SetRow(_detail, 1); SetColumn(_detail, 2); SetColumnSpan(_detail, 1);

            _splitter.Height = double.NaN;
            _splitter.Width = SplitterThickness;
            _splitter.HorizontalAlignment = HorizontalAlignment.Center;
            _splitter.VerticalAlignment = VerticalAlignment.Stretch;
            _splitter.ResizeDirection = GridResizeDirection.Columns;
            _splitter.Cursor = Cursors.SizeWE;
            _splitter.ToolTip = "拖曳調整預覽寬度；聚焦後使用 ← / →。";
            AutomationProperties.SetName(_splitter, "調整 SQL 預覽寬度");
        }
        else
        {
            RowDefinitions.Add(new RowDefinition { Height = _masterHeight, MinHeight = 80 });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = _detailHeight, MinHeight = 100 });
            ColumnDefinitions.Add(new ColumnDefinition());

            SetRow(_master, 0); SetColumn(_master, 0); SetColumnSpan(_master, 1);
            SetRow(_divider, 1); SetColumn(_divider, 0); SetColumnSpan(_divider, 1);
            SetRow(_splitter, 1); SetColumn(_splitter, 0); SetColumnSpan(_splitter, 1);
            SetRow(_detail, 2); SetColumn(_detail, 0); SetColumnSpan(_detail, 1);

            _splitter.Width = double.NaN;
            _splitter.Height = SplitterThickness;
            _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _splitter.VerticalAlignment = VerticalAlignment.Top;
            _splitter.ResizeDirection = GridResizeDirection.Rows;
            _splitter.Cursor = Cursors.SizeNS;
            _splitter.ToolTip = "拖曳調整預覽高度；聚焦後使用 ↑ / ↓。";
            AutomationProperties.SetName(_splitter, "調整 SQL 預覽高度");
        }

        ApplyProportions();
        UpdateToggle();
        OrientationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RememberProportions()
    {
        if (!IsDetailExpanded) return;

        if (IsSideBySide)
        {
            _masterWidth = ColumnDefinitions[0].Width;
            _detailWidth = ColumnDefinitions[2].Width;
            return;
        }

        _masterHeight = RowDefinitions[0].Height;
        _detailHeight = RowDefinitions[2].Height;
    }

    private void ApplyProportions()
    {
        if (IsSideBySide)
        {
            ColumnDefinitions[0].Width = IsDetailExpanded ? _masterWidth : new GridLength(1, GridUnitType.Star);
            ColumnDefinitions[2].MinWidth = IsDetailExpanded ? MinPaneWidth : 0;
            ColumnDefinitions[2].Width = IsDetailExpanded ? _detailWidth : new GridLength(0);
            return;
        }

        RowDefinitions[0].Height = IsDetailExpanded ? _masterHeight : new GridLength(1, GridUnitType.Star);
        RowDefinitions[2].MinHeight = IsDetailExpanded ? 100 : 0;
        RowDefinitions[2].Height = IsDetailExpanded ? _detailHeight : new GridLength(0);
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
