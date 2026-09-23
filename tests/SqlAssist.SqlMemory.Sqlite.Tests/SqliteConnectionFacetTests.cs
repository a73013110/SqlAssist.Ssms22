using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteConnectionFacetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NamesAreDistinctExactScopedAndSortedWithoutDependingOnFirstHistoryPage()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(1, seconds: 0, context: new SqlConnectionLabel("BranchB", "Archive")), Token);
        await store.Process(repository, store.Capture(2, seconds: 1, context: new SqlConnectionLabel("BranchA", "Main")), Token);
        await store.Process(repository, store.Capture(3, seconds: 2, context: new SqlConnectionLabel("BranchB", "Main")), Token);
        async Task<IReadOnlyList<string>> Servers(SqlConnectionFacetSort sort) => await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, false, sort: sort), Token);
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(SqlConnectionFacetSort.Recent));
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(SqlConnectionFacetSort.Oldest));
        Assert.Equal(new[] { "BranchA", "BranchB" }, await Servers(SqlConnectionFacetSort.Alphabetical));
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(SqlConnectionFacetSort.ReverseAlphabetical));
        Assert.Equal(new[] { "Main" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, true, new[] { "BranchA" }), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, true, new[] { "brancha" }), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, true, new[] { "' OR 1=1--" }), Token));
    }

    [Fact]
    public async Task FacetsHaveBoundedPagesAndCancellation()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 105; i++)
            await store.Process(repository, store.Capture(i, seconds: i, context: new SqlConnectionLabel("Branch" + i.ToString("D3"), "Main")), Token);
        var first = await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, false), Token);
        Assert.Equal(SqlConnectionFacetRequest.PageSize + 1, first.Count);
        var next = await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, false, offset: 100), Token);
        Assert.Equal(5, next.Count);
        Assert.Equal(105, first.Take(100).Concat(next).Distinct().Count());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, false), new CancellationToken(true)));
    }

    [Fact]
    public async Task FavoriteTagsAndFacetDtosRoundTripThroughIsolation()
    {
        using var store = new SqliteTestStore();
        using var repository = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, Token);
        await new SqlCaptureCommitter(repository, new SqlCapturePlanner()).ProcessAsync(
            store.Capture(context: new SqlConnectionLabel("HistoryOnly", "History")), SqliteTestStore.Policy, Token);
        var row = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        foreach (var (server, database) in new[] { ((string?)null, (string?)null), ("Library", null), ("Library", "Main"), (null, "Archive") })
        {
            var query = new SqlFavorite(Guid.NewGuid(), "借閱", "", row.RevisionId!.Value, server, database);
            await repository.SaveFavoriteAsync(new SqlFavoriteSave(query, null, SqliteTestStore.Start), Token);
        }
        // History 與收藏各讀各的名稱；收藏的標註不會混進 History 的篩選名單。
        Assert.Equal(new[] { "HistoryOnly" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, false), Token));
        Assert.Equal(new[] { "Library" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(true, false), Token));
        Assert.Equal(new[] { "Archive", "Main" }, await repository.ReadConnectionFacetsAsync(
            new SqlConnectionFacetRequest(true, true, sort: SqlConnectionFacetSort.Alphabetical), Token));
        Assert.Equal(new[] { "Main" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(true, true, new[] { "Library" }), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(true, true, new[] { "HistoryOnly" }), Token));
        var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(10, databases: new[] { "Archive" }), Token);
        Assert.Equal((null, "Archive"), (Assert.Single(page.Items).Favorite.Server, page.Items[0].Favorite.Database));
    }
}
