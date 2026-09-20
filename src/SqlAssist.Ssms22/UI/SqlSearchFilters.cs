using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
                Content = SqlAssistChrome.CreateButtonText(label),
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

/// <summary>過濾面板的三種形狀；差別只有互斥與否，以及面板上還有沒有別的東西。</summary>
internal enum SqlSearchFilterMode
{
    /// <summary>單選：選項畫成 radio，選完就關，沒有全選與清除。</summary>
    Single,

    /// <summary>複選：選項畫成核取方塊，面板留著讓人連勾好幾個，附全選與清除。</summary>
    Multiple,

    /// <summary>複選再加一個搜尋框；名稱可能上百個的清單才需要。</summary>
    SearchableMultiple
}

/// <summary>
/// 工具列上的過濾按鈕：按鈕顯示摘要，選項在彈出面板裡。
/// </summary>
/// <remarks>
/// 十幾種物件攤成 pill 會佔掉兩列，在停靠面板裡等於少看四筆結果；摘要留在按鈕上，
/// 完整名單留在面板與 chip 列。用 <see cref="Popup"/> 而不是 <see cref="ContextMenu"/>，
/// 是因為資料庫那一份面板裡有搜尋框與兩顆命令鈕——快捷選單裡的輸入欄拿不到鍵盤焦點。
///
/// 單選與複選<b>是同一個控制項的兩種模式</b>，不是兩個類別：外觀、面板、摘要與 chip 都一樣，
/// 只有互斥語意不同。分成兩個的症狀是其中一邊漏掉主題套用或 Esc 關閉，而那種漏只在
/// 深色主題或鍵盤操作時才看得出來。
///
/// 單選<b>不顯示</b>全選與清除：全選對互斥的選項沒有意義，而清除等於「一個範圍都不選」，
/// 那不是使用者做得到的狀態。單選選完就關面板——它一次只改得了一項，留著面板等於要他
/// 再按一次外面。
///
/// <see cref="PopupSurface"/> 要由宿主接上動態資源。Popup 的內容不在宿主的視覺樹上，
/// 沒有這一道就會在深色主題露出白底；這裡不自己做，是為了讓這個控制項留在純 WPF，
/// 主題回歸測試才編得進去。
/// </remarks>
internal sealed class SqlSearchFilterButton : Button
{
    private readonly TextBlock _label = SqlAssistChrome.CreateButtonText("");
    private readonly TextBlock _summary = SqlAssistChrome.CreateButtonText("");
    private readonly ItemsControl _options;
    private readonly SqlBusyNotice _notice = new();
    private readonly TextBox? _filter;
    private IReadOnlyList<SqlSearchFilterGroup> _groups = Array.Empty<SqlSearchFilterGroup>();
    private readonly Popup _popup;
    private readonly string _name;
    private bool _compact;

    /// <summary>選項區的高度上限；捲的是選項本身，搜尋框與兩顆命令鈕要一直看得見。</summary>
    private const double OptionsHeight = 280;

    /// <param name="mode">單選、複選，或複選加搜尋框。</param>
    public SqlSearchFilterButton(string name, SqlIcon icon, SqlSearchFilterMode mode = SqlSearchFilterMode.Multiple)
    {
        _name = name;
        Mode = mode;
        var single = mode == SqlSearchFilterMode.Single;
        _options = SqlAssistChrome.CreateSearchOptionList(OptionsHeight, single);
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

        if (mode == SqlSearchFilterMode.SearchableMultiple)
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

        if (!single)
        {
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            commands.Children.Add(CreateCommand(SqlIcon.SelectAll, "全選", () => SelectAllRequested?.Invoke(this, EventArgs.Empty)));
            commands.Children.Add(CreateCommand(SqlIcon.Clear, "清除", () => ClearRequested?.Invoke(this, EventArgs.Empty)));
            panel.Children.Add(commands);
        }

        // 提示在清單上方：清單本身可能是空的，而空清單底下的一行字要滑到底才看得到。
        panel.Children.Add(_notice);
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

    /// <summary>單選、複選，或複選加搜尋框。</summary>
    public SqlSearchFilterMode Mode { get; }

    /// <summary>面板開著沒有；單選選完自動關閉由它驗。</summary>
    public bool IsOpen => _popup.IsOpen;

    /// <summary>
    /// 面板要開了；宿主在這時候才去填選項。
    /// </summary>
    /// <remarks>
    /// 開下拉<b>就是</b>使用者在要求這份清單，所以宿主可以在這裡去問資料庫；不可以的是
    /// 在沒有人打開它的時候先問一輪。慢的那一份走 <see cref="SetNotice"/> 先說一句，
    /// 面板不會為了等它而空著。
    /// </remarks>
    public event EventHandler? OptionsRequested;

    /// <summary>全選；單選面板上沒有這顆鈕，也不會發這個事件。</summary>
    public event EventHandler? SelectAllRequested;

    /// <summary>清除；單選面板上沒有這顆鈕，也不會發這個事件。</summary>
    public event EventHandler? ClearRequested;

    /// <summary>窄窗只留圖示與箭頭；名稱與摘要留在 Tooltip 與 chip 列。</summary>
    /// <remarks>
    /// 收字之後主動把這顆按鈕整條路徑標成待量測。改 <see cref="UIElement.Visibility"/> 只把那兩個
    /// <see cref="TextBlock"/> 標成 dirty，中間的版面容器仍然有效——平常由版面管理員在下一回合
    /// 往上傳播，但工具列是在<b>同一個</b>量測回合裡立刻問寬度的，少了這一道會拿到收字前的那一份，
    /// 而症狀是窄窗明明收了字卻還是換行。
    /// </remarks>
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

            InvalidateMeasure();
            for (DependencyObject? node = _label; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is UIElement element) element.InvalidateMeasure();
                if (ReferenceEquals(node, this)) break;
            }
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

    /// <summary>
    /// 清單上方那一行狀態：正在讀取，或這一份為什麼不完整。
    /// </summary>
    /// <param name="message">空字串收起整列。</param>
    /// <param name="busy">還在等清單；轉圈只在這時候跑。</param>
    /// <remarks>
    /// 面板不因為清單還沒到就空著：空面板與「這台伺服器上一個都沒有」在畫面上一模一樣，
    /// 而使用者會關掉它去別的地方找。
    /// </remarks>
    public void SetNotice(string message, bool busy = false) => _notice.Show(message, busy);

    /// <summary>
    /// 打開面板，與使用者自己按下這顆按鈕走同一條路。
    /// </summary>
    /// <remarks>
    /// 宿主在別處（空狀態那顆按鈕）要讓使用者挑同一份清單時用它，不另外做一份選單：
    /// 兩份清單的下場是其中一邊漏掉分段、主題或選完關閉，而那種漏只在深色主題或
    /// 鍵盤操作時才看得出來。
    /// </remarks>
    public void Open()
    {
        OptionsRequested?.Invoke(this, EventArgs.Empty);
        _popup.IsOpen = true;
    }

    /// <summary>
    /// 單選選完就關；宿主換完範圍之後才關，不搶在它前面。
    /// </summary>
    /// <remarks>
    /// 只有「選上」才關。選項已經是選上的那一個時再按一次，radio 不會發出取消，
    /// 而宿主重填選項時走的是繫結而不是這條路；真的收到 false 時那是宿主寫回來的，
    /// 關掉面板等於替使用者關掉他還在看的清單。
    /// </remarks>
    private void CloseAfterPick(bool selected)
    {
        if (!selected) return;
        _popup.IsOpen = false;
        Focus();
    }

    private Button CreateCommand(SqlIcon icon, string label, Action run)
    {
        var button = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
        button.Content = SqlAssistChrome.CreateIconLabel(icon, label);
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
                rows.Add(SqlSearchFilterRow.Option(item, Mode == SqlSearchFilterMode.Single ? CloseAfterPick : null));
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
    private readonly Action<bool>? _picked;
    private bool _isSelected;

    private SqlSearchFilterRow(
        string label,
        string toolTip,
        Thickness margin,
        bool isCaption,
        bool isSelected,
        Action<bool>? selected,
        Action<bool>? picked)
    {
        Label = label;
        ToolTip = toolTip;
        Margin = margin;
        IsCaption = isCaption;
        _isSelected = isSelected;
        _selected = selected;
        _picked = picked;
    }

    /// <param name="first">整份清單的第一列不留上緣間距，否則面板頂端會多出一條空白。</param>
    public static SqlSearchFilterRow Caption(string title, bool first) =>
        new(title, "", new Thickness(0, first ? 0 : 8, 0, 4), isCaption: true, isSelected: false, selected: null, picked: null);

    /// <param name="picked">選完之後要做的事（單選是關面板）；複選傳 null。</param>
    public static SqlSearchFilterRow Option(SqlSearchFilterOption option, Action<bool>? picked = null) =>
        new(option.Label, option.ToolTip, new Thickness(0, 2, 0, 2), isCaption: false, option.IsSelected, option.Selected, picked);

    /// <summary>
    /// 單選鈕的群組名；每一列各一個，等於不讓 WPF 自動互斥。
    /// </summary>
    /// <remarks>
    /// 互斥由模型負責，理由見 <c>SqlAssistChrome.CreateSearchOptionRow</c>。複選的列不讀它。
    /// </remarks>
    public string GroupName { get; } = Guid.NewGuid().ToString("N");

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
            _picked?.Invoke(value);
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
