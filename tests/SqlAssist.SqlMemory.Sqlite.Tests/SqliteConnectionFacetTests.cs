using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        async Task<IReadOnlyList<string>> Servers(SqlConnectionFacetSort sort) => await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, false, sort: sort), Token);
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(SqlConnectionFacetSort.Recent));
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(SqlConnectionFacetSort.Oldest));
        Assert.Equal(new[] { "BranchA", "BranchB" }, await Servers(SqlConnectionFacetSort.Alphabetical));
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(SqlConnectionFacetSort.ReverseAlphabetical));
        Assert.Equal(new[] { "Main" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, true, "BranchA"), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, true, "brancha"), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, true, "' OR 1=1--"), Token));
    }

    [Fact]
    public async Task FacetsHaveBoundedPagesAndCancellation()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 105; i++)
            await store.Process(repository, store.Capture(i, seconds: i, context: new SqlConnectionLabel("Branch" + i.ToString("D3"), "Main")), Token);
        var first = await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, false), Token);
        Assert.Equal(SqlConnectionFacetRequest.PageSize + 1, first.Count);
        var next = await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, false, offset: 100), Token);
        Assert.Equal(5, next.Count);
        Assert.Equal(105, first.Take(100).Concat(next).Distinct().Count());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, false), new CancellationToken(true)));
    }

    [Fact]
    public async Task FavoriteScopesAndFacetDtosRoundTripThroughIsolation()
    {
        using var store = new SqliteTestStore();
        using var repository = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, Token);
        await new SqlCaptureCommitter(repository, new SqlCapturePlanner()).ProcessAsync(
            store.Capture(context: new SqlConnectionLabel("HistoryOnly", "History")), SqliteTestStore.Policy, Token);
        var row = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        foreach (var scope in new[] { SqlFavoriteScope.Global, SqlFavoriteScope.Server, SqlFavoriteScope.Database })
        {
            var query = new SqlFavorite(Guid.NewGuid(), "借閱", "", row.RevisionId!.Value, scope,
                scope == SqlFavoriteScope.Global ? null : new SqlConnectionLabel("Library", scope == SqlFavoriteScope.Database ? "Main" : ""));
            await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query, null), Token);
        }
        Assert.Equal(new[] { "HistoryOnly" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(false, SqlFavoriteScope.Global, false), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(true, SqlFavoriteScope.Global, false), Token));
        Assert.Equal(new[] { "Library" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(true, SqlFavoriteScope.Server, false), Token));
        Assert.Equal(new[] { "Main" }, await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(true, SqlFavoriteScope.Database, true, "Library"), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new SqlConnectionFacetRequest(true, SqlFavoriteScope.Server, true, "Library"), Token));
    }
}
