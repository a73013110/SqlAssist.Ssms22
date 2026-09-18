using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

internal sealed class SqlPillSelector : WrapPanel
{
    private readonly List<RadioButton> _buttons = new();
    private int _selectedIndex = -1;
    public event EventHandler? SelectionChanged;

    public SqlPillSelector(params (string Label, SqlIcon Icon)[] options)
        : this(Array.ConvertAll(options, option => (option.Label, (SqlIcon?)option.Icon)))
    {
    }

    /// <summary>
    /// 只有文字的膠囊。
    /// </summary>
    /// <remarks>
    /// 給選項由資料決定、沒有固定語意圖示的過濾列用（搜尋的分類 pill 由 provider 宣告的分類產生）。
    /// 硬挑一顆看似合理的圖示套給每一個分類，會讓不同意思的選項共用同一個形狀，
    /// 而辨識本來就該同時靠形狀與文字——兩者只剩文字時，至少沒有一個錯的形狀。
    /// </remarks>
    public SqlPillSelector(params string[] labels)
        : this(Array.ConvertAll(labels, label => (label, (SqlIcon?)null)))
    {
    }

    private SqlPillSelector((string Label, SqlIcon? Icon)[] options)
    {
        var group = Guid.NewGuid().ToString("N");
        foreach (var (label, icon) in options)
        {
            var index = _buttons.Count;
            var content = icon is { } glyph
                ? (object)SqlAssistChrome.CreateMemoryLabel(glyph, label)
                : SqlAssistChrome.CreateMemoryButtonText(label);
            var button = new RadioButton { Content = content, GroupName = group, Style = SqlAssistChrome.CreateMemoryPillStyle() };
            AutomationProperties.SetName(button, label);
            button.Checked += (_, _) => SelectedIndex = index;
            _buttons.Add(button); Children.Add(button);
        }
        if (_buttons.Count > 0) SelectedIndex = 0;
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value == _selectedIndex) return;
            if (value < 0 || value >= _buttons.Count) throw new ArgumentOutOfRangeException(nameof(value));
            _selectedIndex = value;
            _buttons[value].IsChecked = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>可收合的名稱膠囊；只負責呈現，資料與取消生命週期由宿主管理。</summary>
internal sealed class SqlConnectionFilter : StackPanel
{
    private readonly WrapPanel _options = new();
    private readonly Button _heading;
    private readonly Button _more;
    private readonly string _label;
    private readonly SqlIcon _icon;
    private readonly List<string> _names = new();
    private readonly string _group = Guid.NewGuid().ToString("N");
    private readonly Button _sortButton;
    private readonly ScrollViewer _optionsHost;
    private readonly TextBlock _summary = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private SqlConnectionFacetSort _sort = SqlConnectionFacetSort.Recent;
    private string? _value;
    /// <summary>未指定名稱的選項；History 與 Favorites 都表示不限。</summary>
    private const string AnyLabel = "全部";
    public event EventHandler? SelectionChanged;
    public event EventHandler? OptionsRequested;
    public ContextMenu SortMenu { get; } = new();
    public SqlConnectionFacetSort Sort
    {
        get => _sort;
        set
        {
            if (!SqlMemoryBrowserModel.SortOptions.Any(option => option.Value == value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_sort == value) return;
            _sort = value; UpdateSortButton();
            ResetOptions(); OptionsRequested?.Invoke(this, EventArgs.Empty);
        }
    }
    public int Offset { get; private set; }
    public bool IsExpanded
    {
        get => _optionsHost.Visibility == Visibility.Visible;
        set { _optionsHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed; UpdateHeading(); }
    }

    public SqlConnectionFilter(string label, SqlIcon icon = SqlIcon.Server)
    {
        _label = label;
        _icon = icon;
        var header = new DockPanel { MinHeight = 28 }; Children.Add(header);
        _heading = SqlAssistChrome.CreateButton(label, SqlAssistChrome.DefaultMetrics);
        _heading.Padding = new Thickness(6, 2, 6, 2);
        _heading.VerticalAlignment = VerticalAlignment.Center;
        _heading.ToolTip = "展開／收合" + label + "篩選；收合不會清除條件。";
        _heading.Click += (_, _) =>
        {
            IsExpanded = !IsExpanded;
        };
        DockPanel.SetDock(_heading, Dock.Left); header.Children.Add(_heading);
        _sortButton = SqlAssistChrome.CreateButton("最近", SqlAssistChrome.DefaultMetrics);
        _sortButton.Padding = new Thickness(6, 2, 6, 2);
        _sortButton.VerticalAlignment = VerticalAlignment.Center;
        _sortButton.ToolTip = label + "排序：最近／最早使用、名稱 A–Z／Z–A";
        UpdateSortButton();
        AutomationProperties.SetName(_sortButton, label + "排序");
        foreach (var option in SqlMemoryBrowserModel.SortOptions)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true, Tag = option.Value,
                Icon = SqlAssistChrome.CreateIcon(SqlAssistChrome.MemoryOptionIcon(option.Value)) };
            item.Click += (_, _) => Sort = option.Value;
            SortMenu.Items.Add(item);
        }
        _sortButton.Click += (_, _) =>
        {
            foreach (MenuItem item in SortMenu.Items) item.IsChecked = Equals(item.Tag, _sort);
            SortMenu.PlacementTarget = _sortButton; SortMenu.IsOpen = true;
        };
        DockPanel.SetDock(_sortButton, Dock.Right); header.Children.Add(_sortButton);
        _summary.Margin = new Thickness(4, 0, 4, 0); _summary.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_summary);
        // 先顯示條件摘要，使用時才揭露名稱；選項獨佔全寬，不讓窄窗的 Header 跨多列置中。
        // 膠囊右外距 4 DIP 讓出覆蓋握把；原生捲軸在 56 DIP 高的區塊裡幾乎只剩箭頭。
        _optionsHost = new ScrollViewer { Content = _options, MaxHeight = 56, Visibility = Visibility.Collapsed };
        SqlAssistChrome.ApplyOverlayScroll(_optionsHost);
        Children.Add(_optionsHost);
        _more = SqlAssistChrome.CreateButton("更多名稱", SqlAssistChrome.DefaultMetrics);
        _more.Visibility = Visibility.Collapsed;
        _more.Padding = new Thickness(6, 2, 6, 2);
        _more.Click += (_, _) => OptionsRequested?.Invoke(this, EventArgs.Empty);
        Margin = new Thickness(0, 0, 3, 2);
        Rebuild();
    }

    public string? Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value;
            // 選取既有名稱只改 checked，不能重建正在握有鍵盤焦點的膠囊。
            if (_options.Children.OfType<RadioButton>().Any(button => Equals(button.Tag, value)))
            {
                foreach (var button in _options.Children.OfType<RadioButton>()) button.IsChecked = Equals(button.Tag, value);
                UpdateHeading();
            }
            else Rebuild();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResetOptions() { Offset = 0; _names.Clear(); _more.Visibility = Visibility.Collapsed; Rebuild(); }
    /// <param name="names">儲存層多回一筆代表還有下一頁；多出的那一筆不顯示。</param>
    public void SetOptions(IReadOnlyList<string> names)
    {
        var count = Math.Min(names.Count, SqlConnectionFacetRequest.PageSize);
        for (var i = 0; i < count; i++) if (!_names.Contains(names[i])) _names.Add(names[i]);
        Offset += count;
        _more.Visibility = names.Count > SqlConnectionFacetRequest.PageSize ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    private void Rebuild()
    {
        var focused = _options.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsKeyboardFocused);
        var focusedValue = focused?.Tag;
        _options.Children.Clear(); Add(AnyLabel, null);
        if (_value != null && !_names.Contains(_value)) Add(_value, _value);
        foreach (var name in _names) Add(name, name);
        if (_more != null) _options.Children.Add(_more);
        UpdateHeading();
        if (focused != null)
            _options.Children.OfType<RadioButton>().FirstOrDefault(button => Equals(button.Tag, focusedValue))?.Focus();
    }

    private void Add(string label, string? value)
    {
        var button = new RadioButton { Content = SqlAssistChrome.CreateMemoryLabel(value is null ? SqlIcon.All : _icon, label),
            MaxWidth = 210, GroupName = _group, Tag = value, ToolTip = label, Style = SqlAssistChrome.CreateMemoryPillStyle(), IsChecked = _value == value };
        AutomationProperties.SetName(button, _label + "：" + label);
        button.Checked += (_, _) => Value = value;
        _options.Children.Add(button);
    }

    private void UpdateHeading()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var chevron = SqlAssistChrome.CreateChevron(IsExpanded);
        chevron.Margin = new Thickness(0, 0, 6, 0); content.Children.Add(chevron);
        var category = SqlAssistChrome.CreateIcon(_icon); category.Margin = new Thickness(0, 0, 5, 0);
        content.Children.Add(category);
        content.Children.Add(SqlAssistChrome.CreateMemoryButtonText(_label));
        _heading.Content = content;
        // Header 只表達 disclosure；選取狀態交給 pills，不把 Header 偽裝成另一個篩選項。
        _summary.Text = _value ?? AnyLabel; _summary.ToolTip = _summary.Text;
        _heading.ToolTip = (_value ?? "全部") + "；點擊展開／收合，不清除篩選。";
        AutomationProperties.SetName(_heading, (IsExpanded ? "收合" : "展開") + _label);
    }

    private void UpdateSortButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = SqlAssistChrome.CreateIcon(SqlAssistChrome.MemoryOptionIcon(_sort));
        icon.Margin = new Thickness(0, 0, 5, 0); content.Children.Add(icon);
        content.Children.Add(SqlAssistChrome.CreateMemoryButtonText(
            SqlMemoryBrowserModel.SortOptions.First(option => option.Value == _sort).ShortLabel));
        var chevron = SqlAssistChrome.CreateChevron(); chevron.Margin = new Thickness(4, 0, 0, 0);
        content.Children.Add(chevron); _sortButton.Content = content;
    }
}
