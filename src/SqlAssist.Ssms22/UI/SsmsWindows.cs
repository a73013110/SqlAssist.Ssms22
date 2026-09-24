using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI.Shell.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// SSMS 的頂層視窗：主視窗、使用者正在操作的框架、對話框的擁有者。
/// </summary>
/// <remarks>
/// 「框架」只有主視窗與停駐系統拆出去的 <see cref="FloatingWindow"/>，用白名單認：
/// 排除對話框的黑名單一定會漏——SSMS 的連線對話框繼承的是它自己那一套 <c>DialogWindow</c>，
/// 不是 VS 的 <c>DialogWindowBase</c>，通知島就錨在它的「連接／取消」上面。
/// 備份、產生指令碼這類 WinForms 對話框本來就不在 <see cref="Application.Windows"/> 裡。
/// 不要求框架由主視窗擁有：VS 可以把浮動框架設成獨立視窗，那時 <see cref="Window.Owner"/> 是 null。
///
/// 只在 UI 執行緒上使用。
/// </remarks>
internal static class SsmsWindows
{
    private static EventHandler? _focusMoved;
    private static bool _focusHooked;

    /// <summary>SSMS 主視窗。</summary>
    public static Window? Main => Application.Current?.MainWindow;

    /// <summary>
    /// 使用者正在操作的框架；焦點在別的程式、對話框、WinForms 視窗或 SqlAssist 自己的視窗上時為 null。
    /// </summary>
    /// <remarks>
    /// 不從「最後取得焦點的 SQL 編輯區」推：SQL Search、物件總管這些工具視窗不是編輯區，
    /// 在主視窗裡操作它們時，那個編輯區可能還在另一台螢幕上拆出去的框架裡。
    /// </remarks>
    public static Window? ActiveFrame
    {
        get
        {
            if (Application.Current is not { } application) return null;
            foreach (Window window in application.Windows)
                if (window.IsActive && IsFrame(window)) return window;
            return null;
        }
    }

    /// <summary>
    /// 鍵盤焦點落到任何 WPF 元素上，包括裝在 HwndHost 裡的編輯器。
    /// </summary>
    /// <remarks>
    /// 視窗的 <c>Activated</c> 不是路由事件，拆出去的框架又是之後才建立的，只能逐一訂閱；
    /// 類別處理常式只註冊一次、第一次訂閱時才掛。事件發出的當下 <see cref="Window.IsActive"/>
    /// 可能還沒更新，作用中框架換了沒有由訂閱端排到下一輪再用 <see cref="ActiveFrame"/> 判斷。
    /// </remarks>
    public static event EventHandler? FocusMoved
    {
        add
        {
            if (!_focusHooked)
            {
                _focusHooked = true;
                EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent,
                    new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), handledEventsToo: true);
            }

            _focusMoved += value;
        }
        remove => _focusMoved -= value;
    }

    /// <summary>主視窗或拆出去的框架；對話框與 SqlAssist 自己的視窗都不算。</summary>
    public static bool IsFrame(Window window) => window is FloatingWindow || ReferenceEquals(window, Main);

    /// <summary>看得到：已顯示而且沒有最小化。</summary>
    public static bool IsShowing(Window? window) => window is { IsVisible: true, WindowState: not WindowState.Minimized };

    /// <summary>
    /// 把 <paramref name="source"/> 所在的框架帶回前景；已經在前景時什麼都不做。
    /// </summary>
    /// <remarks>
    /// 給不會自己啟用的浮動表面用（編輯器上的預覽 Popup）：WPF 的 Popup 對滑鼠回 <c>MA_NOACTIVATE</c>，
    /// SSMS 在背景時點進去，程式不會回到前景，鍵盤焦點就進不去——搜尋框點不進、指令碼拉選不起來、
    /// Esc 送到別的程式；只靠滑鼠的分頁、資料格與捲軸照常動，所以看起來像壞了一半。
    /// 在按下的預覽階段先啟用框架（同一條執行緒，啟用是同步完成的），這一次按下接著照常把焦點
    /// 搬進表面。使用者剛在這個程序上按下滑鼠，前景鎖允許這一次切換。
    /// </remarks>
    public static void ActivateFrameOf(DependencyObject source)
    {
        if (Window.GetWindow(source) is { IsActive: false } window) window.Activate();
    }

    /// <summary>對話框的擁有者：來源所在的視窗，拿不到時是主視窗。</summary>
    public static Window OwnerOf(DependencyObject source) =>
        Window.GetWindow(source) ?? Main ?? throw new InvalidOperationException("找不到 SSMS 主視窗，無法開啟對話框。");

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        // 類別處理常式會在路由上的每一個元素各叫一次；只在焦點落下的那一個發出。
        if (_focusMoved is not { } handler || !ReferenceEquals(sender, args.NewFocus)) return;
        SqlAssistPlatformGuard.Run("通知焦點移動", () => handler(null, EventArgs.Empty));
    }
}
