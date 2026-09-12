using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteSavedQueryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<SavedQuery> CreateQuery(SqliteTestStore store, SqliteQueryMemoryRepository repository)
    {
        await store.Process(repository, store.Capture(), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        return new SavedQuery(Guid.NewGuid(), "讀者查詢", "圖書館範例", state.LatestRevision.RevisionId, SavedQueryScope.Global, null, false);
    }

    [Fact]
    public async Task CreateUpdateDeleteAndReopenDoNotMutateHistory()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        Assert.Equal(SavedQueryWriteResult.Committed, await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token));
        var saved = await (await store.Open(Token)).ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(saved);
        Assert.Equal(query, saved.Query);
        Assert.Equal("SELECT * FROM Lib_Reader;", saved.Preview);
        Assert.NotEqual(Guid.Empty, saved.Version);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        var changed = query with { Name = "標籤查詢", CurrentRevisionId = state.LatestRevision.RevisionId, Pinned = true,
            Scope = SavedQueryScope.Database, Connection = new QueryConnectionContext("LibraryServer", "Library") };
        Assert.Equal(SavedQueryWriteResult.Committed, await repository.WriteSavedQueryAsync(new SavedQueryWrite(changed, saved.Version), Token));
        var updated = await repository.ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(updated);
        Assert.Equal(changed, updated.Query);
        Assert.NotEqual(saved.Version, updated.Version);
        Assert.Equal(SavedQueryWriteResult.Committed, await repository.DeleteSavedQueryAsync(query.SavedQueryId, updated.Version, Token));
        Assert.Null(await repository.ReadSavedQueryAsync(query.SavedQueryId, Token));
        Assert.NotNull(await repository.ReadContentAsync(saved.ContentId, Token));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ConcurrentEditorsAndRecreatedIdsRejectStaleVersionsWithoutWritingContexts()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        var original = await repository.ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(original);
        var other = await store.Open(Token);
        var results = await Task.WhenAll(repository.WriteSavedQueryAsync(new SavedQueryWrite(query with { Name = "讀者一" }, original.Version), Token),
            other.WriteSavedQueryAsync(new SavedQueryWrite(query with { Name = "讀者二" }, original.Version), Token));
        Assert.Single(results, result => result == SavedQueryWriteResult.Committed);
        Assert.Single(results, result => result == SavedQueryWriteResult.Conflict);
        var contextual = query with { Scope = SavedQueryScope.Database, Connection = new QueryConnectionContext("LibraryServer", "Library") };
        Assert.Equal(SavedQueryWriteResult.Conflict, await repository.WriteSavedQueryAsync(new SavedQueryWrite(contextual, original.Version), Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts;"));
        Assert.Equal(SavedQueryWriteResult.Conflict, await repository.DeleteSavedQueryAsync(query.SavedQueryId, original.Version, Token));
        var current = await repository.ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(current);
        await repository.DeleteSavedQueryAsync(query.SavedQueryId, current.Version, Token);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        Assert.Equal(SavedQueryWriteResult.Conflict, await repository.WriteSavedQueryAsync(new SavedQueryWrite(query, current.Version), Token));
        Assert.Equal(SavedQueryWriteResult.Conflict, await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token));
    }

    [Fact]
    public async Task MissingRevisionRollsBackContextAndExistingSavedQuery()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        var original = await repository.ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(original);
        var invalid = query with { CurrentRevisionId = Guid.NewGuid(), Scope = SavedQueryScope.Database,
            Connection = new QueryConnectionContext("LibraryServer", "Library") };
        await Assert.ThrowsAsync<SqliteException>(() => repository.WriteSavedQueryAsync(new SavedQueryWrite(invalid, original.Version), Token));
        Assert.Equal(original, await repository.ReadSavedQueryAsync(query.SavedQueryId, Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts;"));
    }

    [Fact]
    public async Task SavedForeignKeyProtectsRevisionAndContentWithoutAnyOtherIncomingReference()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        var saved = await repository.ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(saved);
        // fixture 移除其他根引用，確保不是 Session head 或 Execution 代替 Saved 擋住刪除。
        store.Scalar("UPDATE Sessions SET LatestRevisionId=NULL,LatestExecutionRevisionId=NULL; DELETE FROM Executions; DELETE FROM Recovery; DELETE FROM History;");
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions;"));
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Contents;"));
        Assert.NotNull(await repository.ReadContentAsync(saved.ContentId, Token));
        await repository.DeleteSavedQueryAsync(query.SavedQueryId, saved.Version, Token);
        store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions; DELETE FROM Contents;");
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task RecoveryReplacementAndClosePreserveSavedContentAndManualRevision()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(kind: QueryCaptureKind.ManualSnapshot), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        var query = new SavedQuery(Guid.NewGuid(), "讀者快照", null, state.LatestRevision.RevisionId, SavedQueryScope.Global, null, true);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;", QueryCaptureKind.DraftIdle), Token);
        await store.Process(repository, store.Capture(3, "SELECT * FROM Lib_Tag;", QueryCaptureKind.EditorClosed), Token);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.NotNull(await repository.ReadContentAsync(state.LatestRevision.ContentId, Token));
        Assert.Equal(query, (await repository.ReadSavedQueryAsync(query.SavedQueryId, Token))?.Query);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ScopePagingIsExactBoundedAndDoesNotReadSqlBlob()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        var ids = new List<Guid>();
        for (var i = 1; i <= 7; i++)
        {
            var id = Guid.NewGuid(); ids.Add(id);
            await repository.WriteSavedQueryAsync(new SavedQueryWrite(query with { SavedQueryId = id }), Token);
        }
        foreach (var context in new[] { new QueryConnectionContext("LibraryServer", ""), new QueryConnectionContext("LibraryServer", "Library"),
            new QueryConnectionContext("libraryserver", "Library"), new QueryConnectionContext("LibraryServer", "library") })
            await repository.WriteSavedQueryAsync(new SavedQueryWrite(query with { SavedQueryId = Guid.NewGuid(), Connection = context,
                Scope = context.Database.Length == 0 ? SavedQueryScope.Server : SavedQueryScope.Database }), Token);
        // 列表不可讀取或驗證全文 BLOB；損壞全文應留到按需讀取時才失敗。
        store.Scalar("UPDATE Contents SET SqlBytes=zeroblob(Length*2);");
        var actual = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await repository.ReadSavedQueriesAsync(new SavedQueryRequest(2, cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 2);
            actual.AddRange(page.Items.Select(item => item.Query.SavedQueryId)); cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(ids.Select(id => id.ToString("N")).OrderByDescending(id => id, StringComparer.Ordinal), actual.Select(id => id.ToString("N")));
        Assert.Single((await repository.ReadSavedQueriesAsync(new SavedQueryRequest(20, SavedQueryScope.Server, "LibraryServer"), Token)).Items);
        Assert.Single((await repository.ReadSavedQueriesAsync(new SavedQueryRequest(20, SavedQueryScope.Database, "LibraryServer", "Library"), Token)).Items);
    }

    [Fact]
    public async Task CursorsRejectOtherStoresScopesHistoryAndMalformedInput()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query with { SavedQueryId = Guid.NewGuid() }), Token);
        var page = await repository.ReadSavedQueriesAsync(new SavedQueryRequest(1), Token);
        Assert.NotNull(page.NextCursor);
        using var second = new SqliteTestStore();
        var other = await second.Open(Token);
        await Assert.ThrowsAsync<ArgumentException>(() => other.ReadSavedQueriesAsync(new SavedQueryRequest(1, cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadSavedQueriesAsync(new SavedQueryRequest(1, SavedQueryScope.Server, "LibraryServer", cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadHistoryAsync(new QueryHistoryRequest(1, cursor: page.NextCursor), Token));
        var history = await repository.ReadHistoryAsync(new QueryHistoryRequest(1), Token);
        Assert.NotNull(history.NextCursor);
        foreach (var cursor in new[] { history.NextCursor, "!", "", new string('a', 513), Convert.ToBase64String(new byte[] { 1 }) })
            await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadSavedQueriesAsync(new SavedQueryRequest(1, cursor: cursor), Token));
    }

    [Fact]
    public async Task CancellationDoesNotChangeSavedQuery()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        var original = await repository.ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(original);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.WriteSavedQueryAsync(new SavedQueryWrite(query, original.Version), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.DeleteSavedQueryAsync(query.SavedQueryId, original.Version, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadSavedQueriesAsync(new SavedQueryRequest(1), source.Token));
        Assert.Equal(original, await repository.ReadSavedQueryAsync(query.SavedQueryId, Token));
    }

    [Fact]
    public async Task ScopeKeysetUsesCompositeIndexWithoutTemporarySort()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT SavedQueryId FROM SavedQueries WHERE Scope=2 AND Server IS 'LibraryServer' AND DatabaseName IS 'Library' AND SavedQueryId < 'f' ORDER BY SavedQueryId DESC LIMIT 20;";
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("IX_SavedQueries_ScopeId"));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
    }
}
