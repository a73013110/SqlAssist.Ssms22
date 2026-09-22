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
/// SQL Search 的工具列：第一層是共用的 <see cref="SqlInputRow"/>，第二層起是 filters 與分段開關。
/// </summary>
/// <remarks>
/// 分兩層而不是擠成一列：搜尋框要吃滿剩餘寬度才打得下一段字串，而停靠在右側時可用寬度
/// 只有 300 DIP 上下——三顆過濾按鈕加三段開關排在同一列，搜尋框會被壓到只剩十來個字元。
/// 第一層的分工（框內是直接控制、框外右緣是作用在這一份結果的操作）由 <see cref="SqlInputRow"/>
/// 定義，SQL Memory 用的是同一份。
///
/// 第二層本身交給共用的 <see cref="SqlFilterBar"/>：分群、兩級分隔線、先收字再依群換行
/// 與列首孤線都在那裡，SQL Memory 用的是同一份。分段開關是「比對哪裡」那一群，
/// 對那一層來說與其他幾群沒有分別——特別待遇寫在這裡的話，它換行時的規則會與別的群分岔。
/// </remarks>
internal sealed class SqlSearchToolbar : Panel
{
    private const double RowGap = SqlAssistChrome.Spacing.Tight;

    private readonly SqlInputRow _row;
    private readonly SqlFilterBar _filters;

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
        if (segments is null) throw new ArgumentNullException(nameof(segments));
        if (filterGroups is null) throw new ArgumentNullException(nameof(filterGroups));

        var groups = new List<IReadOnlyList<FrameworkElement>>(filterGroups.Count + 1);
        foreach (var group in filterGroups) groups.Add(new List<FrameworkElement>(group));
        groups.Add(new FrameworkElement[] { segments });

        _row = new SqlInputRow(search, trailing);
        _filters = new SqlFilterBar(groups.ToArray());

        Children.Add(_row);
        Children.Add(_filters);
    }

    /// <summary>目前收到第幾級；版面回歸測試以它驗門檻。</summary>
    public SqlSearchToolbarMode Mode =>
        _filters.RowCount > 1 ? SqlSearchToolbarMode.Wrapped
        : _filters.IsCompact ? SqlSearchToolbarMode.Compact
        : SqlSearchToolbarMode.Full;

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
        return new Size(width, _row.DesiredSize.Height + RowGap + _filters.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var width = Math.Max(size.Width, 0);
        var first = _row.DesiredSize.Height;
        _row.Arrange(new Rect(0, 0, width, first));

        var second = first + RowGap;
        _filters.Arrange(new Rect(0, second, width, Math.Max(size.Height - second, 0)));
        return size;
    }
}
