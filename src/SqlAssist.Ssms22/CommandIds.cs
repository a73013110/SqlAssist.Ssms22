using System;

namespace SqlAssist.Ssms22;

internal static class CommandIds
{
    public const string CommandSetString = "4a188946-9364-4f07-af7e-97f3bd7ca7a7";
    public static readonly Guid CommandSet = new(CommandSetString);

    public const int ToggleEnabled = 0x0100;
    public const int ToggleTabExpansion = 0x0101;
    public const int ToggleKeywordUppercase = 0x0102;
    public const int ToggleObjectPicker = 0x0103;
    public const int ToggleResultGridCommands = 0x0104;
    public const int ToggleSuggestions = 0x0105;
    public const int ShowDiagnostics = 0x0200;
    public const int OpenSettings = 0x0201;
    public const int RefreshSuggestions = 0x0202;
    public const int ToggleAsyncCompletionProbe = 0x0203;
}
