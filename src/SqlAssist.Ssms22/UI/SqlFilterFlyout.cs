using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SqlAssist.Ssms22.UI;

/// <summary>過濾面板的三種形狀；差別只有互斥與否，以及面板上還有沒有別的東西。</summary>
internal enum SqlFilterMode
{
    /// <summary>單選：選項畫成 radio，選完就關，沒有全選。</summary>
    Single,

    /// <summary>複選：選項畫成核取方塊，面板留著讓人連勾好幾個。</summary>
    Multiple,

    /// <summary>複選再加一個搜尋框；名稱可能上百個的清單才需要。</summary>
    SearchableMultiple
}

/// <summary>
/// 面板上那一列整批命令。
/// </summary>
/// <remarks>
/// 兩顆並排而且一直亮著，不隨狀態收起也不停用。收起的那一版在剩下那一顆滑進它的位置時
/// 讓同一個像素換了意思——按完全選、手沒移開再按一次就全清掉；停用的那一版則永遠有一顆是灰的。
/// 代價是全勾時按全選、全空時按全不選各是一次 no-op，而 no-op 毀不掉任何東西，
/// 比會動、會換意思的按鈕便宜。
///
/// 有沒有這一列由<b>宿主</b>決定，不由模式決定，而且只在接線時講一次：兩顆的字不跟狀態跑，
/// 宿主不必每次重填選項都回來同步一次。要再加一種整批命令（例如反向選擇）就多一個列舉值。
/// </remarks>
internal enum SqlFilterBulkCommands
{
    /// <summary>沒有整批命令。</summary>
    None,

    /// <summary>全選與全不選並排。</summary>
    SelectAndClear
}

/// <summary>
/// 工具列上的過濾按鈕：按鈕顯示摘要，選項在彈出面板裡。SQL Memory 與 SQL Search 共用這一份。
/// </summary>
/// <remarks>
/// 十幾種物件攤成 pill 會佔掉兩列，在停靠面板裡等於少看四筆結果；摘要留在按鈕上，
/// 完整名單留在面板與 Tooltip。用 <see cref="Popup"/> 而不是 <see cref="ContextMenu"/>，
/// 是因為資料庫那一份面板裡有搜尋框與命令鈕——快捷選單裡的輸入欄拿不到鍵盤焦點。
///
/// 單選與複選<b>是同一個控制項的兩種模式</b>，不是兩個類別：外觀、面板與摘要都一樣，
/// 只有互斥語意不同。分成兩個的症狀是其中一邊漏掉主題套用或 Esc 關閉，而那種漏只在
/// 深色主題或鍵盤操作時才看得出來。同樣的理由，<b>整個擴充只有這一份過濾面板</b>：
/// SQL Memory 的連線篩選曾經是另一個 inline 控制項，兩份各自有一套主題套用、鍵盤路徑與
/// 選項回收規則，而它們要解的是同一件事。
///
/// 「沒有勾任何一個」在這幾個面板上都是一個<b>實際的預設</b>（全部種類、全部伺服器、
/// 連線預設的資料庫），所以它由 <see cref="SetEmptyOption"/> 畫成面板第一列，而不是靠一片
/// 空白表達：摘要寫著「全部」而清單上一個勾都沒有時，使用者會以為自己把條件弄丟了。
/// 那一列的字<b>由宿主給</b>——SQL Memory 的未選是「對已存的列不設限」，SQL Search 的未選是
/// 「這一輪用連線預設」，兩句話不一樣，控制項不替它們挑一句。
///
/// 面板上的整批命令是<b>兩顆並排、一直亮著</b>的全選與全不選（<see cref="SetBulkCommands"/>），
/// 要不要由宿主決定；判準是<b>有沒有搜尋框</b>，不是是不是複選——沒有搜尋框時「列出來的那一份」
/// 恆等於整份，全選就與「一個都沒勾」同義，那一列只剩一顆按不出差別的鈕。
/// 全不選與第一列那個預設做的是同一件事，但兩個都要有：第一列是一個<b>值</b>，回答「沒指名時
/// 用哪一個」，而命令列上是一個<b>動作</b>，回答「把我剛勾的這幾個拿掉」。勾了一部分是最常見的
/// 狀態，那時使用者要的是後者。
/// 全選只作用在<b>目前列出來的那一份</b>（套用搜尋字之後，見 <see cref="Matches"/>）：打了字就是
/// 「把篩出來的這幾個都勾起來」，沒打字才是整份。全不選相反，清的是整個維度——它走的就是
/// 第一列那個預設的清除路徑，不另寫一次狀態同步，兩份的下場是其中一邊忘了同步第一列的勾。
/// 單選沒有這一列：全選對互斥的選項沒有意義。單選選完就關面板——它一次只改得了一項，
/// 留著面板等於要他再按一次外面。
///
/// 排序（<see cref="SetSortOptions"/>）與續頁（<see cref="SetMore"/>）是<b>面板等級的一般能力</b>，
/// 不綁任何一個功能的值：清單是上百個名稱時，排序決定哪一頁先到，續頁才問得到下一頁。
///
/// <see cref="PopupSurface"/> 與 <see cref="SortMenu"/> 要由宿主接上動態資源。兩者的內容都不在
/// 宿主的視覺樹上，沒有這一道就會在深色主題露出白底；這裡不自己做，是為了讓這個控制項
/// 留在純 WPF，主題回歸測試才編得進去。
/// </remarks>
internal sealed class SqlFilterFlyout : Button
{
    private readonly TextBlock _label = SqlAssistChrome.CreateButtonText("");
    private readonly TextBlock _summary = SqlAssistChrome.CreateButtonText("");
    private readonly ItemsControl _options;
    private readonly SqlBusyNotice _notice = new();
    private readonly TextBox? _filter;
    // LastChildFill 要關掉：開著的話最後加進去的那一顆會吃掉排序左邊的整段空白，
    // 命令鈕就從一顆貼著文字的鈕變成一條橫跨面板的色塊。
    private readonly DockPanel _commands = new() { Margin = new Thickness(0, 0, 0, 4), LastChildFill = false };
    private readonly Button _sortButton;
    private readonly Button _selectAll;
    private readonly Button _clearAll;
    private readonly Button _more;
    private readonly ContentPresenter _defaultRow;
    private readonly Border _defaultDivider;
    private IReadOnlyList<SqlFilterGroup> _groups = Array.Empty<SqlFilterGroup>();
    private IReadOnlyList<SqlFilterSortOption> _sorts = Array.Empty<SqlFilterSortOption>();
    private object? _sort;
    private SqlFilterRow? _empty;
    private readonly Popup _popup;
    private readonly Path _chevron;
    private Path? _sortChevron;
    private readonly string _name;
    private readonly bool? _motion;
    private bool _compact;

    /// <summary>選項區的高度上限；捲的是選項本身，搜尋框、命令鈕與續頁鈕要一直看得見。</summary>
    private const double OptionsHeight = 280;

    /// <param name="mode">單選、複選，或複選加搜尋框。</param>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public SqlFilterFlyout(string name, SqlIcon icon, SqlFilterMode mode = SqlFilterMode.Multiple, bool? motion = null)
    {
        _name = name;
        _motion = motion;
        Mode = mode;
        var single = mode == SqlFilterMode.Single;
        _options = SqlAssistChrome.CreateFilterOptionList(OptionsHeight, single);
        Style = SqlAssistChrome.CreateFilterButtonStyle();
        Template = SqlAssistChrome.CreateFilterButtonTemplate();
        Padding = new Thickness(6, 2, 6, 2);
        _label.Text = name + ": ";

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = SqlAssistChrome.CreateIcon(icon);
        glyph.Margin = new Thickness(0, 0, 5, 0);
        content.Children.Add(glyph);
        content.Children.Add(_label);
        content.Children.Add(_summary);
        _chevron = SqlAssistChrome.CreateChevron(expanded: false);
        _chevron.Margin = new Thickness(3, 0, 0, 0);
        content.Children.Add(_chevron);
        Content = content;

        var panel = new StackPanel();

        if (mode == SqlFilterMode.SearchableMultiple)
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

        // 排序靠右、整批命令靠左：兩者都是這個面板的命令，但排序改的是「哪一頁先到」，
        // 全選與全不選改的是條件本身；分在同一列的兩端才看得出不是同一種東西。
        _sortButton = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
        _sortButton.Padding = new Thickness(6, 2, 6, 2);
        _sortButton.Visibility = Visibility.Collapsed;
        _sortButton.Click += (_, _) => OpenSortMenu();
        AutomationProperties.SetName(_sortButton, name + "排序");
        DockPanel.SetDock(_sortButton, Dock.Right);
        _commands.Children.Add(_sortButton);

        // 兩顆整批命令先建起來但整列收著；建好之後字就不再動。全選排在前面：它是那一列的
        // 主要動作，全不選是退路。
        _selectAll = CreateCommand(SqlIcon.SelectAll, "全選", RunSelectAll);
        _clearAll = CreateCommand(SqlIcon.Clear, "全不選", RunClearAll);
        foreach (var command in new[] { _selectAll, _clearAll })
        {
            command.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(command, Dock.Left);
            _commands.Children.Add(command);
        }

        // 命令列上兩邊都還沒有人要時整列先收起；沒有人叫 SetSortOptions 或 SetBulkCommands 的面板不留一條空白。
        _commands.Visibility = Visibility.Collapsed;

        panel.Children.Add(_commands);

        // 提示在清單上方：清單本身可能是空的，而空清單底下的一行字要滑到底才看得到。
        panel.Children.Add(_notice);

        // 第一列那個預設與它底下那條橫線都在捲動區外面，理由見 SqlAssistChrome.CreateFilterDefaultRow。
        _defaultRow = SqlAssistChrome.CreateFilterDefaultRow();
        _defaultDivider = SqlAssistChrome.CreateFilterPanelDivider();
        _defaultRow.Visibility = Visibility.Collapsed;
        _defaultDivider.Visibility = Visibility.Collapsed;
        panel.Children.Add(_defaultRow);
        panel.Children.Add(_defaultDivider);
        panel.Children.Add(_options);

        // 續頁鈕在清單外面：它要一直看得見。捲進清單裡的那一版得先滑到底才按得到，
        // 而清單正是因為還沒到齊才需要它。
        _more = SqlAssistChrome.CreateButton("", SqlAssistChrome.DefaultMetrics);
        _more.Padding = new Thickness(6, 2, 6, 2);
        _more.Margin = new Thickness(0, 4, 0, 0);
        _more.HorizontalAlignment = HorizontalAlignment.Left;
        _more.Visibility = Visibility.Collapsed;
        _more.Click += (_, _) => MoreRequested?.Invoke(this, EventArgs.Empty);
        panel.Children.Add(_more);

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

        // 排序選單是自己的一個 popup：它開起來時這個面板會被當成「按到外面」而關掉，
        // 而使用者按排序正是為了看重排之後的這一份清單。開選單的期間先不讓它自己關。
        SortMenu.Opened += (_, _) =>
        {
            _popup.StaysOpen = true;
            if (_sortChevron is { } glyph) SqlAssistChrome.SetChevronExpanded(glyph, expanded: true, _motion);
        };
        SortMenu.Closed += (_, _) =>
        {
            RestoreAutoClose();
            if (_sortChevron is { } glyph) SqlAssistChrome.SetChevronExpanded(glyph, expanded: false, _motion);
        };

        Click += (_, _) => Open();
        // 箭頭轉向與面板開合同一個事實。綁在 Click 上的那一版在「按到外面自己關掉」時不會轉回來，
        // 而按鈕上就一直畫著一顆朝下的箭頭，指著一個已經不在畫面上的面板。
        _popup.Opened += (_, _) =>
        {
            SqlAssistChrome.SetChevronExpanded(_chevron, expanded: true, _motion);
            SqlAssistChrome.PlayAppear(surface);
            _filter?.Focus();
        };
        _popup.Closed += (_, _) =>
        {
            // 面板是在排序選單還開著的時候被收掉的：留著 StaysOpen 的話，下一次打開就是一張
            // 再也不會因為按到外面而收合的面板。關著時寫它不影響捕捉，下一次打開才重新取得。
            _popup.StaysOpen = false;
            SqlAssistChrome.SetChevronExpanded(_chevron, expanded: false, _motion);
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

    /// <summary>排序選單；與 <see cref="PopupSurface"/> 一樣要由宿主套一次主題資源。</summary>
    public ContextMenu SortMenu { get; } = new();

    /// <summary>單選、複選，或複選加搜尋框。</summary>
    public SqlFilterMode Mode { get; }

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

    /// <summary>
    /// 全選目前列出來的那一份；字串是面板搜尋框裡的過濾字，空字串表示沒打字。
    /// </summary>
    /// <remarks>
    /// 交出去的是過濾字而不是面板上那幾列：清單分頁或還在路上時，面板列得出來的不等於宿主
    /// 要勾的那一份（SQL Search 按下去會先把整台的資料庫問回來）。兩邊用 <see cref="Matches"/>
    /// 同一條比對規則，才不會出現面板上看到五個、勾起來卻是七個。
    /// 只有宿主叫過 <see cref="SetBulkCommands"/> 的面板發得出這個事件。
    /// </remarks>
    public event Action<string>? SelectAllRequested;

    /// <summary>
    /// 全不選。
    /// </summary>
    /// <remarks>
    /// 清的是整個維度，不是篩出來的那一份：宿主接到它要走與第一列那個預設<b>同一條</b>清除路徑，
    /// 不另寫一份：兩份的下場是其中一邊忘了同步第一列的勾，而畫面上「全部」沒勾、條件卻已經清光。
    /// </remarks>
    public event EventHandler? ClearAllRequested;

    /// <summary>
    /// 按下續頁鈕；宿主據此去問下一頁，並把新名稱接在現有清單後面。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="OptionsRequested"/> 分開：後者是「這份清單從頭來」，而續頁要接在
    /// 已經看到的那些後面。合成同一個事件的症狀是按了更多卻跳回第一頁，
    /// 而使用者剛捲到的位置也一起沒了。位移由宿主記——面板不知道一頁是幾筆。
    /// </remarks>
    public event EventHandler? MoreRequested;

    /// <summary>換一種排序；宿主換完之後用 <see cref="SetSortOptions"/> 把選中的那一個寫回來。</summary>
    /// <remarks>
    /// 不在這裡就地改狀態：排序換的是宿主要去問的那一份請求，而它可能失敗或被新的一輪取代。
    /// 面板先亮起新的排序、結果卻是舊的那一份，使用者看不出是哪一邊錯了。
    /// </remarks>
    public event Action<object>? SortRequested;

    /// <summary>窄窗只留圖示與箭頭；名稱與摘要留在 Tooltip，有沒有條件看 <see cref="IsNarrowed"/>。</summary>
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

    /// <summary>
    /// 這個維度不是預設值（有勾選、或指名了一台）；按鈕換成強調底加強調框。
    /// </summary>
    /// <remarks>
    /// 與搜尋框裡的開關「開著」同一組色：兩者說的都是「這一顆正在縮小結果」。按鈕本身就是
    /// 條件的出口，所以不在工具列下面另外列一排已選條件——那一排與按鈕摘要說的是同一件事，
    /// 卻要多佔一列。窄窗收掉摘要之後，靠的就是這個底框。預設值由宿主判斷，控制項不猜。
    /// </remarks>
    public static readonly DependencyProperty IsNarrowedProperty = DependencyProperty.Register(
        nameof(IsNarrowed), typeof(bool), typeof(SqlFilterFlyout), new PropertyMetadata(false));

    public bool IsNarrowed
    {
        get => (bool)GetValue(IsNarrowedProperty);
        set => SetValue(IsNarrowedProperty, value);
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
    public void SetOptions(IReadOnlyList<SqlFilterGroup> groups)
    {
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        ApplyFilter();
    }

    /// <summary>
    /// 面板第一列那個「沒有指名＝用這個」的預設；null 表示這個面板沒有預設可回。
    /// </summary>
    /// <remarks>
    /// 它畫成 radio 且在捲動區外面（<see cref="SqlAssistChrome.CreateFilterDefaultRow"/>），所以
    /// 既不受搜尋框過濾，也不會隨清單捲出畫面——打了字之後一個都不相符、或名稱有上百個時，
    /// 使用者都還回得到預設。字由宿主給，與按鈕摘要共用同一份（例如「連線預設（master）」或
    /// 「全部」），兩處不會說得不一樣，控制項也不替任何一個功能挑一句。
    /// </remarks>
    public void SetEmptyOption(SqlFilterOption? option)
    {
        _empty = option is null
            ? null
            : SqlFilterRow.Empty(option, Mode == SqlFilterMode.Single ? CloseAfterPick : null);
        _defaultRow.Content = _empty;
        var visibility = _empty is null ? Visibility.Collapsed : Visibility.Visible;
        _defaultRow.Visibility = visibility;
        _defaultDivider.Visibility = visibility;
    }

    /// <summary>
    /// 別的選項勾掉或取消之後，把預設那一列的勾改過來。
    /// </summary>
    /// <remarks>
    /// 只改那一列，不重建整份清單：使用者連勾三個資料庫時，重建會把捲動位置與鍵盤焦點
    /// 一起丟掉，而他正在往下走。寫進去不回呼宿主——這是把模型的結果畫出來，不是一次選取。
    /// </remarks>
    public void SyncEmptyOption(bool selected) => _empty?.Sync(selected);

    /// <summary>
    /// 面板上那一列整批命令；<see cref="SqlFilterBulkCommands.None"/>（預設）收起它。
    /// </summary>
    /// <remarks>
    /// 接線時講一次就夠：兩顆一直亮著，字不跟狀態跑，宿主重填選項時不必回來同步。
    /// 單選不畫這一列，傳什麼進來都一樣。
    /// </remarks>
    public void SetBulkCommands(SqlFilterBulkCommands commands)
    {
        if (!Enum.IsDefined(typeof(SqlFilterBulkCommands), commands)) throw new ArgumentOutOfRangeException(nameof(commands));
        if (Mode == SqlFilterMode.Single) commands = SqlFilterBulkCommands.None;
        var show = commands != SqlFilterBulkCommands.None;
        if (show == (_selectAll.Visibility == Visibility.Visible)) return;

        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _selectAll.Visibility = visibility;
        _clearAll.Visibility = visibility;
        // 排序還在的話整列留著；兩邊都沒有才收掉，面板頂端不留一條空白。
        _commands.Visibility = show || _sortButton.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 全選那一顆的範圍註腳；null 或空字串只留基本的名稱。
    /// </summary>
    /// <remarks>
    /// 全選作用在「面板列出來的那一份」，而那一份有沒有到齊只有宿主知道：SQL Memory 的名稱是
    /// 分頁問回來的，按下去只勾得到已載入的那幾頁；SQL Search 會先把整台問回來，沒有這個界線。
    /// 話由宿主說，控制項不替任何一個功能挑一句。
    ///
    /// 掛在 Tooltip 與 <see cref="AutomationProperties.HelpTextProperty"/>，不佔清單上方那一行：
    /// 那一行同時要報「正在讀取」與載入失敗，兩件事輪流蓋掉彼此之後都說不清楚，而這一句是
    /// 按鈕的範圍註腳，不是面板的狀態。
    /// </remarks>
    public void SetSelectAllHint(string? hint)
    {
        var tip = "全選" + _name;
        _selectAll.ToolTip = string.IsNullOrEmpty(hint) ? tip : tip + Environment.NewLine + hint;
        AutomationProperties.SetHelpText(_selectAll, hint ?? "");
    }

    /// <summary>
    /// 面板底部的續頁鈕；null 或空字串收起它。
    /// </summary>
    /// <remarks>
    /// 字由宿主給（「更多名稱」「更多資料庫」）：面板不知道自己列的是什麼。還有沒有下一頁
    /// 也由宿主判斷——儲存層是多回一筆還是回總數，是那一層自己的契約。
    /// </remarks>
    public void SetMore(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            _more.Visibility = Visibility.Collapsed;
            return;
        }

        _more.Content = SqlAssistChrome.CreateButtonText(label!);
        _more.ToolTip = label;
        AutomationProperties.SetName(_more, label);
        _more.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 面板裡的排序入口；不呼叫就沒有這顆鈕。
    /// </summary>
    /// <remarks>
    /// 按鈕與選單讀同一份 <paramref name="options"/>，圖示由宿主在那一份裡指定：兩邊各挑一次
    /// 圖示的下場是按鈕上畫著升冪而選單上打勾的那一列是降冪，而它們其實是同一個值。
    /// </remarks>
    /// <param name="selected">目前生效的排序；必須在 <paramref name="options"/> 裡。</param>
    public void SetSortOptions(IReadOnlyList<SqlFilterSortOption> options, object selected)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.Count == 0) throw new ArgumentException("排序至少要有一個選項。", nameof(options));
        if (selected is null) throw new ArgumentNullException(nameof(selected));

        // 同一份選項只建一次選單：每次換排序都重建的話，正開著的那一份會在使用者按下去的
        // 那一刻被抽掉，而重建也讓鍵盤走到一半的位置回到第一項。
        if (!ReferenceEquals(_sorts, options))
        {
            _sorts = options;
            SortMenu.Items.Clear();
            foreach (var option in options)
            {
                var item = new MenuItem
                {
                    Header = option.Label,
                    IsCheckable = true,
                    Tag = option.Value,
                    Icon = SqlAssistChrome.CreateIcon(option.Icon)
                };
                item.Click += (_, _) => SortRequested?.Invoke(option.Value);
                SortMenu.Items.Add(item);
            }
        }

        var current = Find(selected);
        _sort = current.Value;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = SqlAssistChrome.CreateIcon(current.Icon);
        icon.Margin = new Thickness(0, 0, 5, 0);
        content.Children.Add(icon);
        content.Children.Add(SqlAssistChrome.CreateButtonText(current.ShortLabel));
        // 與過濾按鈕同一顆箭頭：收起時朝右，選單開著時朝下。內容重建時把新的那一顆記下來，
        // 記著舊的那一版會讓排序換過一次之後箭頭再也不轉。
        _sortChevron = SqlAssistChrome.CreateChevron(expanded: SortMenu.IsOpen);
        _sortChevron.Margin = new Thickness(4, 0, 0, 0);
        content.Children.Add(_sortChevron);
        _sortButton.Content = content;
        _sortButton.ToolTip = _name + "排序：" + current.Label;
        _sortButton.Visibility = Visibility.Visible;
        _commands.Visibility = Visibility.Visible;
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

    private SqlFilterSortOption Find(object value)
    {
        foreach (var option in _sorts)
        {
            if (Equals(option.Value, value)) return option;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "這個值不在這一份排序選項裡。");
    }

    private void OpenSortMenu()
    {
        foreach (MenuItem item in SortMenu.Items) item.IsChecked = Equals(item.Tag, _sort);
        SortMenu.PlacementTarget = _sortButton;
        SortMenu.IsOpen = true;
    }

    /// <summary>
    /// 排序選單收掉之後，把「按到外面就關」還給這個面板。
    /// </summary>
    /// <remarks>
    /// <see cref="Popup.StaysOpen"/> 轉回 false 時，Popup 會去把滑鼠捕捉拿回來，而那一步只在
    /// <b>沒有人正握著捕捉</b>時成立。選單關閉事件發出的那一刻捕捉還在選單自己手上，就地還原
    /// 等於整步跳過：這個面板從此不再因為按到外面而收合——使用者接著去開旁邊那一顆資料庫下拉，
    /// 伺服器這一份仍然開著，兩張面板疊在畫面上，而唯一收得掉它的方法是挑掉其中一個選項。
    /// 排到這一輪輸入之後再還原，捕捉已經放開，Popup 才拿得回來。
    ///
    /// 期間使用者可能已經關掉面板或又打開了選單，所以還原前重新確認一次狀態。
    /// </remarks>
    private void RestoreAutoClose() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    {
        if (!_popup.IsOpen || SortMenu.IsOpen) return;
        _popup.StaysOpen = false;
    }));

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
        button.Padding = new Thickness(6, 2, 6, 2);
        button.Margin = new Thickness(0, 0, 4, 0);
        button.Click += (_, _) => run();
        Label(button, icon, label);
        return button;
    }

    private void Label(Button button, SqlIcon icon, string label)
    {
        button.Content = SqlAssistChrome.CreateIconLabel(icon, label);
        button.ToolTip = label + _name;
        AutomationProperties.SetName(button, label + _name);
    }

    /// <summary>全選：把目前的過濾字一起交出去，宿主才知道「列出來的那一份」是哪幾個。</summary>
    private void RunSelectAll() => SelectAllRequested?.Invoke(_filter?.Text ?? "");

    private void RunClearAll() => ClearAllRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>搜尋框的比對規則：名稱裡有這一段就算中，不分大小寫；空字串全中。</summary>
    /// <remarks>
    /// 公開出來是因為全選要在宿主那一端照同一份規則再篩一次（見 <see cref="SelectAllRequested"/>）；
    /// 兩邊各寫一次的症狀是面板上看到五個、按下去勾起來的卻是七個。
    /// </remarks>
    public static bool Matches(string label, string pattern) =>
        pattern.Length == 0 || label.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

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
        var rows = new List<SqlFilterRow>();

        foreach (var group in _groups)
        {
            var start = rows.Count;

            foreach (var item in group.Items)
            {
                if (!Matches(item.Label, pattern)) continue;
                rows.Add(SqlFilterRow.Option(item, Mode == SqlFilterMode.Single ? CloseAfterPick : null));
            }

            if (rows.Count == start) continue;
            if (group.Title.Length != 0) rows.Insert(start, SqlFilterRow.Caption(group.Title, first: start == 0));
        }

        _options.ItemsSource = rows;
    }
}

/// <summary>排序入口的一個選項；面板上的按鈕與選單讀同一份。</summary>
internal sealed class SqlFilterSortOption
{
    /// <param name="shortLabel">按鈕上那一個字；選單上仍用完整的 <paramref name="label"/>。</param>
    internal SqlFilterSortOption(object value, string label, string shortLabel, SqlIcon icon)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ShortLabel = shortLabel ?? throw new ArgumentNullException(nameof(shortLabel));
        Icon = icon;
    }

    public object Value { get; }

    public string Label { get; }

    public string ShortLabel { get; }

    public SqlIcon Icon { get; }
}

/// <summary>過濾面板裡的一段：標題加上它底下的選項。</summary>
internal sealed class SqlFilterGroup
{
    internal SqlFilterGroup(string title, IReadOnlyList<SqlFilterOption> items)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public string Title { get; }

    public IReadOnlyList<SqlFilterOption> Items { get; }
}

/// <summary>
/// 攤平後的一列：一段的標題，或一個勾選項。
/// </summary>
/// <remarks>
/// 虛擬化要的是一份平的清單，所以標題與選項同型。狀態留在這裡而不是留在
/// <see cref="SqlFilterOption"/>：後者是宿主每次重填時新建的一份契約，
/// 這一列才是繫結寫得回去的那一端。
/// </remarks>
internal sealed class SqlFilterRow : INotifyPropertyChanged
{
    private readonly Action<bool>? _selected;
    private readonly Action<bool>? _picked;
    private readonly bool _sticky;
    private bool _isSelected;

    private SqlFilterRow(
        string label,
        string toolTip,
        Thickness margin,
        bool isCaption,
        bool isSelected,
        Action<bool>? selected,
        Action<bool>? picked,
        bool sticky = false)
    {
        Label = label;
        ToolTip = toolTip;
        Margin = margin;
        IsCaption = isCaption;
        _isSelected = isSelected;
        _selected = selected;
        _picked = picked;
        _sticky = sticky;
    }

    /// <param name="first">整份清單的第一列不留上緣間距，否則面板頂端會多出一條空白。</param>
    public static SqlFilterRow Caption(string title, bool first) =>
        new(title, "", new Thickness(0, first ? 0 : 8, 0, 4), isCaption: true, isSelected: false, selected: null, picked: null);

    /// <param name="picked">選完之後要做的事（單選是關面板）；複選傳 null。</param>
    public static SqlFilterRow Option(SqlFilterOption option, Action<bool>? picked = null) =>
        new(option.Label, option.ToolTip, new Thickness(0, 2, 0, 2), isCaption: false, option.IsSelected, option.Selected, picked);

    /// <summary>面板第一列那個預設；選得上去，取消不掉，所以畫成 radio。</summary>
    /// <remarks>
    /// 取消它不是使用者做得到的狀態——「一個都不選」就是它自己。不擋的症狀與分段開關
    /// 最後一段相同：控制項彈起來了，而條件其實一點都沒變。形狀的理由見
    /// <see cref="SqlAssistChrome.CreateFilterDefaultRow"/>。
    /// </remarks>
    public static SqlFilterRow Empty(SqlFilterOption option, Action<bool>? picked = null) =>
        new(option.Label, option.ToolTip, new Thickness(0, 2, 0, 2), isCaption: false, option.IsSelected,
            option.Selected, picked, sticky: true);

    /// <summary>
    /// 單選鈕的群組名；每一列各一個，等於不讓 WPF 自動互斥。
    /// </summary>
    /// <remarks>
    /// 互斥由模型負責，理由見 <c>SqlAssistChrome.CreateFilterOptionRow</c>。複選的列不讀它。
    /// </remarks>
    public string GroupName { get; } = Guid.NewGuid().ToString("N");

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; }

    public string ToolTip { get; }

    public Thickness Margin { get; }

    public bool IsCaption { get; }

    /// <summary>
    /// 把模型的結果畫出來，不當成一次選取。
    /// </summary>
    /// <remarks>
    /// 走這一支而不是 <see cref="IsSelected"/>：後者會回呼宿主，而宿主正是呼叫這一支的人——
    /// 那條路繞回去會把使用者剛改的條件再改一次。
    /// </remarks>
    public void Sync(bool selected)
    {
        if (_isSelected == selected) return;
        _isSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    /// <summary>勾或取消勾；寫進來的只會是使用者的動作，宿主換選項是換掉整份列清單。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;

            // 勾選框已經彈起來了，所以要把它按回去：不還原的話，畫面上「全部」是沒勾的，
            // 而實際上這個維度仍然一個條件都沒有。
            if (_sticky && !value)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _selected?.Invoke(value);
            _picked?.Invoke(value);
        }
    }
}

/// <summary>過濾面板裡的一個勾選項。</summary>
internal sealed class SqlFilterOption
{
    internal SqlFilterOption(string label, string toolTip, bool isSelected, Action<bool> selected)
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
