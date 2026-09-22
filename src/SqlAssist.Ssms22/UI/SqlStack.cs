using System;
using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 垂直堆疊，子元素之間由這一層給同一段間距。
/// </summary>
/// <remarks>
/// 不用 <see cref="StackPanel"/> 加每個子元素自己的 margin：那等於把一段間距拆給相鄰兩個
/// 元素各出一半，而這裡堆的那幾塊（已選條件列、主機狀態、整組搜尋與篩選）隨時會
/// <see cref="Visibility.Collapsed"/>。各出一半的那一版收起之後留下半格空白，而收起正是
/// 它們的常態——沒有已選條件、沒有主機訊息都是預設狀態。
///
/// 間距只加在**看得見**的兩塊之間，所以收起的那一塊連同它前面那一段一起讓開。
/// </remarks>
internal sealed class SqlStack : Panel
{
    private readonly double _gap;

    public SqlStack(double gap) => _gap = gap;

    protected override Size MeasureOverride(Size constraint)
    {
        var width = 0.0;
        var height = 0.0;
        var first = true;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            child.Measure(new Size(constraint.Width, double.PositiveInfinity));
            if (!first) height += _gap;
            first = false;
            height += child.DesiredSize.Height;
            width = Math.Max(width, child.DesiredSize.Width);
        }

        return new Size(double.IsInfinity(constraint.Width) ? width : constraint.Width, height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var width = Math.Max(size.Width, 0);
        var top = 0.0;
        var first = true;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            if (!first) top += _gap;
            first = false;
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(0, top, width, height));
            top += height;
        }

        return size;
    }
}
