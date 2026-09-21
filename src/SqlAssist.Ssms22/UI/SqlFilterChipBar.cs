using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 已選條件的 chip 列；預設狀態整列收起，不佔那一列。
/// </summary>
/// <remarks>
/// 這是這個版面空間極大化的關鍵：沒有條件就不留空白列。
/// 收起用 <see cref="Visibility.Collapsed"/> 而不是把高度設成 0——後者仍會參與量測，
/// 而清單少掉的正是那幾個 DIP。
///
/// <b>永遠只有一列</b>：chip 一維度一顆（目前的維度型別是 <c>SqlSearchFilterChip</c>），而放不下時
/// 橫向捲動，不換行。換行的那一版在停靠面板裡會長到三列，而那三列換算成少看六筆結果；
/// 捲動的規矩與預覽資訊列同一份 <see cref="SqlAssistChrome.CreateHorizontalStrip"/>。
///
/// 這一列不認得任何一種維度——<see cref="SetChips{T}"/> 吃的是宿主的型別加一個取標籤的委派，
/// 事件也只把那顆 chip 原樣交回去。所以名稱不帶 Search：第二個要畫已選條件的視窗直接用它，
/// 不必先去 Search 那一份確認自己有沒有在借別人的東西。上這一列的條件限於<b>清得掉、也開得了
/// 面板</b>的維度，常駐可見的直接控制不上來（見 <c>docs/ui-windows.md</c>）。
/// </remarks>
internal sealed class SqlFilterChipBar : ContentControl
{
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };

    public SqlFilterChipBar()
    {
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 4, 0, 0);
        Focusable = false;
        Content = SqlAssistChrome.CreateHorizontalStrip(_strip, "已選條件（可水平捲動）");
        AutomationProperties.SetName(this, "已選條件");
    }

    /// <summary>按下某一顆 chip 的十字；宿主據此清掉它代表的整個維度。</summary>
    public event Action<object>? RemoveRequested;

    /// <summary>按下 chip 本體；宿主據此打開那個維度的過濾面板。</summary>
    public event Action<object>? OpenRequested;

    /// <summary>
    /// 換一整列 chip；空的就整列收起。
    /// </summary>
    /// <remarks>
    /// 每一顆都開得了自己的面板，所以沒有「這一顆按不下去」那個分支：上這一列的條件就是
    /// 有面板、清得掉的那幾個維度，常駐可見的開關（大小寫、全字）本來就不該再畫一顆。
    /// </remarks>
    public void SetChips<T>(IReadOnlyList<T> chips, Func<T, string> label) where T : class
    {
        if (chips is null) throw new ArgumentNullException(nameof(chips));
        if (label is null) throw new ArgumentNullException(nameof(label));

        _strip.Children.Clear();

        foreach (var chip in chips)
        {
            var element = SqlAssistChrome.CreateFilterChip(
                label(chip), "：開啟面板調整", out var remove, out var open);
            remove.Click += (_, _) => RemoveRequested?.Invoke(chip);
            open.Click += (_, _) => OpenRequested?.Invoke(chip);
            _strip.Children.Add(element);
        }

        Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
