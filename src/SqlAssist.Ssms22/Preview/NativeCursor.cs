using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 取得與任何視窗都無關的滑鼠座標。
/// </summary>
/// <remarks>
/// 縮放握把不能用 <see cref="System.Windows.Controls.Primitives.Thumb"/> 回報的位移量。
/// 那個位移量是相對於握把的父代算出來的，而浮動預覽在調整大小的過程中會被平台
/// 重新定位——父代自己在動，位移量就會把視窗的移動誤算成滑鼠的移動，
/// 於是「變大 → 視窗被移走 → 又算出一段位移 → 再變大」，形成回授而亂跳。
///
/// 改成每一次都直接問系統游標在螢幕上的絕對座標，尺寸就成為
/// 「起始尺寸 ＋ 游標相對於按下瞬間的位移」這個純函式，
/// 視窗怎麼跳都不會影響算出來的大小。
/// </remarks>
internal static class NativeCursor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>目前的游標位置，單位是實體像素；取不到時回傳 null。</summary>
    public static Point? TryGetPosition()
    {
        return SqlAssistPlatformGuard.Probe<Point?>(
            "取得游標位置",
            () => GetCursorPos(out var point) ? new Point(point.X, point.Y) : null,
            fallback: null);
    }

    /// <summary>取得拖曳開始時固定使用的 DIP 到實體像素矩陣。</summary>
    public static Matrix GetTransformToDevice(Visual visual)
    {
        return SqlAssistPlatformGuard.Probe(
            "換算 DPI",
            () => PresentationSource.FromVisual(visual)?.CompositionTarget is { } target
                ? target.TransformToDevice
                : Matrix.Identity,
            fallback: Matrix.Identity);
    }
}
