using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>工具列現在收到第幾級；窄窗先收字，再依群組換行。</summary>
internal enum SqlSearchToolbarMode
{
    /// <summary>兩列：搜尋列在上，過濾按鈕與分段開關併在第二列，按鈕帶名稱與摘要。</summary>
    Full,

    /// <summary>兩列：過濾按鈕只留圖示與箭頭；名稱與摘要留在 Tooltip 與 chip 列。</summary>
    Compact,

    /// <summary>三列：第二列容不下兩群，分段開關整群換到第三列。</summary>
    Wrapped
}

/// <summary>
/// SQL Search 的工具列：第一層是搜尋框與排序／重新整理，第二層是 filters 與分段開關。
/// </summary>
/// <remarks>
/// 分兩層而不是擠成一列：搜尋框要吃滿剩餘寬度才打得下一段字串，而停靠在右側時可用寬度
/// 只有 300 DIP 上下——三顆過濾按鈕加三段開關排在同一列，搜尋框會被壓到只剩十來個字元。
/// 排序與重新整理留在第一層，它們作用在「這一份結果」而不是「要搜什麼」。
///
/// 自己量測而不是用 <see cref="WrapPanel"/>，理由是換行的單位是<b>群</b>不是控制項：
/// 過濾按鈕與分段開關各自成群，一群要嘛整群在同一列，要嘛整群換到下一列。交給換行面板的話
/// 三顆過濾按鈕會有一顆單獨掉到下一列，而那一列看起來就只是一顆沒有來由的按鈕。
///
/// 先收字再換行：收掉過濾按鈕上的字還讀得到 Tooltip 與 chip 列，多一列卻是永久少看一筆結果。
/// 換行之後過濾按鈕獨佔一整列，字放得下就放回去——那一段沒有回授，不會在同一個寬度上反覆跳。
///
/// 門檻是量出來的內容寬度而不是寫死的數字：同一組按鈕在不同字級與 DPI 下佔的 DIP 不同，
/// 而使用者的停靠面板寬度是以像素決定的。
/// </remarks>
internal sealed class SqlSearchToolbar : Panel
{
    /// <summary>搜尋框無論如何保留的寬度；再窄下去它就不是一個可以打字的欄位了。</summary>
    private const double MinSearchWidth = 96;

    private const double ItemGap = 4;
    private const double SearchGap = 8;
    private const double RowGap = 4;

    private readonly FrameworkElement _search;
    private readonly SqlSearchSegments _segments;
    private readonly IReadOnlyList<SqlFilterFlyout> _filters;

    /// <summary>過濾那一列由左到右要擺的東西：各群的按鈕，群與群之間夾一條分隔線。</summary>
    private readonly IReadOnlyList<FrameworkElement> _filterRowItems;

    /// <summary>分段開關前面那一條；它換到第三列時整條收起，不留一條開頭的線。</summary>
    private readonly Border _segmentDivider = SqlAssistChrome.CreateFilterGroupDivider();
    private readonly IReadOnlyList<FrameworkElement> _trailing;
    private double _searchWidth = MinSearchWidth;
    private double _searchRow;
    private double _filterRow;
    private double _segmentRow;

    /// <param name="filterGroups">
    /// 依<b>問題</b>分好的幾群過濾按鈕：搜哪裡（伺服器、資料庫）、搜什麼（種類）。
    /// 群與群之間由這裡補上共用的分隔線，群內只留 <see cref="ItemGap"/>——
    /// 攤成一份平清單的那一版，使用者看到的是一排可以互相取代的按鈕。
    /// </param>
    /// <param name="trailing">搜尋框右邊的圖示鈕（排序、重新整理）；它們跟搜尋框同一列。</param>
    public SqlSearchToolbar(
        FrameworkElement search,
        SqlSearchSegments segments,
        IReadOnlyList<IReadOnlyList<SqlFilterFlyout>> filterGroups,
        params FrameworkElement[] trailing)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _segments = segments ?? throw new ArgumentNullException(nameof(segments));
        if (filterGroups is null) throw new ArgumentNullException(nameof(filterGroups));
        _trailing = trailing ?? throw new ArgumentNullException(nameof(trailing));

        var filters = new List<SqlFilterFlyout>();
        var items = new List<FrameworkElement>();
        foreach (var group in filterGroups)
        {
            if (items.Count > 0) items.Add(SqlAssistChrome.CreateFilterGroupDivider());
            foreach (var filter in group) { filters.Add(filter); items.Add(filter); }
        }

        _filters = filters;
        _filterRowItems = items;

        Children.Add(search);
        foreach (var element in _trailing) Children.Add(element);
        foreach (var item in _filterRowItems) Children.Add(item);
        Children.Add(_segmentDivider);
        Children.Add(segments);
    }

    /// <summary>目前收到第幾級；版面回歸測試以它驗門檻。</summary>
    public SqlSearchToolbarMode Mode { get; private set; } = SqlSearchToolbarMode.Full;

    protected override Size MeasureOverride(Size constraint)
    {
        var available = double.IsInfinity(constraint.Width) || constraint.Width <= 0 ? 0 : constraint.Width;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);

        _segments.Measure(unbounded);
        var segments = _segments.DesiredSize.Width;

        // 分隔線先照「看得見」量一次：它的寬度要算進門檻，而門檻還沒決定要不要收起它。
        _segmentDivider.Visibility = Visibility.Visible;
        _segmentDivider.Measure(unbounded);
        var divider = _segmentDivider.DesiredSize.Width;

        // 兩種狀態各量一次：要先知道帶字的那一份放不放得下，才決定收不收字。
        var full = MeasureFilters(compact: false, unbounded);
        var compact = MeasureFilters(compact: true, unbounded);

        // 寬度還沒決定（量測在無限寬度下）時一律照完整版算；收起來的按鈕量出來的寬度
        // 會讓第一次排版就停在窄版上，而視窗其實很寬。
        Mode = available <= 0 || full + divider + segments <= available ? SqlSearchToolbarMode.Full
            : compact + divider + segments <= available ? SqlSearchToolbarMode.Compact
            : SqlSearchToolbarMode.Wrapped;

        // 換行之後過濾按鈕獨佔一整列；那一列放得下字就放回去。
        var wrapped = Mode == SqlSearchToolbarMode.Wrapped;
        // 分段開關換到第三列時，它前面那一條分隔線會變成第三列開頭的一條孤線，所以整條收起。
        if (wrapped)
        {
            _segmentDivider.Visibility = Visibility.Collapsed;
            _segmentDivider.Measure(unbounded);
        }

        var filters = MeasureFilters(
            compact: Mode == SqlSearchToolbarMode.Compact || (wrapped && full > available),
            unbounded);

        var tail = 0d;
        foreach (var element in _trailing)
        {
            element.Measure(unbounded);
            tail += ItemGap + element.DesiredSize.Width;
        }

        _searchWidth = available <= 0 ? MinSearchWidth : Math.Max(MinSearchWidth, available - SearchGap - tail);
        _search.Measure(new Size(_searchWidth, double.PositiveInfinity));

        _searchRow = _search.DesiredSize.Height;
        foreach (var element in _trailing) _searchRow = Math.Max(_searchRow, element.DesiredSize.Height);

        _filterRow = 0;
        foreach (var item in _filterRowItems) _filterRow = Math.Max(_filterRow, item.DesiredSize.Height);

        if (wrapped) _segmentRow = _segments.DesiredSize.Height;
        else { _filterRow = Math.Max(_filterRow, _segments.DesiredSize.Height); _segmentRow = 0; }

        var width = available > 0
            ? available
            : Math.Max(_searchWidth + SearchGap + tail, filters + _segmentDivider.DesiredSize.Width + segments);
        var height = _searchRow + RowGap + _filterRow + (wrapped ? RowGap + _segmentRow : 0);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var x = PlaceAt(_search, 0, 0, _searchRow, _searchWidth) + SearchGap;
        foreach (var element in _trailing) x = PlaceAt(element, x, 0, _searchRow) + ItemGap;

        var second = _searchRow + RowGap;
        x = 0;
        for (var index = 0; index < _filterRowItems.Count; index++)
        {
            var item = _filterRowItems[index];
            x = PlaceAt(item, x, second, _filterRow);
            if (index + 1 < _filterRowItems.Count) x += GapBetween(item, _filterRowItems[index + 1]);
        }

        // 收起來的那一條寬度是 0，但仍要排掉：沒有排過的子項在 WPF 裡是未定義的版面狀態。
        x = PlaceAt(_segmentDivider, x, second, _filterRow);

        if (Mode == SqlSearchToolbarMode.Wrapped) PlaceAt(_segments, 0, second + _filterRow + RowGap, _segmentRow);
        else PlaceAt(_segments, x, second, _filterRow);

        return size;
    }

    /// <summary>
    /// 兩顆之間要留多少：分隔線自己的 <see cref="FrameworkElement.Margin"/> 就是群距，
    /// 所以它前後不再加一次群內間距，否則群與群之間會比設計的寬出兩個 <see cref="ItemGap"/>。
    /// </summary>
    private static double GapBetween(FrameworkElement left, FrameworkElement right) =>
        left is SqlFilterFlyout && right is SqlFilterFlyout ? ItemGap : 0;

    /// <summary>量一次過濾那一列，回傳整列的寬度（含群內間距與分隔線，不含它後面的間距）。</summary>
    private double MeasureFilters(bool compact, Size unbounded)
    {
        foreach (var item in _filterRowItems)
        {
            if (item is SqlFilterFlyout filter) filter.IsCompact = compact;
            item.Measure(unbounded);
        }

        var width = 0d;
        for (var index = 0; index < _filterRowItems.Count; index++)
        {
            var item = _filterRowItems[index];
            width += item.DesiredSize.Width;
            if (index + 1 < _filterRowItems.Count) width += GapBetween(item, _filterRowItems[index + 1]);
        }

        return width;
    }

    /// <summary>把一個控制項擺進某一列，並回傳它的右緣。</summary>
    /// <remarks>
    /// 同一條視覺中心線：高度不同的控制項在列裡垂直置中，不是各自貼著上緣。
    /// 置中用排版位置而不是每個控制項自己的 <c>VerticalAlignment</c>，
    /// 否則字級或 DPI 一變就要回頭調每一個外距。
    /// </remarks>
    private static double PlaceAt(FrameworkElement element, double x, double top, double rowHeight, double? width = null)
    {
        var used = width ?? element.DesiredSize.Width;
        var height = Math.Min(element.DesiredSize.Height, rowHeight);
        element.Arrange(new Rect(x, top + (rowHeight - height) / 2, used, height));
        // 回傳的是這一個的右緣，間距由呼叫端依前後是什麼決定——分隔線前後不留，兩顆按鈕之間才留。
        return x + used;
    }
}
