using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using SqlAssist.QueryMemory.Hosting;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteConnectionFacetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NamesAreDistinctExactScopedAndSortedWithoutDependingOnFirstHistoryPage()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(1, seconds: 0, context: new QueryConnectionContext("BranchB", "Archive")), Token);
        await store.Process(repository, store.Capture(2, seconds: 1, context: new QueryConnectionContext("BranchA", "Main")), Token);
        await store.Process(repository, store.Capture(3, seconds: 2, context: new QueryConnectionContext("BranchB", "Main")), Token);
        async Task<string[]> Servers(QueryConnectionSort sort) => await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false, sort: sort), Token);
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(QueryConnectionSort.Recent));
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(QueryConnectionSort.Oldest));
        Assert.Equal(new[] { "BranchA", "BranchB" }, await Servers(QueryConnectionSort.Alphabetical));
        Assert.Equal(new[] { "BranchB", "BranchA" }, await Servers(QueryConnectionSort.ReverseAlphabetical));
        Assert.Equal(new[] { "Main" }, await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, true, "BranchA"), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, true, "brancha"), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, true, "' OR 1=1--"), Token));
    }

    [Fact]
    public async Task FacetsHaveBoundedPagesAndCancellation()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 105; i++)
            await store.Process(repository, store.Capture(i, seconds: i, context: new QueryConnectionContext("Branch" + i.ToString("D3"), "Main")), Token);
        var first = await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false), Token);
        Assert.Equal(101, first.Length);
        var next = await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false, offset: 100), Token);
        Assert.Equal(5, next.Length);
        Assert.Equal(105, first.Take(100).Concat(next).Distinct().Count());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false), new CancellationToken(true)));
    }

    [Fact]
    public async Task FavoriteScopesAndFacetDtosRoundTripThroughIsolation()
    {
        using var store = new SqliteTestStore();
        using var repository = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, Token);
        await new QueryMemoryProcessor(repository, new QueryRevisionEngine()).ProcessAsync(
            store.Capture(context: new QueryConnectionContext("HistoryOnly", "History")), SqliteTestStore.Policy, Token);
        var row = Assert.Single((await repository.ReadHistoryAsync(new QueryHistoryRequest(10, QueryHistoryKind.Executed), Token)).Items);
        foreach (var scope in new[] { FavoriteQueryScope.Global, FavoriteQueryScope.Server, FavoriteQueryScope.Database })
        {
            var query = new FavoriteQuery(Guid.NewGuid(), "借閱", "", row.RevisionId!.Value, scope,
                scope == FavoriteQueryScope.Global ? null : new QueryConnectionContext("Library", scope == FavoriteQueryScope.Database ? "Main" : ""));
            await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query, null), Token);
        }
        Assert.Equal(new[] { "HistoryOnly" }, await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(false, FavoriteQueryScope.Global, false), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(true, FavoriteQueryScope.Global, false), Token));
        Assert.Equal(new[] { "Library" }, await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(true, FavoriteQueryScope.Server, false), Token));
        Assert.Equal(new[] { "Main" }, await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(true, FavoriteQueryScope.Database, true, "Library"), Token));
        Assert.Empty(await repository.ReadConnectionFacetsAsync(new QueryConnectionFacetRequest(true, FavoriteQueryScope.Server, true, "Library"), Token));
    }
}
