using System;
using System.Windows;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 通知島浮層落在擁有者視窗的哪裡：純計算，不碰視窗。
/// </summary>
/// <remarks>
/// 錨在右下角、狀態列上方，從那一角往左上長：展開、收回與提醒疊起來時右下角不動，
/// 使用者的視線停在同一個點上。擁有者是作用中文件所在的頂層視窗（主視窗或拆出去的
/// 文件框架），座標與結果都是裝置像素，DIP 的邊距依那一台螢幕的 DPI 換算。
/// </remarks>
internal static class NotificationPlacement
{
    /// <summary>與擁有者右緣的距離（DIP）。</summary>
    public const double RightMargin = 16;

    /// <summary>與狀態列上緣的距離（DIP）。</summary>
    public const double StatusBarGap = 12;

    /// <summary>抓不到狀態列時假設的高度（DIP）；SSMS 22 的狀態列在 100% 時就是這麼高。</summary>
    public const double FallbackStatusBarHeight = 28;

    /// <summary>算出浮層在螢幕上的矩形（裝置像素）。</summary>
    /// <param name="owner">擁有者視窗的外框，裝置像素。</param>
    /// <param name="statusBarHeight">狀態列高度（DIP）；抓不到時傳 null。</param>
    /// <param name="dpiScale">擁有者所在螢幕的縮放（1 = 96 DPI）。</param>
    /// <param name="island">島嶼含疊層在內的尺寸（DIP）。</param>
    /// <remarks>擁有者比島嶼還小時貼齊左上角並裁掉超出的部分，不跑出擁有者之外。</remarks>
    public static Rect Place(Rect owner, double? statusBarHeight, double dpiScale, Size island)
    {
        if (dpiScale <= 0 || double.IsNaN(dpiScale)) throw new ArgumentOutOfRangeException(nameof(dpiScale));
        var statusBar = statusBarHeight is { } height && height > 0 ? height : FallbackStatusBarHeight;
        var width = Math.Round(island.Width * dpiScale);
        var tall = Math.Round(island.Height * dpiScale);
        var right = owner.Right - Math.Round(RightMargin * dpiScale);
        var bottom = owner.Bottom - Math.Round((statusBar + StatusBarGap) * dpiScale);
        var left = Math.Max(owner.Left, right - width);
        var top = Math.Max(owner.Top, bottom - tall);
        return new Rect(left, top, Math.Max(0, Math.Min(width, right - left)), Math.Max(0, Math.Min(tall, bottom - top)));
    }
}
