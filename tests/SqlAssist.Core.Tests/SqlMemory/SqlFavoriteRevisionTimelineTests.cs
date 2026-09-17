using System;
using System.Linq;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlFavoriteRevisionTimelineTests
{
    private static readonly Guid FavoriteId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    private static SqlFavoriteRevisionItem Item(int minute, bool current = false, string? content = null) =>
        new(Guid.NewGuid(), content ?? "c" + minute, Start.AddMinutes(minute), SqlRevisionReason.Favorite, current, "SELECT " + minute, 8);

    private static SqlMemoryPage<SqlFavoriteRevisionItem> Page(string? cursor, params SqlFavoriteRevisionItem[] items) => new(items, cursor);

    [Fact]
    public void RequestContractRejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => new SqlFavoriteRevisionRequest(Guid.Empty, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlFavoriteRevisionRequest(FavoriteId, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlFavoriteRevisionRequest(FavoriteId, 201));
        var request = new SqlFavoriteRevisionRequest(FavoriteId, 200, "next");
        Assert.Equal((FavoriteId, 200, "next"), (request.FavoriteId, request.PageSize, request.Cursor));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlFavoriteRevisionTimeline(FavoriteId, "c", 0));
    }

    [Fact]
    public void PagesFollowTheCursorAndRejectStaleGenerations()
    {
        var timeline = new SqlFavoriteRevisionTimeline(FavoriteId, "c3", 20, pageSize: 2);
        Assert.Equal(SqlMemoryFooterKind.Hidden, timeline.Footer().Kind);
        var first = timeline.BeginFirstPage(out var generation);
        Assert.Null(first.Cursor);
        Assert.Equal(2, first.PageSize);
        Assert.Null(timeline.BeginNextPage(out _));
        var initial = timeline.Accept(generation, Page("p2", Item(3, true), Item(2)));
        Assert.NotNull(initial);
        // 開窗的第一頁不播進場，否則整份清單一起跳動。
        Assert.Empty(initial);
        Assert.Equal(SqlMemoryFooterKind.More, timeline.Footer().Kind);

        var next = timeline.BeginNextPage(out var nextGeneration);
        Assert.Equal("p2", next?.Cursor);
        Assert.Equal(SqlMemoryFooterKind.Loading, timeline.Footer().Kind);
        var stale = timeline.BeginFirstPage(out var reload);
        Assert.Null(timeline.Accept(nextGeneration, Page(null, Item(1))));
        Assert.NotNull(timeline.Accept(reload, Page(null, timeline.Items.ToArray())));
        Assert.Equal(2, timeline.Items.Count);
        Assert.Equal(SqlMemoryFooterKind.End, timeline.Footer().Kind);
        Assert.Null(stale.Cursor);
    }

    [Fact]
    public void ReloadAfterRevertReportsOnlyNewVersionsAndDeduplicates()
    {
        var timeline = new SqlFavoriteRevisionTimeline(FavoriteId, "c2", 20);
        var old = Item(1, content: "c1");
        var current = Item(2, true);
        timeline.Accept(Begin(timeline), Page(null, current, old));
        var reverted = Item(3, true, "c1");
        var appeared = timeline.Accept(Begin(timeline), Page(null, reverted, current with { IsCurrent = false }, old, old));
        Assert.Equal(new[] { reverted.RevisionId }, appeared!.Select(item => item.RevisionId));
        Assert.Equal(3, timeline.Items.Count);
        Assert.Equal("c1", timeline.CurrentContentId);

        static long Begin(SqlFavoriteRevisionTimeline timeline) { timeline.BeginFirstPage(out var generation); return generation; }
    }

    [Fact]
    public void ComparesOlderVersionsWithCurrentAndCurrentWithItsPredecessor()
    {
        var timeline = new SqlFavoriteRevisionTimeline(FavoriteId, "c3", 20);
        var current = Item(3, true);
        var middle = Item(2);
        var first = Item(1, content: "c3");
        timeline.BeginFirstPage(out var generation);
        timeline.Accept(generation, Page(null, current, middle, first));

        Assert.Equal(new SqlFavoriteRevisionComparison("c2", "c3", "此版本 → 目前版本"), timeline.ComparisonFor(middle));
        Assert.Equal(new SqlFavoriteRevisionComparison("c2", "c3", "前一版 → 目前版本"), timeline.ComparisonFor(current));
        Assert.True(timeline.CanRevert(middle));
        Assert.False(timeline.CanRevert(current));
        // 內容與目前版本相同的舊版本回溯只會多一份一樣的版本。
        Assert.False(timeline.CanRevert(first));

        var alone = new SqlFavoriteRevisionTimeline(FavoriteId, "c3", 20);
        alone.BeginFirstPage(out var single);
        alone.Accept(single, Page(null, current));
        Assert.Null(alone.ComparisonFor(current));

        timeline.UseCurrentContent("c2");
        Assert.False(timeline.CanRevert(middle));
    }

    [Fact]
    public void FooterStatesRetentionHonestly()
    {
        var timeline = new SqlFavoriteRevisionTimeline(FavoriteId, "c2", 2);
        timeline.BeginFirstPage(out var generation);
        timeline.Accept(generation, Page(null, Item(2, true)));
        var partial = timeline.Footer();
        Assert.Equal("共保留 1 版", partial.Summary);
        Assert.Contains("最多保留最近 2 版", partial.Hint);

        timeline.BeginFirstPage(out generation);
        timeline.Accept(generation, Page(null, Item(2, true), Item(1)));
        var full = timeline.Footer();
        Assert.Contains("只保留最近 2 版", full.Hint);
        Assert.Contains("已由維護回收", full.Hint);

        var removed = new SqlFavoriteRevisionTimeline(FavoriteId, "c", 20);
        removed.BeginFirstPage(out generation);
        removed.Accept(generation, Page(null));
        Assert.Equal(SqlMemoryFooterKind.Empty, removed.Footer().Kind);
    }
}
