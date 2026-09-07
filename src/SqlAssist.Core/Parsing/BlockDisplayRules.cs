using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Parsing;

/// <summary>共用呈現篩選；關掉某一類只影響顯示，不改變 END 的解析歸屬。</summary>
public static class BlockDisplayRules
{
    public static bool IsKindEnabled(BlockKind kind, SqlAssistSettings settings) =>
        settings.Enabled && settings.BlockMatchingEnabled && kind switch
        {
            BlockKind.Case => settings.BlockMatchCase,
            BlockKind.Parenthesis or BlockKind.Bracket => settings.BlockMatchParentheses,
            _ => true
        };

    public static bool ShowRange(SqlAssistSettings settings, bool sameLine, bool highContrast) =>
        settings.Enabled && settings.BlockMatchingEnabled && settings.BlockRangeBackground &&
        (!sameLine || settings.BlockSameLineBackground) && !highContrast;
}
