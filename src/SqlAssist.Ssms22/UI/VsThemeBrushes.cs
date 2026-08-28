using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 解析 SSMS 目前佈景主題的筆刷。
/// </summary>
/// <remarks>
/// 原本建議視窗一律使用 <see cref="SystemColors"/>，那跟的是 Windows 佈景主題而不是
/// SSMS 的，所以 Windows 淺色搭配 SSMS 深色時會出現白底黑字的清單。
/// 這裡改為向 VS 的主題資源字典查詢；查不到時退回原本的系統色，
/// 讓建議視窗在任何情況下都還是看得見。
/// </remarks>
internal static class VsThemeBrushes
{
    public static Brush ListBackground =>
        Resolve(EnvironmentColors.ToolTipBrushKey, SystemColors.WindowBrush);

    public static Brush ListForeground =>
        Resolve(EnvironmentColors.ToolTipTextBrushKey, SystemColors.WindowTextBrush);

    public static Brush DimForeground =>
        Resolve(EnvironmentColors.SystemGrayTextBrushKey, SystemColors.GrayTextBrush);

    /// <summary>工具視窗的底色；與提示視窗不同，工具視窗停駐在 IDE 裡，跟的是另一組資源。</summary>
    public static Brush WindowBackground =>
        Resolve(EnvironmentColors.ToolWindowBackgroundBrushKey, SystemColors.ControlBrush);

    public static Brush WindowForeground =>
        Resolve(EnvironmentColors.ToolWindowTextBrushKey, SystemColors.ControlTextBrush);

    public static Brush Border =>
        Resolve(EnvironmentColors.ToolTipBorderBrushKey, SystemColors.ActiveBorderBrush);

    /// <summary>分隔用的細線，比 <see cref="Border"/> 淡得多。</summary>
    public static Brush Hairline => Overlay(0.10);

    /// <summary>滑鼠掃過的那一列。</summary>
    public static Brush RowHover => Overlay(0.05);

    /// <summary>交替列；淡到只夠讓眼睛沿著一列橫著走，不會被讀成分組。</summary>
    public static Brush RowAlternate => Overlay(0.045);

    /// <summary>選取的儲存格；也是按鈕被滑鼠掃過時的底色。</summary>
    public static Brush RowSelected => Overlay(0.12);

    /// <summary>按下去的那一刻；比滑鼠掃過再重一階，手指離開就退回去。</summary>
    public static Brush RowPressed => Overlay(0.18);

    /// <summary>分段控制器的底槽。</summary>
    public static Brush SegmentTrack => Overlay(0.06);

    /// <summary>中性徽章的底色。</summary>
    public static Brush BadgeBackground => Overlay(0.07);

    /// <summary>
    /// 強調用徽章的底色；整個視窗只有主索引鍵用得到。
    /// </summary>
    /// <remarks>
    /// 備援刻意不是 <see cref="SystemColors.HighlightBrush"/>。那是一塊飽和的實心藍，
    /// 徽章上的字仍然是淡色的前景色，疊上去就是看不清楚的深藍配灰。
    /// 主題查不到時退回中性徽章——少一點強調只是平了一點，配錯色卻是讀不到。
    /// </remarks>
    public static Brush AccentBackground =>
        Resolve(EnvironmentColors.AccentPaleBrushKey, BadgeBackground);

    public static Brush AccentBorder =>
        Resolve(EnvironmentColors.AccentBorderBrushKey, Hairline);

    /// <summary>
    /// 前景色的低透明度版本。
    /// </summary>
    /// <remarks>
    /// 層次刻意不用主題裡現成的那些筆刷。它們是為停駐面板調的，放在提示視窗的
    /// 底色上不是太重就是完全看不見，而且淺色與深色主題各偏一邊。
    /// 從前景色本身按比例調淡，兩種主題就自動各自成立——淺色主題得到淡灰，
    /// 深色主題得到淡白，對比永遠是同一個量。
    ///
    /// 取不到顏色時回傳透明而不是猜一個灰：少一層底色只是平了一點，
    /// 猜錯方向卻會在深色主題上糊成一片。
    /// </remarks>
    private static Brush Overlay(double opacity)
    {
        if (ListForeground is not SolidColorBrush { Color: var color })
        {
            return System.Windows.Media.Brushes.Transparent;
        }

        var brush = new SolidColorBrush(
            Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B));

        brush.Freeze();
        return brush;
    }

    private static Brush Resolve(object resourceKey, Brush fallback)
    {
        try
        {
            // 主題字典由 VS 併入 Application.Current.Resources；SSMS 若尚未載入
            // 或鍵值不存在，TryFindResource 會回傳 null 而不是擲例外。
            if (Application.Current?.TryFindResource(resourceKey) is Brush brush)
            {
                return brush;
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"解析佈景主題筆刷失敗：{exception.Message}");
        }

        return fallback;
    }
}
