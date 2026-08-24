using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace SqlAssist.Ssms22;

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

    public static Brush Border =>
        Resolve(EnvironmentColors.ToolTipBorderBrushKey, SystemColors.ActiveBorderBrush);

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
