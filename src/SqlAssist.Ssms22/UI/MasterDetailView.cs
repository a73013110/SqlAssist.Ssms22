using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>停駐工具窗的主從區；轉向、收合與兩個方向的拖曳比例都在這裡，不建立第二個預覽視窗。</summary>
/// <remarks>
/// 工具窗停在右側時窄、停在下方時寬；寬版面下上下分割會讓清單只剩幾列高，旁邊卻空著一半，
/// 所以寬到門檻就轉成左右。轉向在 <see cref="MeasureOverride"/> 決定，等到排版才換會閃一次舊版面。
/// </remarks>
internal sealed class MasterDetailView : Grid
{
    /// <summary>轉成左右分割的預設門檻；兩塊各 <see cref="MinPaneWidth"/> 加上分隔線與外距。</summary>
    /// <remarks>
    /// 再低的話，轉向之後兩邊都窄到讀不完一個限定名稱，而使用者只是把面板拉寬了一點。
    /// </remarks>
    public const double DefaultSideBySideWidth = 520;

    private readonly UIElement _master;
    private readonly UIElement _detail;
    private readonly GridSplitter _splitter;
    private readonly Button _toggle;
    private readonly Grid _divider;
    private readonly FrameworkElement? _summaryHost;
    private readonly double? _sideBySideWidth;
    private readonly bool? _motion;

    /// <summary>抬頭上那一顆箭頭；跨兩次 <see cref="UpdateHeading"/> 留著同一顆才轉得動。</summary>
    private readonly System.Windows.Shapes.Path _chevron;

    /// <summary>分隔線的厚度；兩個方向共用同一個數字，握把不因轉向變粗變細。</summary>
    private const double SplitterThickness = 5;

    /// <summary>左右分割時任一邊的最小寬度；比這窄的清單一列都讀不完。</summary>
    private const double MinPaneWidth = 220;

    /// <summary>轉回上下要比轉去左右再窄這麼多 DIP。</summary>
    /// <remarks>
    /// 只有單一門檻的話，臨界寬度每量測一次就翻一次版面：使用者拖視窗邊框時清單與 Preview
    /// 反覆對調，選取與捲動看起來像在跳。留一段只進不出的區間，拖回來要真的窄回去才換。
    /// </remarks>
    private const double OrientationHysteresis = 32;

    /// <summary>上下分割時的兩段比例；轉向後換回來仍是使用者拖過的那一份。</summary>
    /// <remarks>清單多分一點：上下分割時 Preview 吃的是整個寬度，矮一點仍讀得完一行 SQL。</remarks>
    private GridLength _masterHeight = new(3, GridUnitType.Star);
    private GridLength _detailHeight = new(2, GridUnitType.Star);

    /// <summary>
    /// 左右分割時的兩段比例；與上下那一份分開記，換向不會把另一邊的拖曳結果洗掉。
    /// </summary>
    /// <remarks>
    /// 與上下那一份<b>相反</b>，Preview 分得比較多。清單列的寬度有上界——名稱截在
    /// <see cref="SqlAssistChrome.RowNameMaxWidth"/>，膠囊與限定名稱都是固定寬或可省略的，
    /// 再寬只是右邊一直空著；而 Preview 裡的 SQL 沒有上界，窄一點就是每一行都折或都要橫捲。
    /// 兩邊都給 <c>3:2</c> 的那一版在左右分割下把多出來的空間全給了不需要它的那一欄。
    /// </remarks>
    private GridLength _masterWidth = new(2, GridUnitType.Star);
    private GridLength _detailWidth = new(3, GridUnitType.Star);

    public bool IsDetailExpanded { get; private set; } = true;
    public event EventHandler? DetailExpandedChanged;

    /// <summary>目前是不是左右分割；版面回歸測試以它驗門檻。</summary>
    public bool IsSideBySide { get; private set; }

    /// <summary>轉向了；宿主據此更新自動化名稱之類跟著方向走的東西。</summary>
    public event EventHandler? OrientationChanged;

    /// <param name="sideBySideWidth">
    /// 寬到這個 DIP 就自動轉成左右分割；null 表示永遠上下分割。
    /// </param>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public MasterDetailView(UIElement master, UIElement detail, FrameworkElement? summary = null,
        double? sideBySideWidth = null, bool? motion = null)
    {
        if (sideBySideWidth is <= 0) throw new ArgumentOutOfRangeException(nameof(sideBySideWidth));

        _master = master;
        _detail = detail;
        _sideBySideWidth = sideBySideWidth;
        _motion = motion;
        _chevron = SqlAssistChrome.CreateChevron(IsDetailExpanded);
        Children.Add(master);
        var divider = _divider = new Grid { MinHeight = 30 };
        Children.Add(divider);
        _splitter = new GridSplitter
        {
            Height = SplitterThickness, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
            ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            KeyboardIncrement = 16, DragIncrement = 1, Focusable = true, Cursor = Cursors.SizeNS,
            ToolTip = "拖曳調整預覽高度；聚焦後使用 ↑ / ↓。"
        };
        // Splitter 必須是主 Grid 的直接子層，PreviousAndNext 才會調整主從兩列。
        Children.Add(_splitter);
        var splitterStyle = new Style(typeof(GridSplitter));
        splitterStyle.Setters.Add(ThemeResourceSet.Setter(BackgroundProperty, ThemeBrush.Hairline));
        var focus = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(ThemeResourceSet.Setter(BackgroundProperty, ThemeBrush.AccentBorder));
        splitterStyle.Triggers.Add(focus); _splitter.Style = splitterStyle;
        AutomationProperties.SetName(_splitter, "調整 SQL 預覽高度");
        _toggle = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
        _toggle.Padding = new Thickness(6, 0, 6, 0);
        _toggle.Margin = new Thickness(0, SqlAssistChrome.Spacing.Tight, 0, 0);
        _toggle.HorizontalAlignment = HorizontalAlignment.Left;
        _toggle.Click += (_, _) => SetDetailExpanded(!IsDetailExpanded);
        var heading = new DockPanel(); divider.Children.Add(heading);
        DockPanel.SetDock(_toggle, Dock.Left); heading.Children.Add(_toggle);
        if (summary is not null)
        {
            // 捲動、滾輪方向與鍵盤都走共用的單列資訊列；已選條件列用的是同一份。
            var metadata = SqlAssistChrome.CreateHorizontalStrip(summary, "預覽資訊（可水平捲動）");
            metadata.Margin = new Thickness(
                SqlAssistChrome.Spacing.Group, SqlAssistChrome.Spacing.Tight,
                SqlAssistChrome.Spacing.Tight, 0);
            heading.Children.Add(metadata);
            _summaryHost = metadata;
        }
        Children.Add(detail);
        ApplyLayout();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // 轉向要在量測時決定：等到排版才換，這一輪已經照舊方向量過一次，畫面會閃一下舊版面。
        if (_sideBySideWidth is { } threshold && !double.IsInfinity(constraint.Width))
        {
            // 轉去左右看門檻，轉回上下要再窄一段 hysteresis；臨界寬度上只換一次，不隨量測反覆跳。
            var pivot = IsSideBySide ? threshold - OrientationHysteresis : threshold;
            SetSideBySide(constraint.Width >= pivot);
        }

        if (IsSideBySide)
        {
            // 與上下分割同一條規則，只是換成寬度：最小值隨可用寬度縮小，不讓 Preview 把清單推到視窗外。
            // 收合後清單獨占整列，最小寬度讓給右緣把手，否則窄窗會把把手推出視窗。
            var usableWidth = Math.Max(0, constraint.Width - SplitterThickness);
            ColumnDefinitions[0].MinWidth = IsDetailExpanded ? Math.Min(MinPaneWidth, usableWidth * 0.45) : 0;
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
        _detail.Visibility = _splitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ApplyLayout();
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
        ApplyLayout();
        OrientationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>照目前方向與收合狀態重排格線；兩者都會換掉抬頭的位置，不是只換比例。</summary>
    private void ApplyLayout()
    {
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();

        if (IsSideBySide) ApplySideBySide();
        else ApplyStacked();

        UpdateHeading();
    }

    private void ApplySideBySide()
    {
        // 抬頭只屬於右邊那一欄。橫跨整列的話，開關會落在清單左上角——使用者在清單上方看到一顆
        // 管右邊那一塊的按鈕。也不能放進中間那一欄：那裡只有 5 DIP 寬。
        // 收合時右欄縮成 Auto，抬頭就是貼在清單右緣的單列把手。
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = IsDetailExpanded ? _masterWidth : new GridLength(1, GridUnitType.Star),
            MinWidth = IsDetailExpanded ? MinPaneWidth : 0
        });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = IsDetailExpanded ? _detailWidth : GridLength.Auto,
            MinWidth = IsDetailExpanded ? MinPaneWidth : 0
        });

        SetRow(_divider, 0); SetColumn(_divider, 2); SetColumnSpan(_divider, 1);
        // 抬頭那一列是 Auto，兩態都只有一列高；清單與分隔線跨過它，左邊不留一條空白。
        _divider.VerticalAlignment = VerticalAlignment.Top;
        SetRow(_master, 0); SetColumn(_master, 0); SetColumnSpan(_master, 1); SetRowSpan(_master, 2);
        SetRow(_splitter, 0); SetColumn(_splitter, 1); SetColumnSpan(_splitter, 1); SetRowSpan(_splitter, 2);
        SetRow(_detail, 1); SetColumn(_detail, 2); SetColumnSpan(_detail, 1); SetRowSpan(_detail, 1);

        _splitter.Height = double.NaN;
        _splitter.Width = SplitterThickness;
        _splitter.HorizontalAlignment = HorizontalAlignment.Center;
        _splitter.VerticalAlignment = VerticalAlignment.Stretch;
        _splitter.ResizeDirection = GridResizeDirection.Columns;
        _splitter.Cursor = Cursors.SizeWE;
        _splitter.ToolTip = "拖曳調整預覽寬度；聚焦後使用 ← / →。";
        AutomationProperties.SetName(_splitter, "調整 SQL 預覽寬度");
    }

    private void ApplyStacked()
    {
        // 收合時 Preview 那一列歸零，抬頭就是貼在清單下緣的把手列。
        RowDefinitions.Add(new RowDefinition
        {
            Height = IsDetailExpanded ? _masterHeight : new GridLength(1, GridUnitType.Star), MinHeight = 80
        });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition
        {
            Height = IsDetailExpanded ? _detailHeight : new GridLength(0), MinHeight = IsDetailExpanded ? 100 : 0
        });
        ColumnDefinitions.Add(new ColumnDefinition());

        SetRow(_master, 0); SetColumn(_master, 0); SetColumnSpan(_master, 1); SetRowSpan(_master, 1);
        SetRow(_divider, 1); SetColumn(_divider, 0); SetColumnSpan(_divider, 1);
        _divider.VerticalAlignment = VerticalAlignment.Stretch;
        SetRow(_splitter, 1); SetColumn(_splitter, 0); SetColumnSpan(_splitter, 1); SetRowSpan(_splitter, 1);
        SetRow(_detail, 2); SetColumn(_detail, 0); SetColumnSpan(_detail, 1); SetRowSpan(_detail, 1);

        _splitter.Width = double.NaN;
        _splitter.Height = SplitterThickness;
        _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        _splitter.VerticalAlignment = VerticalAlignment.Top;
        _splitter.ResizeDirection = GridResizeDirection.Rows;
        _splitter.Cursor = Cursors.SizeNS;
        _splitter.ToolTip = "拖曳調整預覽高度；聚焦後使用 ↑ / ↓。";
        AutomationProperties.SetName(_splitter, "調整 SQL 預覽高度");
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

    /// <summary>
    /// 收合後抬頭退化成單列把手：摘要講的是 Preview 的內容，跟著 Preview 一起收；開關必須留下來，
    /// 一起收掉就再也展不開。右緣那一條只有一欄寬，文字放不進去，只留 chevron 與它的 ToolTip。
    /// </summary>
    private void UpdateHeading()
    {
        if (_summaryHost is not null)
            _summaryHost.Visibility = IsDetailExpanded ? Visibility.Visible : Visibility.Collapsed;

        var iconOnly = !IsDetailExpanded && IsSideBySide;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // 同一顆箭頭搬到新的抬頭裡再轉：每次重建一顆新的話，它一出場就已經指著最終方向，
        // 而收合與展開這個狀態轉換就再也看不出是同一件事。
        if (_chevron.Parent is StackPanel previous) previous.Children.Remove(_chevron);
        _chevron.Margin = iconOnly ? default : new Thickness(0, 0, 6, 0);
        panel.Children.Add(_chevron);
        SqlAssistChrome.SetChevronExpanded(_chevron, IsDetailExpanded, _motion);
        if (!iconOnly) panel.Children.Add(SqlAssistChrome.CreateButtonText("預覽"));
        _toggle.Content = panel;
        _toggle.ToolTip = IsDetailExpanded ? "收合預覽，保留目前選取。" : "展開目前選取的 SQL 預覽。";
        AutomationProperties.SetName(_toggle, IsDetailExpanded ? "收合預覽" : "展開預覽");
    }
}
