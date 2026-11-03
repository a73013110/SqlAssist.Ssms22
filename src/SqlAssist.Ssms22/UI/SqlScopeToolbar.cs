using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>工具列現在收到第幾級；窄窗先收字，再依群組換行。</summary>
internal enum SqlScopeToolbarMode
{
    /// <summary>兩列：過濾按鈕與分段開關在上，搜尋列在下貼著清單；按鈕帶名稱與摘要。</summary>
    Full,

    /// <summary>兩列：過濾按鈕只留圖示與箭頭；名稱與摘要留在 Tooltip，有條件的那幾顆換強調底框。</summary>
    Compact,

    /// <summary>三列：過濾那一層容不下所有群，其中一群換到下一列，搜尋列仍在最下面。</summary>
    Wrapped
}

/// <summary>
/// 範圍工具列：上層是幾群範圍控制，下層是輸入列那一格，貼著清單。SQL Search 與資料字典共用。
/// </summary>
/// <remarks>
/// 範圍在上、輸入在下：先決定「在哪裡找」，再打「找什麼」，打字的地方離結果最近。
/// SQL Memory 同一個順序，理由與取捨見 <c>docs/ui-windows.md</c>。
///
/// 分兩層而不是擠成一列：搜尋框要吃滿剩餘寬度才打得下一段字串，而停靠在右側時可用寬度
/// 只有 300 DIP 上下——三顆過濾按鈕加三段開關排在同一列，搜尋框會被壓到只剩十來個字元。
/// 搜尋列的分工（框內是直接控制、框外右緣是作用在這一份結果的操作）由 <see cref="SqlInputRow"/>
/// 定義，SQL Memory 用的是同一份；多選時選取工具列蓋在同一格上，所以這裡收的是那一整格
/// （<see cref="SqlSelectionBar.Slot"/>），不自己建搜尋列。
///
/// 過濾那一層本身交給共用的 <see cref="SqlFilterBar"/>：分群、兩級分隔線、先收字再依群換行
/// 與列首孤線都在那裡，SQL Memory 用的是同一份。SQL Search 的分段開關是「比對哪裡」那一群，
/// 由呼叫端當最後一群傳進來，與其他幾群沒有分別——特別待遇寫在這裡的話，它換行時的規則會與
/// 別的群分岔，別的視窗也就用不上這一份。
/// </remarks>
internal sealed class SqlScopeToolbar : Panel
{
    private const double RowGap = SqlAssistChrome.Spacing.Tight;

    private readonly FrameworkElement _row;
    private readonly SqlFilterBar _filters;

    /// <param name="filterGroups">
    /// 依<b>問題</b>分好的幾群範圍控制：搜哪裡（伺服器、資料庫）、搜什麼（種類）、比對哪裡。
    /// </param>
    /// <param name="searchRow">下層那一格：輸入列，或疊著選取工具列的那一格。</param>
    public SqlScopeToolbar(FrameworkElement searchRow, IReadOnlyList<IReadOnlyList<FrameworkElement>> filterGroups)
    {
        _row = searchRow ?? throw new ArgumentNullException(nameof(searchRow));
        if (filterGroups is null) throw new ArgumentNullException(nameof(filterGroups));

        var groups = new IReadOnlyList<FrameworkElement>[filterGroups.Count];
        for (var i = 0; i < groups.Length; i++) groups[i] = new List<FrameworkElement>(filterGroups[i]);

        _filters = new SqlFilterBar(groups);

        Children.Add(_filters);
        Children.Add(_row);
    }

    /// <summary>目前收到第幾級；版面回歸測試以它驗門檻。</summary>
    public SqlScopeToolbarMode Mode =>
        _filters.RowCount > 1 ? SqlScopeToolbarMode.Wrapped
        : _filters.IsCompact ? SqlScopeToolbarMode.Compact
        : SqlScopeToolbarMode.Full;

    protected override Size MeasureOverride(Size constraint)
    {
        var available = double.IsInfinity(constraint.Width) || constraint.Width <= 0
            ? double.PositiveInfinity
            : constraint.Width;

        _row.Measure(new Size(available, double.PositiveInfinity));
        _filters.Measure(new Size(available, double.PositiveInfinity));

        var width = double.IsInfinity(available)
            ? Math.Max(_row.DesiredSize.Width, _filters.DesiredSize.Width)
            : available;
        return new Size(width, _filters.DesiredSize.Height + RowGap + _row.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var width = Math.Max(size.Width, 0);
        var first = _filters.DesiredSize.Height;
        _filters.Arrange(new Rect(0, 0, width, first));

        var second = first + RowGap;
        _row.Arrange(new Rect(0, second, width, _row.DesiredSize.Height));
        return size;
    }
}
