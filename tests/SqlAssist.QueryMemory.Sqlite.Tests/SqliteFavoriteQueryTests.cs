using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteFavoriteQueryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<FavoriteQuery> CreateQuery(SqliteTestStore store, SqliteTestRepository repository)
    {
        await store.Process(repository, store.Capture(), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        return new FavoriteQuery(Guid.NewGuid(), "讀者查詢", "圖書館範例", state.LatestRevision.RevisionId, FavoriteQueryScope.Global, null);
    }

    [Fact]
    public async Task CreateUpdateDeleteAndReopenDoNotMutateHistory()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token));
        var favorite = await (await store.Open(Token)).ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(favorite);
        Assert.Equal(query, favorite.Query);
        Assert.Equal("SELECT * FROM Lib_Reader;", favorite.Preview);
        Assert.NotEqual(Guid.Empty, favorite.Version);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        var changed = query with { Name = "標籤查詢", CurrentRevisionId = state.LatestRevision.RevisionId,
            Scope = FavoriteQueryScope.Database, Connection = new QueryConnectionContext("LibraryServer", "Library") };
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(changed, favorite.Version), Token));
        var updated = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(updated);
        Assert.Equal(changed, updated.Query);
        Assert.NotEqual(favorite.Version, updated.Version);
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.DeleteFavoriteQueryAsync(query.FavoriteQueryId, updated.Version, Token));
        Assert.Null(await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token));
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
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
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        var original = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(original);
        var other = await store.Open(Token);
        var results = await Task.WhenAll(repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { Name = "讀者一" }, original.Version), Token),
            other.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { Name = "讀者二" }, original.Version), Token));
        Assert.Single(results, result => result == FavoriteQueryWriteResult.Committed);
        Assert.Single(results, result => result == FavoriteQueryWriteResult.Conflict);
        var contextual = query with { Scope = FavoriteQueryScope.Database, Connection = new QueryConnectionContext("LibraryServer", "Library") };
        Assert.Equal(FavoriteQueryWriteResult.Conflict, await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(contextual, original.Version), Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts;"));
        Assert.Equal(FavoriteQueryWriteResult.Conflict, await repository.DeleteFavoriteQueryAsync(query.FavoriteQueryId, original.Version, Token));
        var current = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(current);
        await repository.DeleteFavoriteQueryAsync(query.FavoriteQueryId, current.Version, Token);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        Assert.Equal(FavoriteQueryWriteResult.Conflict, await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query, current.Version), Token));
        Assert.Equal(FavoriteQueryWriteResult.Conflict, await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token));
    }

    [Fact]
    public async Task MissingRevisionRollsBackContextAndExistingFavoriteQuery()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        var original = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(original);
        var invalid = query with { CurrentRevisionId = Guid.NewGuid(), Scope = FavoriteQueryScope.Database,
            Connection = new QueryConnectionContext("LibraryServer", "Library") };
        await Assert.ThrowsAsync<SqliteException>(() => repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(invalid, original.Version), Token));
        Assert.Equal(original, await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts;"));
    }

    [Fact]
    public async Task FavoriteForeignKeyProtectsRevisionAndContentWithoutAnyOtherIncomingReference()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        var favorite = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(favorite);
        // fixture 移除其他根引用，確保不是 Session head 或 Execution 代替 Favorite 擋住刪除。
        store.Scalar("UPDATE Sessions SET LatestRevisionId=NULL,LatestExecutionRevisionId=NULL; DELETE FROM Executions; DELETE FROM Recovery; DELETE FROM History;");
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions;"));
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Contents;"));
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
        await repository.DeleteFavoriteQueryAsync(query.FavoriteQueryId, favorite.Version, Token);
        store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions; DELETE FROM Contents;");
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task RecoveryReplacementAndClosePreserveFavoriteContentAndManualRevision()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(kind: QueryCaptureKind.ManualSnapshot), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        var query = new FavoriteQuery(Guid.NewGuid(), "讀者快照", null, state.LatestRevision.RevisionId, FavoriteQueryScope.Global, null);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;", QueryCaptureKind.DraftIdle), Token);
        await store.Process(repository, store.Capture(3, "SELECT * FROM Lib_Tag;", QueryCaptureKind.EditorClosed), Token);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.NotNull(await repository.ReadContentAsync(state.LatestRevision.ContentId, Token));
        Assert.Equal(query, (await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token))?.Query);
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
            await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { FavoriteQueryId = id }), Token);
        }
        foreach (var context in new[] { new QueryConnectionContext("LibraryServer", ""), new QueryConnectionContext("LibraryServer", "Library"),
            new QueryConnectionContext("libraryserver", "Library"), new QueryConnectionContext("LibraryServer", "library") })
            await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { FavoriteQueryId = Guid.NewGuid(), Connection = context,
                Scope = context.Database.Length == 0 ? FavoriteQueryScope.Server : FavoriteQueryScope.Database }), Token);
        // 列表不可讀取或驗證全文 BLOB；損壞全文應留到按需讀取時才失敗。
        store.Scalar("UPDATE Contents SET SqlBytes=zeroblob(Length*2);");
        var actual = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(2, cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 2);
            actual.AddRange(page.Items.Select(item => item.Query.FavoriteQueryId)); cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(ids.Select(id => id.ToString("N")).OrderByDescending(id => id, StringComparer.Ordinal), actual.Select(id => id.ToString("N")));
        Assert.Single((await repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(20, FavoriteQueryScope.Server, "LibraryServer"), Token)).Items);
        Assert.Single((await repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(20, FavoriteQueryScope.Database, "LibraryServer", "Library"), Token)).Items);
    }

    [Fact]
    public async Task SearchMatchesNameDescriptionAndSqlWithoutWideningScope()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var reader = await CreateQuery(store, repository);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(reader), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        // 說明為 NULL 的收藏仍須靠 SQL 命中；instr 對 NULL 回傳 NULL，不能因此整列消失。
        var tag = reader with { FavoriteQueryId = Guid.NewGuid(), Name = "標籤清單", Description = null,
            CurrentRevisionId = state.LatestRevision.RevisionId };
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(tag), Token);
        var scoped = reader with { FavoriteQueryId = Guid.NewGuid(), Scope = FavoriteQueryScope.Server,
            Connection = new QueryConnectionContext("LibraryServer", "") };
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(scoped), Token);

        async Task<Guid[]> Find(string? search, FavoriteQueryScope scope = FavoriteQueryScope.Global, string? server = null) =>
            (await repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(20, scope, server, search: search), Token))
            .Items.Select(item => item.Query.FavoriteQueryId).ToArray();

        Assert.Equal(new[] { reader.FavoriteQueryId }, await Find("圖書館範例"));
        Assert.Equal(new[] { reader.FavoriteQueryId }, await Find("Lib_Reader"));
        Assert.Equal(new[] { tag.FavoriteQueryId }, await Find("標籤"));
        Assert.Equal(new[] { tag.FavoriteQueryId }, await Find("Lib_Tag"));
        Assert.Equal(2, (await Find("SELECT * FROM")).Length);
        Assert.Equal(2, (await Find(null)).Length);
        Assert.Equal(2, (await Find("")).Length);
        foreach (var missing in new[] { "lib_reader", "讀者查詢 ", "' OR 1=1--", "Library.sql" })
            Assert.Empty(await Find(missing));
        // 搜尋只在 scope 之內；同一段 SQL 在別的 scope 也不會被帶進來。
        Assert.Equal(new[] { scoped.FavoriteQueryId }, await Find("Lib_Reader", FavoriteQueryScope.Server, "LibraryServer"));
        Assert.Empty(await Find("Lib_Tag", FavoriteQueryScope.Server, "LibraryServer"));
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
            await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { FavoriteQueryId = id,
                Name = wanted ? "借閱報表 " + i : "其他 " + i }), Token);
        }
        var actual = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(2, search: "借閱報表", cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 2);
            actual.AddRange(page.Items.Select(item => item.Query.FavoriteQueryId));
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(matching.Select(id => id.ToString("N")).OrderByDescending(id => id, StringComparer.Ordinal), actual.Select(id => id.ToString("N")));
        var first = await repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(1, search: "借閱報表"), Token);
        Assert.NotNull(first.NextCursor);
        foreach (var other in new string?[] { null, "", "借閱", "借閱報表 " })
            await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.ReadFavoriteQueriesAsync(
                new FavoriteQueryRequest(1, search: other, cursor: first.NextCursor), Token));
    }

    [Fact]
    public async Task SearchCancellationNeverSurfacesProviderExceptions()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        for (var i = 0; i < 20; i++)
            await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { FavoriteQueryId = Guid.NewGuid() }), Token);
        using var source = new CancellationTokenSource();
        // 取消可能落在派送前或 BLOB 掃描中；無論哪一種都不得讓 SqliteException 外流。
        var reading = repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(200, search: "Lib_Reader"), source.Token);
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
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { FavoriteQueryId = Guid.NewGuid() }), Token);
        var page = await repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(1), Token);
        Assert.NotNull(page.NextCursor);
        using var second = new SqliteTestStore();
        var other = await second.Open(Token);
        await Assert.ThrowsAsync<QueryMemoryStorageException>(() => other.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(1, cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(1, FavoriteQueryScope.Server, "LibraryServer", cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.ReadHistoryAsync(new QueryHistoryRequest(1, cursor: page.NextCursor), Token));
        var history = await repository.ReadHistoryAsync(new QueryHistoryRequest(1), Token);
        Assert.NotNull(history.NextCursor);
        foreach (var cursor in new[] { history.NextCursor, "!", "", new string('a', 513), Convert.ToBase64String(new byte[] { 1 }) })
            await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(1, cursor: cursor), Token));
    }

    [Fact]
    public async Task CancellationDoesNotChangeFavoriteQuery()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        var original = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(original);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query, original.Version), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.DeleteFavoriteQueryAsync(query.FavoriteQueryId, original.Version, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(1), source.Token));
        Assert.Equal(original, await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token));
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
            command.CommandText = @"EXPLAIN QUERY PLAN SELECT s.FavoriteQueryId FROM FavoriteQueries s
JOIN Revisions r ON r.RevisionId=s.CurrentRevisionId JOIN Contents c ON c.ContentId=r.ContentId
WHERE s.Scope=2 AND s.Server IS 'LibraryServer' AND s.DatabaseName IS 'Library' AND s.FavoriteQueryId < 'f'" +
                filter + " ORDER BY s.FavoriteQueryId DESC LIMIT 20;";
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, line => line.Contains("IX_FavoriteQueries_ScopeId"));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
        }
    }

    private const string EditedSql = "SELECT * FROM Lib_Tag WHERE TagId=1;";

    private static async Task<FavoriteQueryEntry> CreateFavorite(SqliteTestStore store, SqliteTestRepository repository,
        FavoriteQuery? query = null)
    {
        query ??= await CreateQuery(store, repository);
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token));
        var favorite = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(favorite);
        return favorite;
    }

    [Fact]
    public async Task EditingSqlSwapsCurrentRevisionInOneTransactionWithoutHistoryOrSession()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        var edit = new FavoriteQueryEdit(favorite.Query.FavoriteQueryId, favorite.Version, Guid.NewGuid(), EditedSql, SqliteTestStore.Start.AddHours(1));
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.EditFavoriteQuerySqlAsync(edit, Token));
        var edited = await (await store.Open(Token)).ReadFavoriteQueryAsync(favorite.Query.FavoriteQueryId, Token);
        Assert.NotNull(edited);
        Assert.Equal(favorite.Query with { CurrentRevisionId = edit.RevisionId }, edited.Query);
        Assert.Equal(QueryContent.Create(EditedSql).ContentId, edited.ContentId);
        Assert.Equal(EditedSql, edited.Preview);
        Assert.NotEqual(favorite.Version, edited.Version);
        Assert.Equal(EditedSql, (await repository.ReadContentAsync(edited.ContentId, Token))?.SqlText);
        // 舊版本與其歷史都留著；新版本不進 History、不造 Session，也不改 head 或序號。
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Sessions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Captures;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM History;"));
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        Assert.Equal(favorite.Query.CurrentRevisionId, state.LatestRevision.RevisionId);
        Assert.Equal(1L, state.Version);
        Assert.Equal(Id(edit.RevisionId), store.Scalar("SELECT RevisionId FROM Revisions WHERE FavoriteQueryId IS NOT NULL;"));
        Assert.Equal((long)QueryRevisionReason.FavoriteQueryEdit,
            store.Scalar("SELECT Reason FROM Revisions WHERE RevisionId='" + Id(edit.RevisionId) + "';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + Id(edit.RevisionId) + "' AND ParentRevisionId IS NULL;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    private static string Id(Guid id) => id.ToString("N");

    [Fact]
    public async Task EditingSqlRejectsStaleVersionsMissingQueriesAndCancellationWithoutPartialWrites()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        var other = await store.Open(Token);
        var results = await Task.WhenAll(
            repository.EditFavoriteQuerySqlAsync(new FavoriteQueryEdit(favorite.Query.FavoriteQueryId, favorite.Version, Guid.NewGuid(),
                EditedSql, SqliteTestStore.Start.AddHours(1)), Token),
            other.EditFavoriteQuerySqlAsync(new FavoriteQueryEdit(favorite.Query.FavoriteQueryId, favorite.Version, Guid.NewGuid(),
                "SELECT * FROM Loan;", SqliteTestStore.Start.AddHours(2)), Token));
        Assert.Single(results, result => result == FavoriteQueryWriteResult.Committed);
        Assert.Single(results, result => result == FavoriteQueryWriteResult.Conflict);
        // 敗方連版本與內容都不留下，重送不冪等，呼叫端必須重讀。
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(FavoriteQueryWriteResult.Conflict, await repository.EditFavoriteQuerySqlAsync(
            new FavoriteQueryEdit(Guid.NewGuid(), favorite.Version, Guid.NewGuid(), EditedSql, SqliteTestStore.Start), Token));
        var current = await repository.ReadFavoriteQueryAsync(favorite.Query.FavoriteQueryId, Token);
        Assert.NotNull(current);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.EditFavoriteQuerySqlAsync(
            new FavoriteQueryEdit(favorite.Query.FavoriteQueryId, current.Version, Guid.NewGuid(), "SELECT * FROM Branch;",
                SqliteTestStore.Start.AddHours(3)), source.Token));
        Assert.Equal(current, await repository.ReadFavoriteQueryAsync(favorite.Query.FavoriteQueryId, Token));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task EditedSqlReusesScopeContextAndIsSearchableInItsOwnScope()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        var scoped = query with { Scope = FavoriteQueryScope.Database, Connection = new QueryConnectionContext("LibraryServer", "Library") };
        var favorite = await CreateFavorite(store, repository, scoped);
        var edit = new FavoriteQueryEdit(scoped.FavoriteQueryId, favorite.Version, Guid.NewGuid(), EditedSql, SqliteTestStore.Start.AddHours(1));
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.EditFavoriteQuerySqlAsync(edit, Token));
        // 新版本沿用收藏自己的連線內容，不另建 Contexts 列，也不從舊版本帶進別的連線。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contexts;"));
        Assert.Equal(store.Scalar("SELECT ContextId FROM FavoriteQueries;"),
            store.Scalar("SELECT ContextId FROM Revisions WHERE FavoriteQueryId IS NOT NULL;"));
        async Task<int> Search(string search) => (await repository.ReadFavoriteQueriesAsync(
            new FavoriteQueryRequest(5, FavoriteQueryScope.Database, "LibraryServer", "Library", search), Token)).Items.Count;
        Assert.Equal(1, await Search("TagId=1"));
        Assert.Equal(0, await Search("Lib_Reader"));
        Assert.Empty((await repository.ReadHistoryAsync(new QueryHistoryRequest(10, QueryHistoryKind.All, "TagId=1"), Token)).Items);
    }

    [Fact]
    public async Task FavoriteRevisionLookupUsesThePartialIndex()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // 部分索引要看得到 IS NOT NULL 才會命中；界線查詢不得退回掃描整張 Revisions。
        command.CommandText = @"EXPLAIN QUERY PLAN SELECT CreatedAt FROM Revisions
WHERE FavoriteQueryId='a' AND FavoriteQueryId IS NOT NULL ORDER BY CreatedAt DESC LIMIT 1 OFFSET 4;";
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("IX_Revisions_Favorite"));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
    }
}
