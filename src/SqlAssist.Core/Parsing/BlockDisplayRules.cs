using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Parsing;

/// <summary>共用呈現篩選；關掉某一類只影響顯示，不改變 END 的解析歸屬。</summary>
public static class BlockDisplayRules
{
    public static bool IsSymbol(BlockKind kind) => kind is BlockKind.Parenthesis or BlockKind.Bracket or BlockKind.String;

    /// <summary>沿既有祖先索引找最近可見種類，不重新掃 SQL；可排除不需塗色的同行小括號。</summary>
    public static BlockPair? FindContext(BlockMatcher matcher, int position, SqlAssistSettings settings,
        System.Func<BlockPair, bool>? accepts = null)
    {
        return matcher.FindEnclosingBlock(position, (Settings: settings, Accepts: accepts),
            static (pair, state) => IsKindEnabled(pair.Kind, state.Settings) && (state.Accepts is null || state.Accepts(pair)));
    }

    public static bool IsKindEnabled(BlockKind kind, SqlAssistSettings settings) =>
        settings.Enabled && settings.BlockMatchingEnabled && kind switch
        {
            BlockKind.Case => settings.BlockMatchCase,
            BlockKind.Parenthesis or BlockKind.Bracket or BlockKind.String => settings.BlockMatchParentheses,
            _ => true
        };

    public static bool ShowRange(SqlAssistSettings settings, bool sameLine, bool highContrast) =>
        settings.Enabled && settings.BlockMatchingEnabled && settings.BlockRangeBackground &&
        (!sameLine || settings.BlockSameLineBackground) && !highContrast;
}
