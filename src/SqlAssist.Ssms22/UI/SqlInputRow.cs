using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 工具窗最上面那一列：輸入框吃滿剩餘寬度，右邊接幾顆作用在「這一份結果」的圖示鈕。
/// </summary>
/// <remarks>
/// 這一列的分工只有一條界線，SQL Search 與 SQL Memory 共用：修飾<b>這個字串怎麼比</b>的
/// 直接控制（大小寫、全字、清除）進<see cref="SqlAssistChrome.CreateInputBar"/>的框裡，
/// 作用在<b>這一份結果</b>的（排序、重新整理、目前連線）留在框外的右緣。縮小搜尋範圍的
/// 篩選則不在這一列上，它們在下一層的 <see cref="SqlFilterBar"/>。
///
/// 三種東西混排在同一個地方的症狀是使用者分不出哪一顆會改變「找到什麼」、哪一顆只改變
/// 「怎麼看這一份」；而框裡那幾顆既然常駐可見，就不必在下面的已選條件列再畫一顆 chip
/// 說同一件事。
///
/// 自己量測而不是用 <see cref="DockPanel"/>：輸入框要吃滿剩餘寬度，而右邊那幾顆是固定寬的，
/// 讓得起的只有輸入框。它再窄也保留 <see cref="MinInputWidth"/>，再窄下去就不是一個
/// 可以打字的欄位了。
/// </remarks>
internal sealed class SqlInputRow : Panel
{
    /// <summary>輸入框無論如何保留的寬度。</summary>
    public const double MinInputWidth = 96;

    /// <summary>右邊那幾顆彼此之間的間距。</summary>
    private const double ItemGap = 4;

    /// <summary>輸入框與右邊那一組之間的間距；比組內大一階，讀得出是兩件事。</summary>
    private const double InputGap = 8;

    private readonly FrameworkElement _input;
    private readonly IReadOnlyList<FrameworkElement> _trailing;
    private double _inputWidth = MinInputWidth;

    /// <param name="input">搜尋列本身（<see cref="SqlAssistChrome.CreateInputBar"/>），不自帶外距。</param>
    /// <param name="trailing">框外右緣的圖示鈕，依陣列順序由左往右；收起的那幾顆連間距一起讓開。</param>
    public SqlInputRow(FrameworkElement input, params FrameworkElement[] trailing)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _trailing = trailing ?? throw new ArgumentNullException(nameof(trailing));

        Children.Add(_input);
        foreach (var element in _trailing) Children.Add(element);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var available = double.IsInfinity(constraint.Width) || constraint.Width <= 0 ? 0 : constraint.Width;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);

        var tail = 0d;
        var height = 0d;
        foreach (var element in _trailing)
        {
            element.Measure(unbounded);
            // 收起的那一顆連它前面那一段間距一起讓開；只跳過元素的話，收起之後右邊會留一段空白。
            if (element.Visibility == Visibility.Collapsed) continue;
            tail += (tail > 0 ? ItemGap : InputGap) + element.DesiredSize.Width;
            height = Math.Max(height, element.DesiredSize.Height);
        }

        _inputWidth = available <= 0 ? MinInputWidth : Math.Max(MinInputWidth, available - tail);
        _input.Measure(new Size(_inputWidth, double.PositiveInfinity));
        height = Math.Max(height, _input.DesiredSize.Height);

        return new Size(available > 0 ? available : _inputWidth + tail, height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var row = Math.Max(size.Height, 0);
        var x = PlaceAt(_input, 0, row, _inputWidth) + InputGap;
        var first = true;
        foreach (var element in _trailing)
        {
            if (element.Visibility == Visibility.Collapsed) continue;
            if (!first) x += ItemGap;
            x = PlaceAt(element, x, row);
            first = false;
        }

        return size;
    }

    /// <summary>把一個控制項擺進這一列，並回傳它的右緣。</summary>
    /// <remarks>
    /// 同一條視覺中心線：高度不同的控制項在列裡垂直置中，不是各自貼著上緣。置中用排版位置
    /// 而不是每個控制項自己的 <c>VerticalAlignment</c>，否則字級或 DPI 一變就要回頭調每一個外距。
    /// </remarks>
    private static double PlaceAt(FrameworkElement element, double x, double rowHeight, double? width = null)
    {
        var used = width ?? element.DesiredSize.Width;
        var height = Math.Min(element.DesiredSize.Height, rowHeight);
        element.Arrange(new Rect(x, (rowHeight - height) / 2, used, height));
        return x + used;
    }
}
