using System;

namespace SqlAssist.Ssms22.Commands;

/// <remarks>
/// 這些數值是使用者自訂鍵盤快速鍵的定址方式，不要為了整齊而重新編號——
/// 換掉一個 ID 等於安靜地解除他綁在上面的快速鍵。
/// 移除命令留下的空號（0x0101–0x0104、0x0203–0x0205、0x0207）刻意不回收。
///
/// 同一組數值也寫在 <c>Menus.vsct</c> 的 IDSymbol 與 <c>SqlAssist.registration.json</c>
/// 的按鈕（十進位）裡。三者分歧不會編譯失敗，按鈕就只是按不到，
/// 因此由 <c>tools/Test-CommandTable.ps1</c> 在建置前交叉驗證。
/// </remarks>
internal static class CommandIds
{
    public const string CommandSetString = "4a188946-9364-4f07-af7e-97f3bd7ca7a7";
    public static readonly Guid CommandSet = new(CommandSetString);

    public const int ToggleEnabled = 0x0100;
    public const int ToggleSuggestions = 0x0105;
    public const int ShowDiagnostics = 0x0200;
    public const int OpenSettings = 0x0201;
    public const int RefreshSuggestions = 0x0202;
    public const int ShowObjectStructure = 0x0206;

    /// <summary>選單項目，同時也是設定頁上的按鈕（註冊檔寫成十進位的 521）。</summary>
    public const int ManageSnippets = 0x0209;

    /// <summary>設定頁上的按鈕，不出現在選單（註冊檔寫成十進位的 520）。</summary>
    public const int OpenDiagnosticsLog = 0x0208;
}
