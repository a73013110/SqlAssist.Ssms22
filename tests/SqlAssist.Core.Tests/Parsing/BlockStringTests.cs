using System;
using System.Linq;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class BlockStringTests
{
    [Theory]
    [InlineData("''", 0)]
    [InlineData("'Loan'", 0)]
    [InlineData("N'借閱😀'", 1)]
    [InlineData("n''", 1)]
    [InlineData("'Loan''Copy'", 0)]
    [InlineData("''''", 0)]
    [InlineData("'BEGIN\nEND ( ) [ ] -- /* */'", 0)]
    public void 只高亮完整字串外框(string sql, int opening)
    {
        var matcher = new BlockMatcher(sql);
        var pair = Assert.Single(matcher.Pairs);
        Assert.Equal(BlockKind.String, pair.Kind);
        Assert.Equal(opening, pair.Opening[0].Start);
        Assert.Equal(sql.Length - 1, pair.Closing[0].Start);
        for (var i = 0; i < sql.Length; i++)
            Assert.Equal(i == opening || i == sql.Length - 1, matcher.FindPairAt(i) is not null);
        Assert.Null(matcher.FindPairAt(sql.Length));
    }

    [Theory]
    [InlineData("'")]
    [InlineData("'Loan")]
    [InlineData("'Loan''")]
    [InlineData("'''")]
    [InlineData("N'")]
    [InlineData("N'Loan''")]
    [InlineData("-- 'Loan'\n/* N'Copy' */")]
    [InlineData("\"'Loan'\"")]
    public void 未閉合或註解識別字內的引號不配對(string sql) => Assert.Empty(new BlockMatcher(sql).Pairs);

    [Fact]
    public void 字串內容不干擾外層且可由種類開關過濾()
    {
        const string sql = "BEGIN SELECT N'BEGIN '' END'; END";
        var matcher = new BlockMatcher(sql);
        var position = sql.IndexOf("''", StringComparison.Ordinal);
        Assert.Equal(new[] { BlockKind.String, BlockKind.Block }, matcher.GetAncestors(position).Select(p => p.Kind));
        Assert.Null(matcher.FindPairAt(position));
        Assert.Equal(BlockKind.Block, BlockDisplayRules.FindContext(matcher, position,
            new SqlAssistSettings { BlockMatchParentheses = false })!.Kind);
        Assert.True(BlockDisplayRules.IsSymbol(BlockKind.String));
    }

    [Fact]
    public void 祖先篩選不為深層查詢建立清單()
    {
        var matcher = new BlockMatcher("BEGIN " + new string('(', 500) + "1" + new string(')', 500) + " END");
        Func<BlockPair, bool> accepts = p => p.Kind == BlockKind.Block;
        _ = matcher.FindEnclosingBlock(507, accepts);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _ = matcher.FindEnclosingBlock(507, accepts);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
        var settings = new SqlAssistSettings { BlockMatchParentheses = false };
        _ = BlockDisplayRules.FindContext(matcher, 507, settings);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _ = BlockDisplayRules.FindContext(matcher, 507, settings);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
    }
}
