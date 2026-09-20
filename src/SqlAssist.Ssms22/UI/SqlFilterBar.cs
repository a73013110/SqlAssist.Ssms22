using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 工具列上那一列篩選：依<b>群</b>排列、依群換行，群間與群內各畫一級分隔線。
/// </summary>
/// <remarks>
/// SQL Memory 與 SQL Search 共用這一份。兩邊各寫一次的症狀是同一條分隔線在兩個視窗上
/// 出現在不同位置，而換行的單位也會分岔——其中一邊交給 <see cref="WrapPanel"/>
/// 的話，三顆篩選裡會有一顆單獨掉到下一列，而那一列看起來就只是一顆沒有來由的按鈕。
///
/// 換行的單位是<b>群</b>：一群要嘛整群在同一列，要嘛整群換到下一列。落在列首的那一群
/// 收起它前面那一條，否則列首會留一條孤線。
///
/// 先收字再換行：收掉按鈕上的字還讀得到 Tooltip 與 chip 列，多一列卻是永久少看一筆結果。
/// 收字只對 <see cref="SqlFilterFlyout"/> 有意義（它自己有 <see cref="SqlFilterFlyout.IsCompact"/>），
/// 分段開關與 pill 這類常駐控制項不降級。
///
/// 收起來（<see cref="Visibility.Collapsed"/>）的控制項連同它的分隔線一起跳過：SQL Memory 的
/// 「狀態」「期間」只屬於 History，切到 Favorites 之後那兩群整群消失，而留著的線會變成列首的孤線。
///
/// 寬度在量測時就算進每一顆自己的快取，排版只是照著擺：兩段各量一次的下場是門檻差了一條
/// 分隔線的寬度，而那正好是「量出來剛好放得下、排出來卻換了行」的那一個 DIP。
/// </remarks>
internal sealed class SqlFilterBar : Panel
{
    private const double RowGap = 4;

    private readonly IReadOnlyList<Group> _groups;
    private readonly List<Line> _lines = new();

    /// <param name="groups">
    /// 依<b>問題</b>分好的幾群控制項：回答同一個問題的是一群。
    /// 群與群之間、群內每兩顆之間的分隔線都由這裡補上，呼叫端不自己加。
    /// </param>
    public SqlFilterBar(params IReadOnlyList<FrameworkElement>[] groups)
    {
        if (groups is null) throw new ArgumentNullException(nameof(groups));

        var built = new List<Group>(groups.Length);

        foreach (var members in groups)
        {
            if (members is null) throw new ArgumentNullException(nameof(groups));

            var items = new List<Item>(members.Count);

            foreach (var member in members)
            {
                // 每一顆自己帶前面那一條：群首那一顆帶的是群間那一條，群內其餘帶矮一截的那一條。
                var divider = items.Count == 0
                    ? SqlAssistChrome.CreateFilterGroupDivider()
                    : SqlAssistChrome.CreateFilterItemDivider();

                items.Add(new Item(member, divider));
                Children.Add(divider);
                Children.Add(member);
            }

            if (items.Count > 0) built.Add(new Group(items));
        }

        _groups = built;
    }

    /// <summary>目前收了字沒有；版面回歸測試以它驗門檻。</summary>
    public bool IsCompact { get; private set; }

    /// <summary>目前排出來幾列；一列放得下時是 1。</summary>
    public int RowCount { get; private set; } = 1;

    protected override Size MeasureOverride(Size constraint)
    {
        var available = double.IsInfinity(constraint.Width) || constraint.Width <= 0 ? 0 : constraint.Width;

        // 寬度還沒決定（量測在無限寬度下）時一律照完整版算；收起來的按鈕量出來的寬度
        // 會讓第一次排版就停在窄版上，而視窗其實很寬。
        var full = MeasureItems(compact: false);
        IsCompact = available > 0 && full > available;
        if (IsCompact) MeasureItems(compact: true);

        return Wrap(available);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var top = 0d;

        foreach (var line in _lines)
        {
            var x = 0d;

            foreach (var group in line.Groups)
            {
                foreach (var item in group.Items)
                {
                    // 收起來的子項也要排：沒有排過的子項在 WPF 裡是未定義的版面狀態。
                    x = Place(item.Divider, x, top, line.Height);
                    x = Place(item.Element, x, top, line.Height);
                }
            }

            top += line.Height + RowGap;
        }

        return size;
    }

    /// <summary>量一次全部並把寬度留在每一顆自己身上；回傳排成一列要多寬（不含列首那一條）。</summary>
    private double MeasureItems(bool compact)
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);

        foreach (var group in _groups)
        {
            foreach (var item in group.Items)
            {
                if (item.Element is SqlFilterFlyout flyout) flyout.IsCompact = compact;

                // 分隔線每一輪都先當成看得見來量：上一輪收起來的那幾條量出來是 0，
                // 拿它算門檻會讓同一個寬度下這一輪與上一輪換行的結論不一樣。
                Show(item.Divider, visible: true);
                item.Element.Measure(unbounded);
                item.Divider.Measure(unbounded);
                item.Cache();
            }
        }

        var width = 0d;
        var first = true;

        foreach (var group in _groups)
        {
            var groupWidth = group.Width(includeLeadingDivider: !first);
            if (groupWidth <= 0) continue;

            width += groupWidth;
            first = false;
        }

        return width;
    }

    /// <summary>決定哪一群落在哪一列，並收起每一列列首那一條分隔線。</summary>
    private Size Wrap(double available)
    {
        _lines.Clear();
        var line = new Line();
        var widest = 0d;
        var total = 0d;

        foreach (var group in _groups)
        {
            if (group.Width(includeLeadingDivider: true) <= 0)
            {
                // 整群收起來時連它的分隔線一起收，並照樣進這一列：沒有排過的子項在 WPF 裡
                // 是未定義的版面狀態，而它的寬度是 0，排進來不影響任何位置。
                group.SetLeadingDivider(visible: false);
                line.Add(group, 0);
                continue;
            }

            var lead = line.IsEmpty;
            var width = group.Width(!lead);

            // 一列都還沒放東西時不換行：一群本身就比整列寬的話，換行只是多留一列空白。
            if (!lead && available > 0 && line.Width + width > available)
            {
                widest = Math.Max(widest, line.Width);
                total += line.Height + RowGap;
                _lines.Add(line);
                line = new Line();
                width = group.Width(includeLeadingDivider: false);
            }

            group.SetLeadingDivider(visible: !line.IsEmpty);
            line.Add(group, width);
        }

        _lines.Add(line);
        widest = Math.Max(widest, line.Width);
        total += line.Height;
        RowCount = _lines.Count;

        return new Size(available > 0 ? available : widest, total);
    }

    /// <summary>只在真的要改的時候寫：相同的值寫回去仍會讓這一層重新量測一輪。</summary>
    private static void Show(FrameworkElement element, bool visible)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != target) element.Visibility = target;
    }

    private static double Place(FrameworkElement element, double x, double top, double rowHeight)
    {
        var width = element.Visibility == Visibility.Visible ? element.DesiredSize.Width : 0;

        // 同一條視覺中心線：高度不同的控制項在列裡垂直置中，不是各自貼著上緣。置中用排版位置
        // 而不是每個控制項自己的 VerticalAlignment，否則字級或 DPI 一變就要回頭調每一個外距。
        var height = Math.Min(element.DesiredSize.Height, rowHeight);
        element.Arrange(new Rect(x, top + (rowHeight - height) / 2, width, height));
        return x + width;
    }

    private sealed class Item
    {
        internal Item(FrameworkElement element, FrameworkElement divider)
        {
            Element = element;
            Divider = divider;
        }

        internal FrameworkElement Element { get; }

        internal FrameworkElement Divider { get; }

        internal bool IsVisible => Element.Visibility == Visibility.Visible;

        /// <summary>量到的寬度；分隔線被收起來之後它自己量出來是 0，而門檻仍要算得出來。</summary>
        internal double ElementWidth { get; private set; }

        internal double DividerWidth { get; private set; }

        internal double Height { get; private set; }

        internal void Cache()
        {
            ElementWidth = Element.DesiredSize.Width;
            DividerWidth = Divider.DesiredSize.Width;
            Height = Element.DesiredSize.Height;
        }
    }

    private sealed class Group
    {
        internal Group(IReadOnlyList<Item> items) => Items = items;

        internal IReadOnlyList<Item> Items { get; }

        /// <summary>這一群排成一列要多寬；整群收起來時回 0，連它的分隔線都不佔位。</summary>
        internal double Width(bool includeLeadingDivider)
        {
            var width = 0d;
            var first = true;

            foreach (var item in Items)
            {
                if (!item.IsVisible) continue;

                if (!first || includeLeadingDivider) width += item.DividerWidth;
                width += item.ElementWidth;
                first = false;
            }

            return first ? 0 : width;
        }

        internal double Height()
        {
            var height = 0d;
            foreach (var item in Items) if (item.IsVisible) height = Math.Max(height, item.Height);
            return height;
        }

        /// <summary>
        /// 群首那一條看不看得見。
        /// </summary>
        /// <remarks>
        /// 收起來的那幾顆前面那一條一律跟著收：留著的話，切到 Favorites 之後列首會多一條
        /// 指著一個不在畫面上的群的線。
        /// </remarks>
        internal void SetLeadingDivider(bool visible)
        {
            var first = true;

            foreach (var item in Items)
            {
                if (!item.IsVisible) { Show(item.Divider, visible: false); continue; }

                Show(item.Divider, !first || visible);
                first = false;
            }
        }
    }

    private sealed class Line
    {
        private readonly List<Group> _groups = new();

        internal IReadOnlyList<Group> Groups => _groups;

        /// <summary>
        /// 這一列還沒有看得見的東西。
        /// </summary>
        /// <remarks>
        /// 看寬度而不是看收了幾群：整群收起來的那幾群寬度是 0，把它們算成「這一列有東西了」
        /// 的話，接在後面的第一群會畫出它前面那一條，而列首就從一條孤線開始。
        /// </remarks>
        internal bool IsEmpty => Width <= 0;

        internal double Width { get; private set; }

        internal double Height { get; private set; }

        internal void Add(Group group, double width)
        {
            _groups.Add(group);
            Width += width;
            Height = Math.Max(Height, group.Height());
        }
    }
}
