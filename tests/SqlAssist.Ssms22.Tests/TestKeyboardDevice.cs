using System.Windows.Input;

namespace SqlAssist.Ssms22.Tests;

/// <summary>
/// 修飾鍵狀態由測試指定的鍵盤裝置。
/// </summary>
/// <remarks>
/// <c>Keyboard.PrimaryDevice</c> 讀的是執行緒的 Win32 按鍵狀態，會混入實體鍵盤；
/// 用快捷鍵觸發 push 時還按著 Ctrl，合成的 Enter 就被當成 Ctrl+Enter。
/// 產品碼讀 <c>KeyEventArgs.KeyboardDevice</c>，測試改傳這個裝置就不再依賴 OS 狀態。
/// </remarks>
internal sealed class TestKeyboardDevice : KeyboardDevice
{
    private readonly ModifierKeys _modifiers;

    public TestKeyboardDevice(ModifierKeys modifiers = ModifierKeys.None)
        : base(InputManager.Current)
    {
        _modifiers = modifiers;
    }

    protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => StateOf(ModifierKeys.Control),
        Key.LeftShift or Key.RightShift => StateOf(ModifierKeys.Shift),
        Key.LeftAlt or Key.RightAlt => StateOf(ModifierKeys.Alt),
        Key.LWin or Key.RWin => StateOf(ModifierKeys.Windows),
        _ => KeyStates.None,
    };

    private KeyStates StateOf(ModifierKeys modifier) =>
        (_modifiers & modifier) == modifier ? KeyStates.Down : KeyStates.None;
}
