using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class BlockDisplayRulesTests
{
    [Fact]
    public void 同行預設不塗背景且不影響跨行()
    {
        var settings = new SqlAssistSettings();
        Assert.False(BlockDisplayRules.ShowRange(settings, sameLine: true, highContrast: false));
        Assert.True(BlockDisplayRules.ShowRange(settings, sameLine: false, highContrast: false));
        Assert.True(BlockDisplayRules.ShowRange(new SqlAssistSettings { BlockSameLineBackground = true }, true, false));
    }

    [Fact]
    public void 兩個呈現層可獨立開關()
    {
        Assert.True(BlockDisplayRules.ShowRange(new SqlAssistSettings { BlockKeywordHighlight = false }, false, false));
        Assert.False(BlockDisplayRules.ShowRange(new SqlAssistSettings { BlockRangeBackground = false }, false, false));
    }

    [Fact]
    public void 總開關與高對比不塗背景()
    {
        Assert.False(BlockDisplayRules.ShowRange(new SqlAssistSettings { Enabled = false }, false, false));
        Assert.False(BlockDisplayRules.ShowRange(new SqlAssistSettings { BlockMatchingEnabled = false }, false, false));
        Assert.False(BlockDisplayRules.ShowRange(new SqlAssistSettings { BlockSameLineBackground = true }, false, true));
    }

    [Fact]
    public void 關閉CASE與括號不影響一般區塊()
    {
        var settings = new SqlAssistSettings { BlockMatchCase = false, BlockMatchParentheses = false };
        Assert.True(BlockDisplayRules.IsKindEnabled(BlockKind.Block, settings));
        Assert.False(BlockDisplayRules.IsKindEnabled(BlockKind.Case, settings));
        Assert.False(BlockDisplayRules.IsKindEnabled(BlockKind.Parenthesis, settings));
        Assert.False(BlockDisplayRules.IsKindEnabled(BlockKind.Bracket, settings));
        Assert.False(BlockDisplayRules.IsKindEnabled(BlockKind.Try, new SqlAssistSettings { Enabled = false }));
    }
}
