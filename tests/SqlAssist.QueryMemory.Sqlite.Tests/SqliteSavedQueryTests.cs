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
    public async Task SearchMatchesNameDescriptionAndSqlWithoutWideningScope()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var reader = await CreateQuery(store, repository);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(reader), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        // 說明為 NULL 的收藏仍須靠 SQL 命中；instr 對 NULL 回傳 NULL，不能因此整列消失。
        var tag = reader with { SavedQueryId = Guid.NewGuid(), Name = "標籤清單", Description = null,
            CurrentRevisionId = state.LatestRevision.RevisionId };
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(tag), Token);
        var scoped = reader with { SavedQueryId = Guid.NewGuid(), Scope = SavedQueryScope.Server,
            Connection = new QueryConnectionContext("LibraryServer", "") };
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(scoped), Token);

        async Task<Guid[]> Find(string? search, SavedQueryScope scope = SavedQueryScope.Global, string? server = null) =>
            (await repository.ReadSavedQueriesAsync(new SavedQueryRequest(20, scope, server, search: search), Token))
            .Items.Select(item => item.Query.SavedQueryId).ToArray();

        Assert.Equal(new[] { reader.SavedQueryId }, await Find("圖書館範例"));
        Assert.Equal(new[] { reader.SavedQueryId }, await Find("Lib_Reader"));
        Assert.Equal(new[] { tag.SavedQueryId }, await Find("標籤"));
        Assert.Equal(new[] { tag.SavedQueryId }, await Find("Lib_Tag"));
        Assert.Equal(2, (await Find("SELECT * FROM")).Length);
        Assert.Equal(2, (await Find(null)).Length);
        Assert.Equal(2, (await Find("")).Length);
        foreach (var missing in new[] { "lib_reader", "讀者查詢 ", "' OR 1=1--", "Library.sql" })
            Assert.Empty(await Find(missing));
        // 搜尋只在 scope 之內；同一段 SQL 在別的 scope 也不會被帶進來。
        Assert.Equal(new[] { scoped.SavedQueryId }, await Find("Lib_Reader", SavedQueryScope.Server, "LibraryServer"));
        Assert.Empty(await Find("Lib_Tag", SavedQueryScope.Server, "LibraryServer"));
    }

    [Fact]
    public async Task SearchPagingKeepsKeysetAndBindsCursorToTheTerm()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        var matching = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            var id = Guid.NewGuid();
            var wanted = i % 2 == 1;
            if (wanted) matching.Add(id);
            await repository.WriteSavedQueryAsync(new SavedQueryWrite(query with { SavedQueryId = id,
                Name = wanted ? "借閱報表 " + i : "其他 " + i }), Token);
        }
        var actual = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await repository.ReadSavedQueriesAsync(new SavedQueryRequest(2, search: "借閱報表", cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 2);
            actual.AddRange(page.Items.Select(item => item.Query.SavedQueryId));
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(matching.Select(id => id.ToString("N")).OrderByDescending(id => id, StringComparer.Ordinal), actual.Select(id => id.ToString("N")));
        var first = await repository.ReadSavedQueriesAsync(new SavedQueryRequest(1, search: "借閱報表"), Token);
        Assert.NotNull(first.NextCursor);
        foreach (var other in new string?[] { null, "", "借閱", "借閱報表 " })
            await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadSavedQueriesAsync(
                new SavedQueryRequest(1, search: other, cursor: first.NextCursor), Token));
    }

    [Fact]
    public async Task SearchCancellationNeverSurfacesProviderExceptions()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        for (var i = 0; i < 20; i++)
            await repository.WriteSavedQueryAsync(new SavedQueryWrite(query with { SavedQueryId = Guid.NewGuid() }), Token);
        using var source = new CancellationTokenSource();
        // 取消可能落在派送前或 BLOB 掃描中；無論哪一種都不得讓 SqliteException 外流。
        var reading = repository.ReadSavedQueriesAsync(new SavedQueryRequest(200, search: "Lib_Reader"), source.Token);
        source.Cancel();
        var error = await Record.ExceptionAsync(() => reading);
        Assert.True(error is null or OperationCanceledException, error?.ToString());
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
        // 搜尋只是 keyset 之上的篩選；加了它仍不得退回全表掃描或暫存排序。
        connection.CreateFunction<byte[], bool>("qm_matches", _ => true);
        foreach (var filter in new[] { "", @" AND (instr(s.Name,'報表')>0 OR instr(s.Description,'報表')>0 OR qm_matches(c.SqlBytes))" })
        {
            command.CommandText = @"EXPLAIN QUERY PLAN SELECT s.SavedQueryId FROM SavedQueries s
JOIN Revisions r ON r.RevisionId=s.CurrentRevisionId JOIN Contents c ON c.ContentId=r.ContentId
WHERE s.Scope=2 AND s.Server IS 'LibraryServer' AND s.DatabaseName IS 'Library' AND s.SavedQueryId < 'f'" +
                filter + " ORDER BY s.SavedQueryId DESC LIMIT 20;";
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, line => line.Contains("IX_SavedQueries_ScopeId"));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
        }
    }
}
