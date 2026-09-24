using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Text.Editor;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 握著鍵盤的自製 Popup：殼層解析成編輯器命令的按鍵要交還給它。
/// </summary>
internal interface IShellKeyTarget
{
    /// <summary>Popup 貼著的那個編輯器；命令鏈是這個編輯器的，別的查詢視窗不受影響。</summary>
    ITextView View { get; }

    /// <summary>按鍵應該落在這一塊之內；焦點不在裡面時不能把按鍵丟進輸入管線。</summary>
    UIElement Scope { get; }

    /// <summary>焦點被殼層搶回編輯器時要回到的那一個元素。</summary>
    IInputElement FocusTarget { get; }

    /// <summary>
    /// Esc 的第二條路：實測第一次 Esc 不一定會變成 <c>VSStd2K/CANCEL</c> 走進命令鏈，
    /// 查詢視窗會先拿它取消自己的選取，只有現代管線的 <c>EscapeKeyCommandArgs</c> 收得到。
    /// </summary>
    void Cancel();
}

/// <summary>
/// 把殼層解析成編輯器命令的按鍵，交還給目前握著鍵盤的自製 Popup。
/// </summary>
/// <remarks>
/// 殼層是照<b>作用中的視窗框架</b>（Popup 開著時仍是查詢視窗）在訊息迴圈裡預先把按鍵
/// 解析成命令，再沿著那個框架的命令鏈派送——與焦點落在哪個 HWND 無關。Backspace、
/// Delete、方向鍵、Enter 在「文字編輯器」範圍全都有繫結，不攔就是改到後面那份 SQL；
/// 英數字沒有繫結，才會只有打字看起來正常。
///
/// 包夾清單與結構預覽的搜尋框都是這種 Popup，因此收在一份：<c>SqlShellCommandFilter</c>
/// 在轉傳前只付得起一次靜態欄位讀取，同一時間也只會有一個 Popup 握著鍵盤。
/// 各寫一份的症狀是濾鏡上多一次讀取，而第二份漏掉 <c>QueryStatus</c> 的認領或通道階段。
///
/// 只在 UI 執行緒上存取（命令派送本來就只發生在那裡），不必同步。
/// </remarks>
internal static class ShellKeyCapture
{
    private static IShellKeyTarget? _active;

    /// <summary>有沒有 Popup 握著鍵盤；命令濾鏡在轉傳前只付得起這一次靜態欄位讀取。</summary>
    public static bool IsActive => _active is not null;

    /// <summary>從現在起按鍵交給 <paramref name="target"/>；後來的取代先來的。</summary>
    public static void Begin(IShellKeyTarget target) =>
        _active = target ?? throw new ArgumentNullException(nameof(target));

    /// <summary>
    /// <paramref name="target"/> 不再握著鍵盤。
    /// </summary>
    /// <remarks>比對執行個體：舊 Popup 的關閉事件可能晚於新的那一份登記，不能把新的清掉。</remarks>
    public static void End(IShellKeyTarget target)
    {
        if (ReferenceEquals(_active, target))
        {
            _active = null;
        }
    }

    /// <summary>
    /// 把殼層命令換回按鍵，交給握著鍵盤的那一個。
    /// </summary>
    /// <remarks>
    /// 這裡不逐個命令自己實作行為，而是換回對應的按鍵重新丟進 WPF 的輸入管線：
    /// 修飾鍵仍是實體狀態，Shift+Tab、Shift+↑、Ctrl+← 之類就由文字方塊與清單自己
    /// 處理，不必在這裡重寫一份鍵盤語意，日後多接一個命令也只是多一列對照。
    ///
    /// 認得的命令一律回 <c>true</c>，即使當下做不了（例如沒有選取還按 Ctrl+C，或是
    /// 焦點被殼層搶回編輯器）：往下轉就是回到「改到後面那份 SQL」的原症狀，
    /// 而少一次按鍵頂多是使用者再按一次。
    /// </remarks>
    /// <param name="view">濾鏡自己掛著的那個編輯器，不是目前作用中的。</param>
    /// <param name="execute">
    /// <c>false</c> 是 <c>QueryStatus</c> 只問「這個命令歸 Popup 管嗎」。認領這一步不能省：
    /// 沒有人認領的命令是停用的，而停用的命令連 <c>Exec</c> 都不會發出去——編輯器
    /// 自己剛好把某個命令回報成停用時（例如沒東西可復原），那個鍵就會安靜地消失。
    /// </param>
    public static bool TryHandle(ITextView view, Guid group, uint commandId, bool execute)
    {
        if (For(view) is not { } target)
        {
            return false;
        }

        var command = ShellKeyMap.MapCommand(group, commandId);
        var key = command is null ? ShellKeyMap.MapKey(group, commandId) : null;
        if (command is null && key is null)
        {
            return false;
        }

        if (!execute)
        {
            return true;
        }

        // 焦點被搶走時先要回來：不能靠 WPF 目前的焦點元素，那可能已經在編輯器裡，
        // 再把按鍵丟進輸入管線就等於自己動手改 SQL。
        if (!target.Scope.IsKeyboardFocusWithin)
        {
            Keyboard.Focus(target.FocusTarget);
        }

        if (Keyboard.FocusedElement is not { } focused || !target.Scope.IsKeyboardFocusWithin)
        {
            return true;
        }

        if (command is not null)
        {
            if (command.CanExecute(null, focused))
            {
                command.Execute(null, focused);
            }

            return true;
        }

        if (PresentationSource.FromVisual(target.Scope) is { } source &&
            !Raise(source, Keyboard.PreviewKeyDownEvent, key!.Value))
        {
            Raise(source, Keyboard.KeyDownEvent, key.Value);
        }

        return true;
    }

    /// <summary>現代管線收到的 Esc；這個編輯器沒有 Popup 握著鍵盤時回 false。</summary>
    /// <param name="view">現代管線給的是 <see cref="ITextView"/>；比的是同一個執行個體。</param>
    public static bool TryCancel(ITextView view)
    {
        if (For(view) is not { } target)
        {
            return false;
        }

        target.Cancel();
        return true;
    }

    private static IShellKeyTarget? For(ITextView view) =>
        _active is { } target && ReferenceEquals(target.View, view) ? target : null;

    /// <summary>
    /// 補一次真正按鍵會有的通道與冒泡。
    /// </summary>
    /// <remarks>
    /// <c>InputManager.ProcessInput</c> 推一個 <c>KeyEventArgs</c> 只會發<b>那一個</b>
    /// 事件，不像真正的按鍵先通道再冒泡。只發 <c>KeyDown</c> 時，文字方塊的編輯鍵仍然
    /// 正常（那些是 <c>KeyDown</c> 的類別處理），但掛在 <c>PreviewKeyDown</c> 的
    /// ↑↓／Enter／Esc 完全收不到。因此兩個階段都補，並尊重通道階段的 <c>Handled</c>。
    /// </remarks>
    private static bool Raise(PresentationSource source, RoutedEvent routedEvent, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = routedEvent
        };
        InputManager.Current.ProcessInput(args);
        return args.Handled;
    }
}
