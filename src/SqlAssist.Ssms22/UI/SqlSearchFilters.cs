using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 比對位置的三段開關；常駐在工具列上，對應 <see cref="SearchQuery.Targets"/>。
/// </summary>
/// <remarks>
/// 做成分段開關而不是下拉：這是使用者切換最頻繁的一項，藏進下拉會讓每一次切換多兩次點擊。
/// 三段可以同時亮，因為 <see cref="SearchTargets"/> 本來就是旗標；<b>但不能全部關掉</b>——
/// 一個部位都不掃的查詢找不到任何東西，而畫面上與「這個字串不存在」一模一樣。
/// 最後一段按下去時維持原樣，不送出變更。
/// </remarks>
internal sealed class SqlSearchSegments : Border
{
    private readonly List<(SearchMatchTarget Target, ToggleButton Button)> _segments = new();
    private SearchTargets _value = SearchTargets.All;
    private bool _updating;

    public SqlSearchSegments()
    {
        SetResourceReference(BackgroundProperty, ThemeBrush.SegmentTrack);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;

        var track = new StackPanel { Orientation = Orientation.Horizontal };
        Child = track;

        foreach (var target in SqlSearchTargets.Order)
        {
            var label = SqlSearchTargets.LabelFor(target);
            var segment = new ToggleButton
            {
                Content = SqlAssistChrome.CreateMemoryButtonText(label),
                Style = SqlAssistChrome.CreateSegmentToggleStyle(),
                IsChecked = true,
                ToolTip = label + "：" + SqlSearchTargets.DescriptionFor(target)
            };
            AutomationProperties.SetName(segment, "比對位置：" + label);
            var flag = target.ToFlag();
            segment.Checked += (_, _) => Toggle(flag, on: true);
            segment.Unchecked += (_, _) => Toggle(flag, on: false);
            _segments.Add((target, segment));
            track.Children.Add(segment);
        }

        AutomationProperties.SetName(this, "比對位置");
    }

    public event EventHandler? ValueChanged;

    /// <summary>目前亮著的幾段；永遠至少一段。</summary>
    public SearchTargets Value
    {
        get => _value;
        set
        {
            if (value == SearchTargets.None || (value & ~SearchTargets.All) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "至少要亮一段，也不接受認不得的位元。");
            }

            if (value == _value) return;
            _value = value;
            Refresh();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Toggle(SearchTargets flag, bool on)
    {
        if (_updating) return;

        var next = on ? _value | flag : _value & ~flag;
        if (next == _value) return;

        // 最後一段關不掉。按鈕已經彈起來了，所以要把它按回去——不還原的話，畫面上三段全暗，
        // 而實際上仍在比對那一段。
        if (next == SearchTargets.None)
        {
            Refresh();
            return;
        }

        _value = next;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            foreach (var (target, button) in _segments) button.IsChecked = (_value & target.ToFlag()) != 0;
        }
        finally
        {
            _updating = false;
        }
    }
}

/// <summary>
/// 工具列上的多選過濾按鈕：按鈕顯示摘要，選項在彈出面板裡。
/// </summary>
/// <remarks>
/// 十幾種物件攤成 pill 會佔掉兩列，在停靠面板裡等於少看四筆結果；摘要留在按鈕上，
/// 完整名單留在面板與 chip 列。用 <see cref="Popup"/> 而不是 <see cref="ContextMenu"/>，
/// 是因為資料庫那一份面板裡有搜尋框與兩顆命令鈕——快捷選單裡的輸入欄拿不到鍵盤焦點。
///
/// <see cref="PopupSurface"/> 要由宿主接上動態資源。Popup 的內容不在宿主的視覺樹上，
/// 沒有這一道就會在深色主題露出白底；這裡不自己做，是為了讓這個控制項留在純 WPF，
/// 主題回歸測試才編得進去。
/// </remarks>
internal sealed class SqlSearchFilterButton : Button
{
    private readonly TextBlock _label = SqlAssistChrome.CreateMemoryButtonText("");
    private readonly TextBlock _summary = SqlAssistChrome.CreateMemoryButtonText("");
    private readonly ItemsControl _options = SqlAssistChrome.CreateSearchOptionList(OptionsHeight);
    private readonly TextBox? _filter;
    private IReadOnlyList<SqlSearchFilterGroup> _groups = Array.Empty<SqlSearchFilterGroup>();
    private readonly Popup _popup;
    private readonly string _name;
    private bool _compact;

    /// <summary>選項區的高度上限；捲的是選項本身，搜尋框與兩顆命令鈕要一直看得見。</summary>
    private const double OptionsHeight = 280;

    /// <param name="filterable">面板上要不要有搜尋框；名稱可能上百個的清單才需要。</param>
    public SqlSearchFilterButton(string name, SqlIcon icon, bool filterable = false)
    {
        _name = name;
        Style = SqlAssistChrome.CreateFilterButtonStyle();
        Template = SqlAssistChrome.CreateGhostButtonTemplate();
        Padding = new Thickness(6, 2, 6, 2);
        _label.Text = name + ": ";

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = SqlAssistChrome.CreateIcon(icon);
        glyph.Margin = new Thickness(0, 0, 5, 0);
        content.Children.Add(glyph);
        content.Children.Add(_label);
        content.Children.Add(_summary);
        var chevron = SqlAssistChrome.CreateChevron(expanded: false);
        chevron.Margin = new Thickness(3, 0, 0, 0);
        content.Children.Add(chevron);
        Content = content;

        var panel = new StackPanel();

        if (filterable)
        {
            _filter = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
            var clear = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除" + name + "篩選字");
            clear.Click += (_, _) => { _filter.Clear(); _filter.Focus(); };
            AutomationProperties.SetName(_filter, "篩選" + name + "名稱");
            var bar = SqlAssistChrome.CreateInputBar(SqlIcon.Search, _filter, clear);
            bar.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(bar);
            _filter.TextChanged += (_, _) => ApplyFilter();
        }

        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        commands.Children.Add(CreateCommand(SqlIcon.SelectAll, "全選", () => SelectAllRequested?.Invoke(this, EventArgs.Empty)));
        commands.Children.Add(CreateCommand(SqlIcon.Clear, "清除", () => ClearRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(commands);

        panel.Children.Add(_options);

        var surface = SqlAssistChrome.CreateSurface(panel);
        surface.Padding = new Thickness(8);
        surface.MinWidth = 220;
        surface.MaxWidth = 320;
        PopupSurface = surface;

        _popup = new Popup
        {
            Child = surface,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            HorizontalOffset = 0,
            VerticalOffset = 2
        };

        Click += (_, _) => Open();
        _popup.Opened += (_, _) =>
        {
            SqlAssistChrome.PlayAppear(surface);
            _filter?.Focus();
        };
        // Esc 關面板並把焦點還給按鈕；面板還開著時按 Esc 不該收掉整個工具窗的搜尋。
        _popup.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            _popup.IsOpen = false;
            Focus();
            args.Handled = true;
        };

        UpdateSummary("", "");
    }

    /// <summary>彈出面板的根節點；宿主必須對它套一次主題資源，Popup 不在宿主的視覺樹上。</summary>
    public FrameworkElement PopupSurface { get; }

    /// <summary>面板要開了；宿主在這時候才去填選項，不為了一個下拉先連一次資料庫。</summary>
    public event EventHandler? OptionsRequested;

    public event EventHandler? SelectAllRequested;

    public event EventHandler? ClearRequested;

    /// <summary>窄窗只留圖示與箭頭；名稱與摘要留在 Tooltip 與 chip 列。</summary>
    public bool IsCompact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            var visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _label.Visibility = visibility;
            _summary.Visibility = visibility;
        }
    }

    /// <summary>按鈕上的摘要與完整的 Tooltip。</summary>
    public void UpdateSummary(string summary, string detail)
    {
        _summary.Text = summary;
        var full = _name + ": " + (summary.Length == 0 ? "" : summary);
        ToolTip = detail.Length == 0 ? full : full + "\n" + detail;
        AutomationProperties.SetName(this, full);
    }

    /// <summary>換一整份選項；面板開著時呼叫也不會關掉它。</summary>
    /// <param name="groups">每一段的標題與內容；標題空字串表示不分段。</param>
    public void SetOptions(IReadOnlyList<SqlSearchFilterGroup> groups)
    {
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        ApplyFilter();
    }

    private void Open()
    {
        OptionsRequested?.Invoke(this, EventArgs.Empty);
        _popup.IsOpen = true;
    }

    private Button CreateCommand(SqlIcon icon, string label, Action run)
    {
        var button = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
        button.Content = SqlAssistChrome.CreateMemoryLabel(icon, label);
        button.Padding = new Thickness(6, 2, 6, 2);
        button.Margin = new Thickness(0, 0, 4, 0);
        button.Click += (_, _) => run();
        AutomationProperties.SetName(button, label + _name);
        return button;
    }

    /// <summary>
    /// 依搜尋字重排列清單；一段裡一個都不相符時，連那一段的標題也不畫。
    /// </summary>
    /// <remarks>
    /// 虛擬化之後不能再靠 <see cref="Visibility"/> 收起不相符的選項——收起來的那幾列仍然要
    /// 先建出來，而那正是這個面板要避開的事。換清單不會搶走鍵盤焦點：會走到這裡的只有搜尋框的
    /// <c>TextChanged</c> 與宿主重填選項，兩者發生時焦點都不在選項上。
    /// </remarks>
    private void ApplyFilter()
    {
        var pattern = _filter?.Text ?? "";
        var rows = new List<SqlSearchFilterRow>();

        foreach (var group in _groups)
        {
            var start = rows.Count;

            foreach (var item in group.Items)
            {
                if (pattern.Length != 0 && item.Label.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(SqlSearchFilterRow.Option(item));
            }

            if (rows.Count == start) continue;
            if (group.Title.Length != 0) rows.Insert(start, SqlSearchFilterRow.Caption(group.Title, first: start == 0));
        }

        _options.ItemsSource = rows;
    }
}

/// <summary>過濾面板裡的一段：標題加上它底下的選項。</summary>
internal sealed class SqlSearchFilterGroup
{
    internal SqlSearchFilterGroup(string title, IReadOnlyList<SqlSearchFilterOption> items)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public string Title { get; }

    public IReadOnlyList<SqlSearchFilterOption> Items { get; }
}

/// <summary>
/// 攤平後的一列：一段的標題，或一個勾選項。
/// </summary>
/// <remarks>
/// 虛擬化要的是一份平的清單，所以標題與選項同型。狀態留在這裡而不是留在
/// <see cref="SqlSearchFilterOption"/>：後者是宿主每次重填時新建的一份契約，
/// 這一列才是繫結寫得回去的那一端。
/// </remarks>
internal sealed class SqlSearchFilterRow : INotifyPropertyChanged
{
    private readonly Action<bool>? _selected;
    private bool _isSelected;

    private SqlSearchFilterRow(string label, string toolTip, Thickness margin, bool isCaption, bool isSelected, Action<bool>? selected)
    {
        Label = label;
        ToolTip = toolTip;
        Margin = margin;
        IsCaption = isCaption;
        _isSelected = isSelected;
        _selected = selected;
    }

    /// <param name="first">整份清單的第一列不留上緣間距，否則面板頂端會多出一條空白。</param>
    public static SqlSearchFilterRow Caption(string title, bool first) =>
        new(title, "", new Thickness(0, first ? 0 : 8, 0, 4), isCaption: true, isSelected: false, selected: null);

    public static SqlSearchFilterRow Option(SqlSearchFilterOption option) =>
        new(option.Label, option.ToolTip, new Thickness(0, 2, 0, 2), isCaption: false, option.IsSelected, option.Selected);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; }

    public string ToolTip { get; }

    public Thickness Margin { get; }

    public bool IsCaption { get; }

    /// <summary>勾或取消勾；寫進來的只會是使用者的動作，宿主換選項是換掉整份列清單。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _selected?.Invoke(value);
        }
    }
}

/// <summary>過濾面板裡的一個勾選項。</summary>
internal sealed class SqlSearchFilterOption
{
    internal SqlSearchFilterOption(string label, string toolTip, bool isSelected, Action<bool> selected)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ToolTip = toolTip ?? "";
        IsSelected = isSelected;
        Selected = selected ?? throw new ArgumentNullException(nameof(selected));
    }

    public string Label { get; }

    public string ToolTip { get; }

    public bool IsSelected { get; }

    /// <summary>勾或取消勾；宿主在這裡改模型並重跑一輪。</summary>
    public Action<bool> Selected { get; }
}

/// <summary>
/// 已選條件的 chip 列；預設狀態整列收起，不佔那一列。
/// </summary>
/// <remarks>
/// 這是這個版面空間極大化的關鍵，作法沿用 SQL Memory 的篩選收合：沒有條件就不留空白列。
/// 收起用 <see cref="Visibility.Collapsed"/> 而不是把高度設成 0——後者仍會參與量測，
/// 而清單少掉的正是那幾個 DIP。
/// </remarks>
internal sealed class SqlSearchChipBar : ItemsControl
{
    public SqlSearchChipBar()
    {
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 4, 0, 0);
        AutomationProperties.SetName(this, "已選條件");
    }

    /// <summary>按下某一顆 chip 的十字；宿主據此清掉它代表的那一個條件。</summary>
    public event Action<object>? RemoveRequested;

    /// <summary>換一整列 chip；空的就整列收起。</summary>
    public void SetChips<T>(IReadOnlyList<T> chips, Func<T, string> label) where T : class
    {
        if (chips is null) throw new ArgumentNullException(nameof(chips));
        if (label is null) throw new ArgumentNullException(nameof(label));

        Items.Clear();

        foreach (var chip in chips)
        {
            var text = label(chip);
            var element = SqlAssistChrome.CreateFilterChip(text, out var remove);
            remove.Click += (_, _) => RemoveRequested?.Invoke(chip);
            Items.Add(element);
        }

        Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
