using System.Windows;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 清單的多選狀態，以可繼承附加屬性往下傳給每一列的樣板。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlRowLayout"/> 的寬度模式同一種做法：清單設一次，每一列從最近的祖先讀，
/// 不在每一個容器上各存一份。容器是 recycling 重用的，存在上面的值會跟著容器換給別的列。
/// </remarks>
internal static class SqlRowCheck
{
    /// <summary>這份清單開了多選；沒開的清單（SQL Search）樣板裡的勾選框永遠不出現。</summary>
    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.RegisterAttached(
        "IsAvailable", typeof(bool), typeof(SqlRowCheck),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>正在多選模式；每一列都亮出勾選框。</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(SqlRowCheck),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsAvailable(DependencyObject element) => (bool)element.GetValue(IsAvailableProperty);

    public static void SetIsAvailable(DependencyObject element, bool value) => element.SetValue(IsAvailableProperty, value);

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}

/// <summary>
/// 列上的勾選框：顯示列資料的 <see cref="ISqlCheckableRow.IsChecked"/>，自己不改它。
/// </summary>
/// <remarks>
/// 滑鼠、空白鍵與無障礙的 Toggle 模式最後都走 <see cref="OnToggle"/>；這裡改成發出
/// <see cref="ToggleRequestedEvent"/>，交給清單經由選取控制器切換。讓它自己翻 <c>IsChecked</c> 的話，
/// 本機值會蓋掉單向繫結，下一次捲動重用容器時顯示的就不再是那一列的狀態，
/// 而且錨點、數量與模式都不知道它變了。
///
/// 不可聚焦也不在 Tab 順序上：清單只有一個 Tab 停駐點，鍵盤由焦點列上的空白鍵勾選。
/// 它只在多選模式出現（見 <see cref="SqlAssistChrome.RevealRowCheck"/>），平常進入多選走
/// Ctrl／Shift+點擊或空白鍵。
/// </remarks>
internal sealed class SqlRowCheckBox : CheckBox
{
    public static readonly RoutedEvent ToggleRequestedEvent = EventManager.RegisterRoutedEvent(
        "ToggleRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SqlRowCheckBox));

    public SqlRowCheckBox()
    {
        Focusable = false;
        IsTabStop = false;
        Template = SqlAssistChrome.CreateCheckBoxTemplate(compact: true, round: true);
    }

    protected override void OnToggle() => RaiseEvent(new RoutedEventArgs(ToggleRequestedEvent, this));
}
