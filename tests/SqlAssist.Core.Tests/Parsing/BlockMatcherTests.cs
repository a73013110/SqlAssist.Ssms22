using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class BlockMatcherTests
{
    [Theory]
    [InlineData("BEGIN END", BlockKind.Block)]
    [InlineData("begin try\r\nSELECT 1;\r\nend try", BlockKind.Try)]
    [InlineData("BEGIN CATCH\nSELECT 1;\nEND CATCH", BlockKind.Catch)]
    [InlineData("CASE WHEN 1=1 THEN 2 END", BlockKind.Case)]
    [InlineData("(1 + 2)", BlockKind.Parenthesis)]
    [InlineData("[Lib_Reader]]Tag]", BlockKind.Bracket)]
    public void 支援基本配對(string sql, BlockKind kind)
    {
        var matcher = new BlockMatcher(sql);
        var pair = Assert.Single(matcher.Pairs);
        Assert.Equal(kind, pair.Kind);
        foreach (var span in pair.Opening.Concat(pair.Closing))
            for (var i = span.Start; i < span.End; i++) Assert.Same(pair, matcher.FindPairAt(i));
        Assert.Null(matcher.FindPairAt(-1));
        Assert.Null(matcher.FindPairAt(sql.Length));
        Assert.Null(matcher.GetEnclosingBlock(sql.Length));
    }

    [Theory]
    [InlineData("BEGIN TRAN")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("BEGIN DISTRIBUTED TRAN")]
    [InlineData("BEGIN /*外 /*內*/ */ DISTRIBUTED -- 註解\nTRANSACTION")]
    [InlineData("BEGIN DIALOG CONVERSATION @Reader")]
    [InlineData("BEGIN CONVERSATION TIMER (@Reader) TIMEOUT=1")]
    public void 非區塊敘述不吞掉外層結尾(string statement)
    {
        var sql = "BEGIN " + statement + "; END";
        var matcher = new BlockMatcher(sql);
        Assert.Equal(sql.Length, matcher.FindPairAt(0)!.Span.End);
        Assert.Null(matcher.FindPairAt(6));
    }

    [Fact]
    public void Broker結尾不偷走外層END()
    {
        var sql = "BEGIN END CONVERSATION @Reader; END";
        var pair = Assert.Single(new BlockMatcher(sql).Pairs);
        Assert.Equal(sql.Length - 3, pair.Closing[0].Start);
    }

    [Fact]
    public void 巢狀祖先由內而外且結束後回到父層()
    {
        var sql = "BEGIN BEGIN TRY IF 1=1 BEGIN SELECT CASE WHEN (1)=1 THEN 2 END; END END TRY END";
        var matcher = new BlockMatcher(sql);
        var position = sql.IndexOf("(1)", StringComparison.Ordinal) + 1;
        Assert.Equal(new[] { BlockKind.Parenthesis, BlockKind.Case, BlockKind.Block, BlockKind.Try, BlockKind.Block },
            matcher.GetAncestors(position).Select(p => p.Kind));
        Assert.Equal(BlockKind.Case, matcher.GetEnclosingBlock(position + 2)!.Kind);
        Assert.Null(matcher.FindPairAt(position));
    }

    [Fact]
    public void 複合關鍵字之間的註解不會高亮()
    {
        var sql = "BEGIN /* CASE END */ TRY SELECT 1; END -- BEGIN\nTRY";
        var matcher = new BlockMatcher(sql);
        var pair = Assert.Single(matcher.Pairs);
        Assert.Equal(BlockKind.Try, pair.Kind);
        Assert.Equal(2, pair.Opening.Count);
        Assert.Null(matcher.FindPairAt(sql.IndexOf("CASE", StringComparison.Ordinal)));
        Assert.Null(matcher.FindPairAt(5));
    }

    [Theory]
    [InlineData("BEGIN")]
    [InlineData("END")]
    [InlineData("BEGIN TRY END CATCH")]
    [InlineData("BEGIN CATCH END TRY")]
    [InlineData("BEGIN TRY END")]
    [InlineData("[Lib_Reader")]
    [InlineData("[Lib_Reader]]")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("BEGIN\nGO\nEND")]
    [InlineData("(\nGO\n)")]
    public void 忽略未配對殘留(string sql) => Assert.Empty(new BlockMatcher(sql).Pairs);

    [Fact]
    public void 方括號只配真正的外框()
    {
        var sql = "[Lib_Reader]]Tag]";
        var matcher = new BlockMatcher(sql);
        Assert.Null(matcher.FindPairAt(sql.IndexOf("]]", StringComparison.Ordinal)));
        Assert.NotNull(matcher.FindPairAt(sql.Length - 1));
    }

    [Fact]
    public void 同名GO識別字不是批次分隔()
    {
        Assert.Single(new BlockMatcher("BEGIN SELECT GO FROM Lib_Reader; END").Pairs);
    }

    [Fact]
    public void 字串不能把BEGIN和TRY串起來()
    {
        Assert.Equal(BlockKind.Block, new BlockMatcher("BEGIN 'x' TRY END").FindPairAt(0)!.Kind);
    }

    [Fact]
    public void 相鄰區塊使用半開區間()
    {
        var matcher = new BlockMatcher("()()");
        Assert.Same(matcher.Pairs[1], matcher.GetEnclosingBlock(2));
        Assert.Same(matcher.Pairs[1], matcher.FindPairAt(2));
        Assert.Null(matcher.GetEnclosingBlock(4));
    }

    [Fact]
    public void 區間查詢與逐筆判斷完全一致且不重複()
    {
        const string sql = "BEGIN SELECT CASE WHEN ((1))=1 THEN [Lib_Reader] END; BEGIN TRY (2) END TRY END ()";
        var matcher = new BlockMatcher(sql);
        for (var start = 0; start <= sql.Length; start++)
            for (var length = 0; length <= sql.Length - start; length++)
            {
                var expected = matcher.Pairs.Where(p => length > 0 && p.Span.Start < start + length && p.Span.End > start)
                    .OrderBy(p => p.Span.Start).ToArray();
                var actual = matcher.GetIntersectingBlocks(start, length).OrderBy(p => p.Span.Start).ToArray();
                Assert.Equal(expected, actual);
            }
    }

    [Fact]
    public void 非法交叉不產生交叉的祖先()
    {
        Assert.Single(new BlockMatcher("BEGIN ( END )").Pairs);
    }

    [Fact]
    public void 已完成的內層不受外層未完成影響()
    {
        Assert.Single(new BlockMatcher("BEGIN BEGIN END").Pairs);
    }

    [Fact]
    public void 空白與取消安全()
    {
        Assert.Empty(new BlockMatcher("").GetAncestors(0));
        Assert.Throws<ArgumentNullException>(() => new BlockMatcher(null!));
        Assert.Throws<OperationCanceledException>(() => new BlockMatcher("BEGIN END", new CancellationToken(true)));
    }

    [Fact]
    public void 深層與大量查詢不使用遞迴或線性掃描()
    {
        const int depth = 10000;
        var matcher = new BlockMatcher(new string('(', depth) + "1" + new string(')', depth));
        Assert.Equal(depth, matcher.Pairs.Count);
        Assert.Equal(depth, matcher.GetAncestors(depth).Count);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 100000; i++)
        {
            Assert.NotNull(matcher.FindPairAt(i % depth));
            Assert.NotNull(matcher.GetEnclosingBlock(i % depth));
        }
        // 寬鬆上限只擋退化成每次遍歷萬筆；不是拿測試環境當正式效能承諾。
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void 游標熱路徑不為每次二分搜尋配置委派()
    {
        var matcher = new BlockMatcher("BEGIN SELECT (1) END");
        // net10 的分層 JIT 可能在首次迴圈配置資料；先暖機，量測只針對穩態查詢配置。
        for (var i = 0; i < 5000; i++)
        {
            _ = matcher.FindPairAt(0);
            _ = matcher.GetEnclosingBlock(8);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
        {
            _ = matcher.FindPairAt(0);
            _ = matcher.GetEnclosingBlock(8);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
    }
}
