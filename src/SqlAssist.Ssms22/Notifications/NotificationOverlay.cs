using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 承載通知島的透明附屬視窗：錨在擁有者右下角、狀態列上方，不接受啟用，島嶼以外的點擊穿透。
/// </summary>
/// <remarks>
/// 附屬（owned）而不是置頂：擁有者最小化時一起隱藏，別的應用程式蓋上來時一起被蓋住，Alt+Tab 也只看到
/// 擁有者。視窗大小固定為 <see cref="NotificationIsland.MaxExtent"/> 加上柔影邊距，變形只在視窗裡面發生；
/// 閒置時整個 <see cref="Window.Hide"/>，分層視窗不再參與合成。
///
/// 點擊穿透有兩道：透明像素本來就不收滑鼠，柔影那一圈半透明像素則由 <c>WM_NCHITTEST</c> 回
/// <c>HTTRANSPARENT</c>——判斷依島嶼自己的命中測試，圓角以外都算在外面。
///
/// 位置一律以裝置像素交給 <c>SetWindowPos</c>，邊距依擁有者的 DPI 換算（<see cref="NotificationPlacement"/>）；
/// 擁有者跨螢幕或換 DPI 時由它自己的事件重新定位。這一份只管視窗；何時顯示、擁有者是誰在控制器。
///
/// 只在 UI 執行緒上使用。
/// </remarks>
internal sealed class NotificationOverlay : Window
{
    /// <summary>柔影需要的邊距（DIP）：半徑 16、偏移 2，裁掉的話底邊會切出一條直線。</summary>
    public const double ShadowMargin = 16;

    /// <summary>主視窗找不到狀態列時，往下搜尋的元素上限；SSMS 的主視窗樹有數千個元素。</summary>
    private const int StatusBarSearchLimit = 4000;

    private Window? _anchor;
    private double? _statusBar;
    private bool _keyboard;
    private IInputElement? _returnFocus;

    public NotificationOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Focusable = false;
        Title = "SqlAssist 通知";
        var extent = NotificationIsland.MaxExtent;
        Width = extent.Width + ShadowMargin * 2;
        Height = extent.Height + ShadowMargin * 2;
        VsThemeBrushes.Apply(this);

        Island = new NotificationIsland();
        // 浮層不接受啟用，鍵盤只能靠「聚焦通知」進來；進來之後 Tab 在島嶼裡繞圈，不跑到看不見的地方。
        KeyboardNavigation.SetTabNavigation(Island, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(Island, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this, Title);
        Content = new Grid { Margin = new Thickness(ShadowMargin), Children = { Island } };

        Island.Vanished += (_, _) => Hide();
        PreviewGotKeyboardFocus += OnPreviewGotKeyboardFocus;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => _keyboard = false;
        SourceInitialized += OnSourceInitialized;
    }

    public NotificationIsland Island { get; }

    /// <summary>擁有者最小化、還原或正要關閉；控制器要重新決定顯示與否。</summary>
    public event EventHandler? AnchorStateChanged;

    /// <summary>目前錨定的視窗；還沒有時是 null。</summary>
    public Window? Anchor => _anchor;

    /// <summary>指標或鍵盤焦點在島嶼上，活動的期限要暫停。</summary>
    public bool Retaining => Island.IsMouseOver || Island.IsKeyboardFocusWithin;

    /// <summary>
    /// 換到這個擁有者上。
    /// </summary>
    /// <returns>
    /// 真的換了才回 true：這時浮層已經隱藏、島嶼已經重設，呼叫端要重新起算可見時間，
    /// 下一次顯示從圓點重新長出來——在舊位置收場、新位置接著變形，看起來是島嶼飛過去。
    /// </returns>
    public bool Attach(Window anchor)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        if (ReferenceEquals(anchor, _anchor)) return false;
        Hide();
        Island.Reset();
        Detach();
        _anchor = anchor;
        _statusBar = null;
        anchor.LocationChanged += OnAnchorMoved;
        anchor.SizeChanged += OnAnchorMoved;
        anchor.DpiChanged += OnAnchorMoved;
        anchor.StateChanged += OnAnchorState;
        anchor.Closing += OnAnchorClosing;
        Owner = anchor;
        return true;
    }

    /// <summary>放開目前的擁有者；擁有者關閉時先放手，附屬視窗才不會跟著被關掉。</summary>
    public void Detach()
    {
        if (_anchor is not { } anchor) return;
        anchor.LocationChanged -= OnAnchorMoved;
        anchor.SizeChanged -= OnAnchorMoved;
        anchor.DpiChanged -= OnAnchorMoved;
        anchor.StateChanged -= OnAnchorState;
        anchor.Closing -= OnAnchorClosing;
        _anchor = null;
        Owner = null;
    }

    /// <summary>定位並在需要時顯示；不啟用、不搶焦點。</summary>
    public void Present()
    {
        if (!SsmsWindows.IsShowing(_anchor)) return;
        // 先有控制代碼、定好位置再顯示，第一個影格才不會出現在螢幕左上角。
        new WindowInteropHelper(this).EnsureHandle();
        Place();
        if (!IsVisible) Show();
    }

    /// <summary>依擁有者目前的位置、大小與 DPI 重新擺位；視窗大小不隨島嶼變形而改。</summary>
    public void Place()
    {
        if (_anchor is not { } anchor || !TryGetClientRect(anchor, out var client)) return;
        var scale = VisualTreeHelper.GetDpi(anchor).DpiScaleX;
        _statusBar ??= FindStatusBarHeight(anchor);
        var extent = NotificationIsland.MaxExtent;
        var island = NotificationPlacement.Place(client, _statusBar, scale, extent);
        // 柔影邊距往四周外擴；落在擁有者之外的那一圈是透明的，本來就穿透。
        var margin = Math.Round(ShadowMargin * scale);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        Native.SetWindowPos(handle, IntPtr.Zero,
            (int)(island.Right - Math.Round(extent.Width * scale) - margin), (int)(island.Bottom - Math.Round(extent.Height * scale) - margin),
            (int)(Math.Round(extent.Width * scale) + margin * 2), (int)(Math.Round(extent.Height * scale) + margin * 2),
            Native.SwpNoActivate | Native.SwpNoZOrder | Native.SwpNoOwnerZOrder);
    }

    /// <summary>
    /// 「聚焦通知」：暫時啟用浮層，把鍵盤焦點放到島嶼上的第一個控制項。
    /// </summary>
    /// <returns>島嶼沒有東西可以聚焦時為 false。</returns>
    public bool EnterKeyboard()
    {
        if (!IsVisible || Island.Shape == NotificationIslandShape.Hidden) return false;
        _returnFocus = Keyboard.FocusedElement;
        _keyboard = true;
        Activate();
        return Island.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) || Island.IsKeyboardFocusWithin;
    }

    /// <summary>Esc：把焦點還給擁有者與原本的元素，島嶼照一般的收回延遲收起。</summary>
    public void LeaveKeyboard()
    {
        if (!_keyboard) return;
        _keyboard = false;
        _anchor?.Activate();
        if (_returnFocus is { } element) Keyboard.Focus(element);
        _returnFocus = null;
    }

    /// <summary>
    /// 不在鍵盤模式時擋下焦點。
    /// </summary>
    /// <remarks>
    /// 按鈕按下時會要求焦點；在不接受啟用的視窗上，那一次 <c>SetFocus</c> 會把整個浮層啟用，
    /// 查詢視窗的游標就這樣被一顆「稍後提醒」搶走。擋在 Preview 階段，點擊本身照常送達。
    /// </remarks>
    private void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (!_keyboard) args.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        LeaveKeyboard();
        args.Handled = true;
    }

    private void OnAnchorMoved(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("定位通知島", () => { if (IsVisible) Place(); });

    private void OnAnchorState(object? sender, EventArgs args) => AnchorStateChanged?.Invoke(this, EventArgs.Empty);

    // 附屬視窗會跟著擁有者一起被關掉；在 Closing 放手，控制器下一輪重新選錨點。
    private void OnAnchorClosing(object? sender, System.ComponentModel.CancelEventArgs args) =>
        SqlAssistPlatformGuard.Probe("放開通知島的擁有者", () =>
        {
            Hide();
            Island.Reset();
            Detach();
            AnchorStateChanged?.Invoke(this, EventArgs.Empty);
        });

    private void OnSourceInitialized(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("設定通知島視窗", () =>
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtr(handle, Native.GwlExStyle).ToInt64();
        Native.SetWindowLongPtr(handle, Native.GwlExStyle, new IntPtr(style | Native.WsExNoActivate | Native.WsExToolWindow));
        HwndSource.FromHwnd(handle)?.AddHook(HitTest);
    });

    /// <summary>島嶼形狀以外（含柔影那一圈）回 <c>HTTRANSPARENT</c>，點擊落到擁有者上。</summary>
    private IntPtr HitTest(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != Native.WmNcHitTest) return IntPtr.Zero;
        var screen = new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var inside = SqlAssistPlatformGuard.Probe("判斷通知島命中", () =>
        {
            if (!Island.IsVisible) return false;
            var point = Island.PointFromScreen(screen);
            return Island.InputHitTest(point) is not null;
        }, fallback: false);
        if (inside) return IntPtr.Zero;
        handled = true;
        return new IntPtr(Native.HtTransparent);
    }

    private static bool TryGetClientRect(Window window, out Rect rect)
    {
        rect = Rect.Empty;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !Native.GetClientRect(handle, out var client)) return false;
        var origin = new Native.Point();
        if (!Native.ClientToScreen(handle, ref origin)) return false;
        rect = new Rect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        return rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>
    /// 從擁有者的視覺樹找貼著底邊的狀態列高度（DIP）；找不到時回 null，由定位改用固定高度。
    /// </summary>
    /// <remarks>
    /// 只有主視窗有狀態列；拆出去的文件框架沒有，但 28 DIP 的預設只是讓島嶼高一點，不會蓋到東西。
    /// 每換一次擁有者找一次，廣度優先並設上限，不在每一次移動視窗時重走一遍整棵樹。
    /// </remarks>
    private static double? FindStatusBarHeight(Window window) => SqlAssistPlatformGuard.Probe<double?>("尋找狀態列", () =>
    {
        if (!ReferenceEquals(window, SsmsWindows.Main) || window.Content is not FrameworkElement root) return null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        for (var visited = 0; queue.Count > 0 && visited < StatusBarSearchLimit; visited++)
        {
            var node = queue.Dequeue();
            if (node is FrameworkElement { IsVisible: true } element && element.ActualHeight is > 0 and < 80 &&
                element.GetType().Name.IndexOf("StatusBar", StringComparison.Ordinal) >= 0)
            {
                var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                if (root.ActualHeight - bounds.Bottom < 2) return element.ActualHeight;
            }

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, index));
        }

        return null;
    }, fallback: null);

    private static class Native
    {
        public const int GwlExStyle = -20;
        public const long WsExNoActivate = 0x08000000;
        public const long WsExToolWindow = 0x00000080;
        public const int WmNcHitTest = 0x0084;
        public const int HtTransparent = -1;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpNoOwnerZOrder = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr window, ref Point point);
    }
}
