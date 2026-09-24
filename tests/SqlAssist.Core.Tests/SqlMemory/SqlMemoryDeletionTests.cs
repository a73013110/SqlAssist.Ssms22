using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryDeletionTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 24, 1, 2, 3, TimeSpan.Zero);

    private static SqlHistoryItem[] History(int count) => Enumerable.Range(0, count)
        .Select(index => new SqlHistoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "content", At,
            SqlHistoryFilter.Executions, "借閱查詢 " + index, "SELECT * FROM Loan;", null))
        .ToArray();

    private static SqlFavoriteItem Favorite(string name) =>
        new(new SqlFavorite(Guid.NewGuid(), name, null, Guid.NewGuid(), null, null), Guid.NewGuid(), "content",
            "SELECT * FROM Lib_Reader;", At);

    private static Task<SqlFavoriteWriteResult> NoFavorites(SqlFavoriteItem item, CancellationToken token) =>
        throw new InvalidOperationException("History 刪除不該碰收藏。");

    private static Task<int> NoHistory(IReadOnlyList<SqlHistoryItem> items, CancellationToken token) =>
        throw new InvalidOperationException("收藏移除不該碰 History。");

    [Fact]
    public async Task History分批送出且已不存在的也移出清單()
    {
        var items = History(SqlMemoryDeletion.ChunkSize + 3);
        var batches = new List<int>();
        var reported = new List<int>();

        var report = await SqlMemoryDeletion.Of(items).RunAsync((chunk, _) =>
        {
            batches.Add(chunk.Count);
            // 每批有一筆已經被別人刪掉。
            return Task.FromResult(chunk.Count - 1);
        }, NoFavorites, new SqlMemoryBulkTests.ImmediateProgress(reported), CancellationToken.None);

        Assert.Equal(new[] { SqlMemoryDeletion.ChunkSize, 3 }, batches);
        Assert.Equal(new[] { SqlMemoryDeletion.ChunkSize, SqlMemoryDeletion.ChunkSize + 3 }, reported);
        Assert.Equal(items.Select(item => item.ItemId), report.Removed);
        Assert.Equal(items.Length - 2, report.Deleted);
        Assert.Equal(2, report.Missing);
        Assert.False(report.IsCanceled);
        Assert.Equal("共刪除 201 筆。2 筆已經不存在。", report.Summary);
    }

    [Fact]
    public async Task 取消在批與批之間生效並回報已刪的部分()
    {
        using var cancel = new CancellationTokenSource();
        var items = History(SqlMemoryDeletion.ChunkSize * 3);

        var report = await SqlMemoryDeletion.Of(items).RunAsync((chunk, _) =>
        {
            cancel.Cancel();
            return Task.FromResult(chunk.Count);
        }, NoFavorites, null, cancel.Token);

        Assert.True(report.IsCanceled);
        Assert.Equal(SqlMemoryDeletion.ChunkSize, report.Removed.Count);
        Assert.StartsWith("取消前已刪除 200 筆", report.Summary);
    }

    [Fact]
    public async Task 交易中途取消那一批不算()
    {
        using var cancel = new CancellationTokenSource();
        var report = await SqlMemoryDeletion.Of(History(2)).RunAsync((_, token) =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(2);
        }, NoFavorites, null, cancel.Token);

        Assert.True(report.IsCanceled);
        Assert.Empty(report.Removed);
        Assert.Equal(0, report.Deleted);
    }

    [Fact]
    public async Task 收藏逐筆比對版本且衝突的留在清單()
    {
        var kept = Favorite("讀者清單");
        var removed = Favorite("館藏清單");

        var report = await SqlMemoryDeletion.Of(new[] { kept, removed }).RunAsync(NoHistory, (item, _) =>
            Task.FromResult(ReferenceEquals(item, kept) ? SqlFavoriteWriteResult.Conflict : SqlFavoriteWriteResult.Committed),
            null, CancellationToken.None);

        Assert.True(report.IsFavorites);
        Assert.Equal(new[] { removed.Favorite.FavoriteId }, report.Removed);
        Assert.Equal(1, report.Conflicts);
        Assert.Equal(0, report.Missing);
        Assert.Equal("共移除 1 筆。1 筆收藏已被修改或移除，未移除；請重新整理後再操作。History 不受影響。", report.Summary);
    }

    [Fact]
    public async Task 單筆成功不重述主體()
    {
        var history = await SqlMemoryDeletion.Of(History(1)).RunAsync((chunk, _) => Task.FromResult(chunk.Count),
            NoFavorites, null, CancellationToken.None);
        var missing = await SqlMemoryDeletion.Of(History(1)).RunAsync((_, _) => Task.FromResult(0),
            NoFavorites, null, CancellationToken.None);
        var favorite = await SqlMemoryDeletion.Of(new[] { Favorite("讀者清單") }).RunAsync(NoHistory,
            (_, _) => Task.FromResult(SqlFavoriteWriteResult.Conflict), null, CancellationToken.None);

        Assert.Equal("", history.Summary);
        Assert.Equal("這一筆已經不存在；已從清單移除。", missing.Summary);
        Assert.Equal("收藏已被修改或移除，未移除；請重新整理後再操作。", favorite.Summary);
    }
}
