using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SqlAssist.Ssms22.Preview;

/// <summary>預覽定位需要的螢幕工作區、DPI 轉換與 Popup 視窗層級操作。</summary>
internal static class NativeScreen
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint GetRoot = 2;
    private const uint SetWindowPositionFlags = 0x0013; // 不移動、不改尺寸、不啟用。
    private static readonly IntPtr NotTopmost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;

        public NativeRect Monitor;

        public NativeRect WorkArea;

        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    /// <summary>
    /// DIP 換算成實體像素的矩陣。
    /// </summary>
    /// <remarks>
    /// 定位運算與原生游標都以實體像素為單位，只有 WPF 的尺寸是 DIP，因此整條路徑上
    /// 只有兩個邊界需要換算。兩邊各自寫一份 <see cref="PresentationSource.FromVisual"/>
    /// 的結果是「其中一份改了另一份沒改」，症狀會是高 DPI 下拖曳與定位對不上。
    /// </remarks>
    public static Matrix GetTransformToDevice(Visual visual) =>
        SqlAssistPlatformGuard.Probe(
            "換算 DPI",
            () => PresentationSource.FromVisual(visual)?.CompositionTarget is { } target
                ? target.TransformToDevice
                : Matrix.Identity,
            fallback: Matrix.Identity);

    /// <summary>實體像素換算回 DIP 的矩陣。</summary>
    public static Matrix GetTransformFromDevice(Visual visual) =>
        SqlAssistPlatformGuard.Probe(
            "換算 DPI",
            () => PresentationSource.FromVisual(visual)?.CompositionTarget is { } target
                ? target.TransformFromDevice
                : Matrix.Identity,
            fallback: Matrix.Identity);

    /// <summary>取得指定螢幕點所在顯示器的工作區；單位仍是實體像素。</summary>
    public static Rect? TryGetWorkArea(Point screenPoint)
    {
        return SqlAssistPlatformGuard.Probe<Rect?>(
            "取得預覽所在螢幕工作區",
            () =>
            {
                var monitor = MonitorFromPoint(
                    new NativePoint
                    {
                        X = (int)Math.Round(screenPoint.X),
                        Y = (int)Math.Round(screenPoint.Y)
                    },
                    MonitorDefaultToNearest);

                if (monitor == IntPtr.Zero)
                {
                    return null;
                }

                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info))
                {
                    return null;
                }

                return new Rect(
                    info.WorkArea.Left,
                    info.WorkArea.Top,
                    Math.Max(0, info.WorkArea.Right - info.WorkArea.Left),
                    Math.Max(0, info.WorkArea.Bottom - info.WorkArea.Top));
            },
            fallback: null);
    }

    /// <summary>
    /// WPF 祖先樹跨不過 HWND 邊界時，只接受與編輯器同欄且向下擴張的最近子視窗；
    /// 明確不檢查 top-level root，否則底部工具窗與狀態列會被誤當成查詢結果區。
    /// </summary>
    public static double? TryGetDocumentColumnBottom(
        Visual visual,
        Rect editorBounds,
        double tolerance)
    {
        return SqlAssistPlatformGuard.Probe<double?>(
            "取得查詢文件欄底界",
            () =>
            {
                if (PresentationSource.FromVisual(visual) is not HwndSource source)
                {
                    return null;
                }

                var current = source.Handle;
                var root = GetAncestor(current, GetRoot);
                var minimumUsefulExpansion = Math.Max(8, tolerance / 2);
                for (var depth = 0; depth < 32 && current != IntPtr.Zero; depth++)
                {
                    if (current == root)
                    {
                        // 明確排除 top-level root；GetParent 對 owned window 不一定回傳零。
                        break;
                    }

                    var parent = GetParent(current);
                    if (parent == IntPtr.Zero)
                    {
                        // current 已是 top-level 視窗，絕不拿整個 SSMS 當文件欄。
                        break;
                    }

                    if (!GetWindowRect(current, out var rectangle))
                    {
                        break;
                    }

                    var sameColumn =
                        Math.Abs(rectangle.Left - editorBounds.Left) <= tolerance &&
                        Math.Abs(rectangle.Right - editorBounds.Right) <= tolerance &&
                        Math.Abs(rectangle.Top - editorBounds.Top) <= tolerance;
                    if (sameColumn &&
                        rectangle.Bottom > editorBounds.Bottom + minimumUsefulExpansion)
                    {
                        return (double)rectangle.Bottom;
                    }

                    current = parent;
                }

                return null;
            },
            fallback: null);
    }

    /// <summary>
    /// WPF Popup 預設會建立最上層視窗；降成非最上層，切到別的應用程式時才不會蓋住它。
    /// </summary>
    public static void SetNoTopmost(Visual visual)
    {
        SqlAssistPlatformGuard.Probe("調整預覽視窗層級", () =>
        {
            if (PresentationSource.FromVisual(visual) is HwndSource source)
            {
                SetWindowPos(source.Handle, NotTopmost, 0, 0, 0, 0, SetWindowPositionFlags);
            }
        });
    }
}
