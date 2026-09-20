using System.Windows;

namespace SqlAssist.Ssms22.UI;

/// <summary>清單列的寬度模式；窄版把低優先的東西降級，不換列也不把內容擠掉。</summary>
internal enum SqlRowWidth
{
    /// <summary>放得下完整的膠囊文字與整組操作。</summary>
    Regular,

    /// <summary>工具窗停在右側那種寬度：連線膠囊只剩圖示，次要操作收進 overflow。</summary>
    Narrow,
}

/// <summary>
/// 清單列的寬度模式：由宿主量一次，樣板的降級規則讀它。
/// </summary>
/// <remarks>
/// 模式看的是<b>可用寬度</b>而不是內容量得多寬：後者每降一級就讓內容縮一次，縮完又放得下，
/// 於是同一次排版在兩個模式之間來回，畫面上是膠囊不停閃動。門檻另留
/// <see cref="Hysteresis"/>，使用者把工具窗拖在臨界寬度上時只換一次。
///
/// 屬性是<b>可繼承</b>的附加屬性，掛在清單上：一份寬度全部的列共用，不是每一列各量一次；
/// 而樣板裡的 trigger 讀得到它，也不必認得是哪一種宿主——沒有人設過就是
/// <see cref="SqlRowWidth.Regular"/>，樣板放進任何容器都還畫得出來。
/// </remarks>
internal static class SqlRowLayout
{
    /// <summary>窄到這個 DIP 以下就降級；工具窗停在右側時整列約 300 DIP。</summary>
    internal const double NarrowWidth = 340;

    /// <summary>回到一般版要比降級再寬這麼多 DIP。</summary>
    private const double Hysteresis = 24;

    public static readonly DependencyProperty WidthModeProperty = DependencyProperty.RegisterAttached(
        "WidthMode", typeof(SqlRowWidth), typeof(SqlRowLayout),
        new FrameworkPropertyMetadata(SqlRowWidth.Regular, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetWidthMode(DependencyObject element, SqlRowWidth value) =>
        element.SetValue(WidthModeProperty, value);

    public static SqlRowWidth GetWidthMode(DependencyObject element) =>
        (SqlRowWidth)element.GetValue(WidthModeProperty);

    /// <summary>掛在清單（或任何承載列的容器）上；寬度一變就重算，底下的列跟著換模式。</summary>
    public static void Track(FrameworkElement host)
    {
        host.SizeChanged += (_, args) => { if (args.WidthChanged) Apply(host, args.NewSize.Width); };
        Apply(host, host.ActualWidth);
    }

    /// <summary>還沒排版過（寬度是 0）時不動：那不是「很窄」，是還不知道。</summary>
    private static void Apply(FrameworkElement host, double width)
    {
        if (width <= 0) return;

        var pivot = GetWidthMode(host) == SqlRowWidth.Narrow ? NarrowWidth + Hysteresis : NarrowWidth;
        SetWidthMode(host, width < pivot ? SqlRowWidth.Narrow : SqlRowWidth.Regular);
    }
}
