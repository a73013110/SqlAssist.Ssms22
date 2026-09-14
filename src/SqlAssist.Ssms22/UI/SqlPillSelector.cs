using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Ssms22.UI;

internal sealed class SqlPillSelector : WrapPanel
{
    private readonly List<RadioButton> _buttons = new();
    private int _selectedIndex = -1;
    public event EventHandler? SelectionChanged;

    public SqlPillSelector(params string[] labels)
    {
        var group = Guid.NewGuid().ToString("N");
        foreach (var label in labels)
        {
            var index = _buttons.Count;
            var button = new RadioButton { Content = label, GroupName = group, Style = SqlAssistChrome.CreateQueryPillStyle() };
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
    private readonly List<string> _names = new();
    private readonly string _group = Guid.NewGuid().ToString("N");
    private readonly Button _sortButton;
    private readonly ScrollViewer _optionsHost;
    private readonly TextBlock _summary = SqlAssistChrome.CreateMetadataText("", SqlAssistChrome.DefaultMetrics);
    private QueryConnectionSort _sort = QueryConnectionSort.Recent;
    private string? _value;
    private string _emptyLabel = "全部";
    public string EmptyLabel
    {
        get => _emptyLabel;
        set { if (_emptyLabel == value) return; _emptyLabel = value; Rebuild(); }
    }
    public event EventHandler? SelectionChanged;
    public event EventHandler? OptionsRequested;
    public ContextMenu SortMenu { get; } = new();
    public QueryConnectionSort Sort
    {
        get => _sort;
        set
        {
            if (!QueryMemoryBrowserModel.SortOptions.Any(option => option.Value == value)) throw new ArgumentOutOfRangeException(nameof(value));
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

    public SqlConnectionFilter(string label)
    {
        _label = label;
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
        foreach (var option in QueryMemoryBrowserModel.SortOptions)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true, Tag = option.Value };
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
        _optionsHost = new ScrollViewer { Content = _options, MaxHeight = 56, Visibility = Visibility.Collapsed,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
        var count = Math.Min(names.Count, QueryConnectionFacetRequest.PageSize);
        for (var i = 0; i < count; i++) if (!_names.Contains(names[i])) _names.Add(names[i]);
        Offset += count;
        _more.Visibility = names.Count > QueryConnectionFacetRequest.PageSize ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    private void Rebuild()
    {
        var focused = _options.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsKeyboardFocused);
        var focusedValue = focused?.Tag;
        _options.Children.Clear(); Add(_emptyLabel, null);
        if (_value != null && !_names.Contains(_value)) Add(_value, _value);
        foreach (var name in _names) Add(name, name);
        if (_more != null) _options.Children.Add(_more);
        UpdateHeading();
        if (focused != null)
            _options.Children.OfType<RadioButton>().FirstOrDefault(button => Equals(button.Tag, focusedValue))?.Focus();
    }

    private void Add(string label, string? value)
    {
        var button = new RadioButton { Content = SqlAssistChrome.CreateQueryButtonText(label),
            MaxWidth = 210, GroupName = _group, Tag = value, ToolTip = label, Style = SqlAssistChrome.CreateQueryPillStyle(), IsChecked = _value == value };
        AutomationProperties.SetName(button, _label + "：" + label);
        button.Checked += (_, _) => Value = value;
        _options.Children.Add(button);
    }

    private void UpdateHeading()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = SqlAssistChrome.CreateQueryButtonIcon("Chevron");
        // 旋轉只改繪圖、不改量測；固定 slot 讓收合及焦點都不推動旁邊文字。
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        icon.RenderTransform = new System.Windows.Media.RotateTransform(IsExpanded ? 0 : -90);
        icon.Margin = new Thickness(0, 0, 6, 0); content.Children.Add(icon);
        content.Children.Add(SqlAssistChrome.CreateQueryButtonText(_label));
        _heading.Content = content;
        // Header 只表達 disclosure；選取狀態交給 pills，不把 Header 偽裝成另一個篩選項。
        _summary.Text = _value ?? _emptyLabel; _summary.ToolTip = _summary.Text;
        _heading.ToolTip = (_value ?? "全部") + "；點擊展開／收合，不清除篩選。";
        AutomationProperties.SetName(_heading, (IsExpanded ? "收合" : "展開") + _label);
    }

    private void UpdateSortButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(SqlAssistChrome.CreateQueryButtonText(
            QueryMemoryBrowserModel.SortOptions.First(option => option.Value == _sort).ShortLabel));
        var chevron = SqlAssistChrome.CreateQueryButtonIcon("Chevron"); chevron.Margin = new Thickness(4, 0, 0, 0);
        content.Children.Add(chevron); _sortButton.Content = content;
    }
}
