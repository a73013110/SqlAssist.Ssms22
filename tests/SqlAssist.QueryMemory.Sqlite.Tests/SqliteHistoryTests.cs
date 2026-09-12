using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameTimestampKeysetPagingHasNoMissingOrDuplicateEntries()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 23; i++) await store.Process(repository, store.Capture(i), Token);
        var ids = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await repository.ReadHistoryAsync(new QueryHistoryRequest(5, QueryHistoryKind.Executed, cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 5);
            foreach (var item in page.Items) Assert.True(ids.Add(item.ItemId));
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(23, ids.Count);
    }

    [Fact]
    public async Task ServerDatabaseAndHalfOpenTimeFiltersUseExecutionContext()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(1, context: new QueryConnectionContext("LibraryServer", "Library")), Token);
        await store.Process(repository, store.Capture(2, seconds: 1, context: new QueryConnectionContext("LibraryServer", "Archive")), Token);
        await store.Process(repository, store.Capture(3, seconds: 2, context: new QueryConnectionContext("LibraryServer", "Library")), Token);
        await store.Process(repository, store.Capture(4, seconds: 3, context: new QueryConnectionContext("OtherServer", "Library")), Token);
        var page = await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Executed,
            server: "LibraryServer", database: "Library", since: SqliteTestStore.Start, until: SqliteTestStore.Start.AddSeconds(2)), Token);
        Assert.Single(page.Items);
        Assert.Equal(SqliteTestStore.Start, page.Items[0].CreatedAt);
        Assert.Equal(3, (await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Executed, database: "Library"), Token)).Items.Count);
        Assert.Empty((await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Pinned), Token)).Items);
    }

    [Fact]
    public async Task SearchIsLiteralCaseSensitiveAndIncludesTextBeyondPreview()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var sql = new string(' ', 300) + "SELECT N'Lib_Reader%📚';\0END";
        await store.Process(repository, store.Capture(sql: sql), Token);
        foreach (var search in new[] { "Lib_Reader%", "📚", "\0END", " ", "" })
        {
            var page = await repository.ReadHistoryAsync(new QueryHistoryRequest(10, QueryHistoryKind.Executed, search: search), Token);
            Assert.Single(page.Items);
            Assert.InRange(page.Items[0].Preview.Length, 0, 240);
        }
        foreach (var search in new[] { "lib_reader", "' OR 1=1--", "DoesNotExist" })
            Assert.Empty((await repository.ReadHistoryAsync(new QueryHistoryRequest(10, QueryHistoryKind.Executed, search: search), Token)).Items);
    }

    [Fact]
    public async Task CursorCannotBeReusedWithDifferentFiltersOrDatabase()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 3; i++) await store.Process(repository, store.Capture(i), Token);
        var page = await repository.ReadHistoryAsync(new QueryHistoryRequest(1, QueryHistoryKind.Executed), Token);
        Assert.NotNull(page.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadHistoryAsync(
            new QueryHistoryRequest(1, QueryHistoryKind.Executed, server: "LibraryServer", cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadHistoryAsync(new QueryHistoryRequest(1, cursor: "!invalid!"), Token));
        using var other = new SqliteTestStore();
        var otherRepository = await other.Open(Token);
        await Assert.ThrowsAsync<ArgumentException>(() => otherRepository.ReadHistoryAsync(
            new QueryHistoryRequest(1, QueryHistoryKind.Executed, cursor: page.NextCursor), Token));
        Assert.Equal(2, (await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Executed, cursor: page.NextCursor), Token)).Items.Count);
    }

    [Fact]
    public async Task CanceledQueriesAndCommitsDoNotWriteAnything()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadHistoryAsync(new QueryHistoryRequest(20), cancellation.Token));
        var write = new QueryRevisionEngine().Prepare(store.Capture(), null, SqliteTestStore.Policy);
        Assert.NotNull(write);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CommitAsync(write, cancellation.Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contents;"));
    }

    [Fact]
    public async Task BusyWriterFailsWithinConfiguredTimeoutWithoutPartialData()
    {
        using var store = new SqliteTestStore();
        var repository = await SqliteQueryMemoryRepository.OpenAsync(store.Path, Token, busyTimeoutSeconds: 1);
        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        blocker.Open();
        using var transaction = blocker.BeginTransaction(deferred: false);
        var error = await Assert.ThrowsAsync<SqliteException>(() => store.Process(repository, store.Capture(), Token));
        Assert.Equal(5, error.SqliteErrorCode);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contents;"));
    }

    [Fact]
    public async Task FilteredKeysetCanUseCompositeIndexWithoutSortingEntireHistory()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"EXPLAIN QUERY PLAN SELECT EntryKey FROM History WHERE Server='LibraryServer' AND DatabaseName='Library'
AND (CreatedAt,EntryKey) < (100,'e00000000000000000000000000000000') ORDER BY CreatedAt DESC,EntryKey DESC LIMIT 20;";
        using var reader = command.ExecuteReader();
        var plans = new List<string>();
        while (reader.Read()) plans.Add(reader.GetString(3));
        Assert.Contains(plans, plan => plan.Contains("IX_History_ServerDatabaseTime"));
        Assert.DoesNotContain(plans, plan => plan.Contains("TEMP B-TREE"));
    }
}
