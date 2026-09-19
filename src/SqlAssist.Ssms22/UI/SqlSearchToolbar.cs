using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>工具列現在收到第幾級；窄窗先收字，再收行。</summary>
internal enum SqlSearchToolbarMode
{
    /// <summary>一列，過濾按鈕帶名稱與摘要。</summary>
    Full,

    /// <summary>一列，過濾按鈕只留圖示與箭頭；名稱與摘要留在 Tooltip 與 chip 列。</summary>
    Compact,

    /// <summary>兩列，分段開關自己一列；它是常駐可見的，收掉字就等於收掉它。</summary>
    Stacked
}

/// <summary>
/// SQL Search 的工具列：搜尋框吃滿剩餘空間，過濾按鈕與分段開關靠右排。
/// </summary>
/// <remarks>
/// 自己量測而不是用 <see cref="WrapPanel"/>，理由是收縮的先後有優先級：搜尋框與分段開關
/// 一定要看得見，先讓的是過濾按鈕上的字（Tooltip 與 chip 列仍讀得到），再不夠才讓分段開關
/// 換到第二列。交給換行面板的話，先掉下去的會是排在最後的分段開關，而那正是切換最頻繁的一項。
///
/// 兩道門檻都是<b>版面寬度</b>而不是字數：同一組按鈕在 200% DPI 下佔的 DIP 一樣，
/// 但實際像素是兩倍，而使用者的停靠面板寬度是以像素決定的。
/// </remarks>
internal sealed class SqlSearchToolbar : Panel
{
    /// <summary>窄於這個寬度就收起過濾按鈕上的名稱與摘要。</summary>
    public const double CompactWidth = 520;

    /// <summary>窄於這個寬度就讓分段開關換到第二列。</summary>
    public const double StackedWidth = 400;

    /// <summary>搜尋框無論如何保留的寬度；再窄下去它就不是一個可以打字的欄位了。</summary>
    private const double MinSearchWidth = 96;

    private const double ItemGap = 4;
    private const double SearchGap = 8;
    private const double RowGap = 4;

    private readonly FrameworkElement _search;
    private readonly SqlSearchSegments _segments;
    private readonly IReadOnlyList<SqlSearchFilterButton> _filters;
    private readonly IReadOnlyList<FrameworkElement> _trailing;
    private double _searchWidth = MinSearchWidth;
    private double _firstRow;
    private double _secondRow;

    /// <param name="trailing">分段開關右邊的圖示鈕（排序、重新整理）；換到第二列時跟著它走。</param>
    public SqlSearchToolbar(
        FrameworkElement search,
        SqlSearchSegments segments,
        IReadOnlyList<SqlSearchFilterButton> filters,
        params FrameworkElement[] trailing)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _segments = segments ?? throw new ArgumentNullException(nameof(segments));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _trailing = trailing ?? throw new ArgumentNullException(nameof(trailing));

        Children.Add(search);
        foreach (var filter in _filters) Children.Add(filter);
        Children.Add(segments);
        foreach (var element in _trailing) Children.Add(element);
    }

    /// <summary>目前收到第幾級；版面回歸測試以它驗門檻。</summary>
    public SqlSearchToolbarMode Mode { get; private set; } = SqlSearchToolbarMode.Full;

    protected override Size MeasureOverride(Size constraint)
    {
        var available = double.IsInfinity(constraint.Width) || constraint.Width <= 0 ? 0 : constraint.Width;

        // 寬度還沒決定（量測在無限寬度下）時一律照完整版算；收起來的按鈕量出來的寬度
        // 會讓第一次排版就停在窄版上，而視窗其實很寬。
        Mode = available <= 0 ? SqlSearchToolbarMode.Full
            : available < StackedWidth ? SqlSearchToolbarMode.Stacked
            : available < CompactWidth ? SqlSearchToolbarMode.Compact
            : SqlSearchToolbarMode.Full;

        foreach (var filter in _filters) filter.IsCompact = Mode != SqlSearchToolbarMode.Full;

        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _segments.Measure(unbounded);

        var trailing = 0d;
        foreach (var filter in _filters)
        {
            filter.Measure(unbounded);
            trailing += filter.DesiredSize.Width + ItemGap;
        }

        var tail = 0d;
        foreach (var element in _trailing)
        {
            element.Measure(unbounded);
            tail += element.DesiredSize.Width + ItemGap;
        }

        var stacked = Mode == SqlSearchToolbarMode.Stacked;
        var inlineSegments = stacked ? 0 : _segments.DesiredSize.Width + ItemGap + tail;

        _searchWidth = available <= 0
            ? MinSearchWidth
            : Math.Max(MinSearchWidth, available - SearchGap - trailing - inlineSegments);

        _search.Measure(new Size(_searchWidth, double.PositiveInfinity));

        _firstRow = _search.DesiredSize.Height;
        foreach (var filter in _filters) _firstRow = Math.Max(_firstRow, filter.DesiredSize.Height);

        var tailHeight = _segments.DesiredSize.Height;
        foreach (var element in _trailing) tailHeight = Math.Max(tailHeight, element.DesiredSize.Height);

        if (stacked) _secondRow = tailHeight;
        else { _firstRow = Math.Max(_firstRow, tailHeight); _secondRow = 0; }

        var width = available > 0 ? available : _searchWidth + SearchGap + trailing + inlineSegments;
        var height = _firstRow + (stacked ? RowGap + _secondRow : 0);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var x = PlaceAt(_search, 0, 0, _firstRow, _searchWidth) + (SearchGap - ItemGap);
        foreach (var filter in _filters) x = PlaceAt(filter, x, 0, _firstRow);

        if (Mode == SqlSearchToolbarMode.Stacked)
        {
            var second = PlaceAt(_segments, 0, _firstRow + RowGap, _secondRow);
            foreach (var element in _trailing) second = PlaceAt(element, second, _firstRow + RowGap, _secondRow);
            return size;
        }

        x = PlaceAt(_segments, x, 0, _firstRow);
        foreach (var element in _trailing) x = PlaceAt(element, x, 0, _firstRow);
        return size;
    }

    /// <summary>把一個控制項擺進某一列，並回傳下一個控制項的起點（含間距）。</summary>
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
        return x + used + ItemGap;
    }
}
