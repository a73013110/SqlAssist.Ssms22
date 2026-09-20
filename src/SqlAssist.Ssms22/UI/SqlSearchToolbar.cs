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

    /// <summary>三列：第二列容不下所有群，其中一群換到第三列。</summary>
    Wrapped
}

/// <summary>
/// SQL Search 的工具列：第一層是搜尋框與排序／重新整理，第二層起是 filters 與分段開關。
/// </summary>
/// <remarks>
/// 分兩層而不是擠成一列：搜尋框要吃滿剩餘寬度才打得下一段字串，而停靠在右側時可用寬度
/// 只有 300 DIP 上下——三顆過濾按鈕加三段開關排在同一列，搜尋框會被壓到只剩十來個字元。
/// 排序與重新整理留在第一層，它們作用在「這一份結果」而不是「要搜什麼」。
///
/// 第二層本身交給共用的 <see cref="SqlFilterBar"/>：分群、兩級分隔線、先收字再依群換行
/// 與列首孤線都在那裡，SQL Memory 用的是同一份。分段開關是「比對哪裡」那一群，
/// 對那一層來說與其他幾群沒有分別——特別待遇寫在這裡的話，它換行時的規則會與別的群分岔。
/// </remarks>
internal sealed class SqlSearchToolbar : Panel
{
    /// <summary>搜尋框無論如何保留的寬度；再窄下去它就不是一個可以打字的欄位了。</summary>
    private const double MinSearchWidth = 96;

    private const double ItemGap = 4;
    private const double SearchGap = 8;
    private const double RowGap = 4;

    private readonly FrameworkElement _search;
    private readonly SqlFilterBar _filters;
    private readonly IReadOnlyList<FrameworkElement> _trailing;
    private double _searchWidth = MinSearchWidth;
    private double _searchRow;

    /// <param name="filterGroups">
    /// 依<b>問題</b>分好的幾群過濾按鈕：搜哪裡（伺服器、資料庫）、搜什麼（種類）。
    /// </param>
    /// <param name="trailing">搜尋框右邊的圖示鈕（排序、重新整理）；它們跟搜尋框同一列。</param>
    public SqlSearchToolbar(
        FrameworkElement search,
        SqlSearchSegments segments,
        IReadOnlyList<IReadOnlyList<SqlFilterFlyout>> filterGroups,
        params FrameworkElement[] trailing)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        if (segments is null) throw new ArgumentNullException(nameof(segments));
        if (filterGroups is null) throw new ArgumentNullException(nameof(filterGroups));
        _trailing = trailing ?? throw new ArgumentNullException(nameof(trailing));

        var groups = new List<IReadOnlyList<FrameworkElement>>(filterGroups.Count + 1);
        foreach (var group in filterGroups) groups.Add(new List<FrameworkElement>(group));
        groups.Add(new FrameworkElement[] { segments });

        _filters = new SqlFilterBar(groups.ToArray());

        Children.Add(search);
        foreach (var element in _trailing) Children.Add(element);
        Children.Add(_filters);
    }

    /// <summary>目前收到第幾級；版面回歸測試以它驗門檻。</summary>
    public SqlSearchToolbarMode Mode =>
        _filters.RowCount > 1 ? SqlSearchToolbarMode.Wrapped
        : _filters.IsCompact ? SqlSearchToolbarMode.Compact
        : SqlSearchToolbarMode.Full;

    protected override Size MeasureOverride(Size constraint)
    {
        var available = double.IsInfinity(constraint.Width) || constraint.Width <= 0 ? 0 : constraint.Width;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);

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

        _filters.Measure(new Size(available <= 0 ? double.PositiveInfinity : available, double.PositiveInfinity));

        var width = available > 0 ? available : Math.Max(_searchWidth + SearchGap + tail, _filters.DesiredSize.Width);
        return new Size(width, _searchRow + RowGap + _filters.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var x = PlaceAt(_search, 0, 0, _searchRow, _searchWidth) + SearchGap;
        foreach (var element in _trailing) x = PlaceAt(element, x, 0, _searchRow) + ItemGap;

        var second = _searchRow + RowGap;
        _filters.Arrange(new Rect(0, second, Math.Max(size.Width, 0), Math.Max(size.Height - second, 0)));
        return size;
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
        return x + used;
    }
}
