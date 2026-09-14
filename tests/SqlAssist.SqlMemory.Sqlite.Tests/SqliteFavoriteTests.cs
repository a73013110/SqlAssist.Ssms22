using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteFavoriteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<SqlFavorite> CreateQuery(SqliteTestStore store, SqliteTestRepository repository)
    {
        await store.Process(repository, store.Capture(), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        return new SqlFavorite(Guid.NewGuid(), "讀者查詢", "圖書館範例", state.LatestRevision.RevisionId, SqlFavoriteScope.Global, null);
    }

    [Fact]
    public async Task CreateUpdateDeleteAndReopenDoNotMutateHistory()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token));
        var favorite = await (await store.Open(Token)).ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        Assert.Equal(query, favorite.Favorite);
        Assert.Equal("SELECT * FROM Lib_Reader;", favorite.Preview);
        Assert.NotEqual(Guid.Empty, favorite.Version);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        var changed = query with { Name = "標籤查詢", CurrentRevisionId = state.LatestRevision.RevisionId,
            Scope = SqlFavoriteScope.Database, Connection = new SqlConnectionLabel("LibraryServer", "Library") };
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.WriteFavoriteAsync(new SqlFavoriteWrite(changed, favorite.Version), Token));
        var updated = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(updated);
        Assert.Equal(changed, updated.Favorite);
        Assert.NotEqual(favorite.Version, updated.Version);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.DeleteFavoriteAsync(query.FavoriteId, updated.Version, Token));
        Assert.Null(await repository.ReadFavoriteAsync(query.FavoriteId, Token));
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
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token);
        var original = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(original);
        var other = await store.Open(Token);
        var results = await Task.WhenAll(repository.WriteFavoriteAsync(new SqlFavoriteWrite(query with { Name = "讀者一" }, original.Version), Token),
            other.WriteFavoriteAsync(new SqlFavoriteWrite(query with { Name = "讀者二" }, original.Version), Token));
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Committed);
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Conflict);
        var contextual = query with { Scope = SqlFavoriteScope.Database, Connection = new SqlConnectionLabel("LibraryServer", "Library") };
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await repository.WriteFavoriteAsync(new SqlFavoriteWrite(contextual, original.Version), Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts;"));
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await repository.DeleteFavoriteAsync(query.FavoriteId, original.Version, Token));
        var current = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(current);
        await repository.DeleteFavoriteAsync(query.FavoriteId, current.Version, Token);
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token);
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query, current.Version), Token));
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token));
    }

    [Fact]
    public async Task MissingRevisionRollsBackContextAndExistingFavorite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token);
        var original = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(original);
        var invalid = query with { CurrentRevisionId = Guid.NewGuid(), Scope = SqlFavoriteScope.Database,
            Connection = new SqlConnectionLabel("LibraryServer", "Library") };
        await Assert.ThrowsAsync<SqliteException>(() => repository.WriteFavoriteAsync(new SqlFavoriteWrite(invalid, original.Version), Token));
        Assert.Equal(original, await repository.ReadFavoriteAsync(query.FavoriteId, Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts;"));
    }

    [Fact]
    public async Task FavoriteForeignKeyProtectsRevisionAndContentWithoutAnyOtherIncomingReference()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token);
        var favorite = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        // fixture 移除其他根引用，確保不是 Session head 或 Execution 代替 Favorite 擋住刪除。
        store.Scalar("UPDATE Sessions SET LatestRevisionId=NULL,LatestExecutionRevisionId=NULL; DELETE FROM Executions; DELETE FROM Recovery; DELETE FROM History;");
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions;"));
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Contents;"));
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
        await repository.DeleteFavoriteAsync(query.FavoriteId, favorite.Version, Token);
        store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions; DELETE FROM Contents;");
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task RecoveryReplacementAndClosePreserveFavoriteContent()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        var query = new SqlFavorite(Guid.NewGuid(), "讀者收藏", null, state.LatestRevision.RevisionId, SqlFavoriteScope.Global, null);
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;", SqlCaptureKind.DraftIdle), Token);
        await store.Process(repository, store.Capture(3, "SELECT * FROM Lib_Tag;", SqlCaptureKind.EditorClosed), Token);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.NotNull(await repository.ReadContentAsync(state.LatestRevision.ContentId, Token));
        Assert.Equal(query, (await repository.ReadFavoriteAsync(query.FavoriteId, Token))?.Favorite);
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
            await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query with { FavoriteId = id }), Token);
        }
        foreach (var context in new[] { new SqlConnectionLabel("LibraryServer", ""), new SqlConnectionLabel("LibraryServer", "Library"),
            new SqlConnectionLabel("libraryserver", "Library"), new SqlConnectionLabel("LibraryServer", "library") })
            await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query with { FavoriteId = Guid.NewGuid(), Connection = context,
                Scope = context.Database.Length == 0 ? SqlFavoriteScope.Server : SqlFavoriteScope.Database }), Token);
        // 列表不可讀取或驗證全文 BLOB；損壞全文應留到按需讀取時才失敗。
        store.Scalar("UPDATE Contents SET SqlBytes=zeroblob(Length*2);");
        var actual = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(2, cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 2);
            actual.AddRange(page.Items.Select(item => item.Favorite.FavoriteId)); cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(ids.Select(id => id.ToString("N")).OrderByDescending(id => id, StringComparer.Ordinal), actual.Select(id => id.ToString("N")));
        Assert.Single((await repository.ReadFavoritesAsync(new SqlFavoriteRequest(20, SqlFavoriteScope.Server, "LibraryServer"), Token)).Items);
        Assert.Single((await repository.ReadFavoritesAsync(new SqlFavoriteRequest(20, SqlFavoriteScope.Database, "LibraryServer", "Library"), Token)).Items);
    }

    [Fact]
    public async Task SearchMatchesNameDescriptionAndSqlWithoutWideningScope()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var reader = await CreateQuery(store, repository);
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(reader), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        // 說明為 NULL 的收藏仍須靠 SQL 命中；instr 對 NULL 回傳 NULL，不能因此整列消失。
        var tag = reader with { FavoriteId = Guid.NewGuid(), Name = "標籤清單", Description = null,
            CurrentRevisionId = state.LatestRevision.RevisionId };
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(tag), Token);
        var scoped = reader with { FavoriteId = Guid.NewGuid(), Scope = SqlFavoriteScope.Server,
            Connection = new SqlConnectionLabel("LibraryServer", "") };
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(scoped), Token);

        async Task<Guid[]> Find(string? search, SqlFavoriteScope scope = SqlFavoriteScope.Global, string? server = null) =>
            (await repository.ReadFavoritesAsync(new SqlFavoriteRequest(20, scope, server, search: search), Token))
            .Items.Select(item => item.Favorite.FavoriteId).ToArray();

        Assert.Equal(new[] { reader.FavoriteId }, await Find("圖書館範例"));
        Assert.Equal(new[] { reader.FavoriteId }, await Find("Lib_Reader"));
        Assert.Equal(new[] { tag.FavoriteId }, await Find("標籤"));
        Assert.Equal(new[] { tag.FavoriteId }, await Find("Lib_Tag"));
        Assert.Equal(2, (await Find("SELECT * FROM")).Length);
        Assert.Equal(2, (await Find(null)).Length);
        Assert.Equal(2, (await Find("")).Length);
        foreach (var missing in new[] { "lib_reader", "讀者查詢 ", "' OR 1=1--", "Library.sql" })
            Assert.Empty(await Find(missing));
        // 搜尋只在 scope 之內；同一段 SQL 在別的 scope 也不會被帶進來。
        Assert.Equal(new[] { scoped.FavoriteId }, await Find("Lib_Reader", SqlFavoriteScope.Server, "LibraryServer"));
        Assert.Empty(await Find("Lib_Tag", SqlFavoriteScope.Server, "LibraryServer"));
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
            await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query with { FavoriteId = id,
                Name = wanted ? "借閱報表 " + i : "其他 " + i }), Token);
        }
        var actual = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(2, search: "借閱報表", cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 2);
            actual.AddRange(page.Items.Select(item => item.Favorite.FavoriteId));
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(matching.Select(id => id.ToString("N")).OrderByDescending(id => id, StringComparer.Ordinal), actual.Select(id => id.ToString("N")));
        var first = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(1, search: "借閱報表"), Token);
        Assert.NotNull(first.NextCursor);
        foreach (var other in new string?[] { null, "", "借閱", "借閱報表 " })
            await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadFavoritesAsync(
                new SqlFavoriteRequest(1, search: other, cursor: first.NextCursor), Token));
    }

    [Fact]
    public async Task SearchCancellationNeverSurfacesProviderExceptions()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        for (var i = 0; i < 20; i++)
            await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query with { FavoriteId = Guid.NewGuid() }), Token);
        using var source = new CancellationTokenSource();
        // 取消可能落在派送前或 BLOB 掃描中；無論哪一種都不得讓 SqliteException 外流。
        var reading = repository.ReadFavoritesAsync(new SqlFavoriteRequest(200, search: "Lib_Reader"), source.Token);
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
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token);
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query with { FavoriteId = Guid.NewGuid() }), Token);
        var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(1), Token);
        Assert.NotNull(page.NextCursor);
        using var second = new SqliteTestStore();
        var other = await second.Open(Token);
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => other.ReadFavoritesAsync(new SqlFavoriteRequest(1, cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadFavoritesAsync(new SqlFavoriteRequest(1, SqlFavoriteScope.Server, "LibraryServer", cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadHistoryAsync(new SqlHistoryRequest(1, cursor: page.NextCursor), Token));
        var history = await repository.ReadHistoryAsync(new SqlHistoryRequest(1), Token);
        Assert.NotNull(history.NextCursor);
        foreach (var cursor in new[] { history.NextCursor, "!", "", new string('a', 513), Convert.ToBase64String(new byte[] { 1 }) })
            await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadFavoritesAsync(new SqlFavoriteRequest(1, cursor: cursor), Token));
    }

    [Fact]
    public async Task CancellationDoesNotChangeFavorite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token);
        var original = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(original);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.WriteFavoriteAsync(new SqlFavoriteWrite(query, original.Version), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.DeleteFavoriteAsync(query.FavoriteId, original.Version, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadFavoritesAsync(new SqlFavoriteRequest(1), source.Token));
        Assert.Equal(original, await repository.ReadFavoriteAsync(query.FavoriteId, Token));
    }

    [Fact]
    public async Task ScopeKeysetUsesCompositeIndexWithoutTemporarySort()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // 搜尋在讀取端逐列比對；候選查詢仍不得退回全表掃描或暫存排序，否則預算限制不了讀入的 BLOB。
        foreach (var (after, includeSql) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            command.CommandText = "EXPLAIN QUERY PLAN " + SqliteFavoriteStore.FavoritePageSql(after, includeSql);
            command.Parameters.Clear();
            foreach (var (name, value) in new (string, object)[] { ("$scope", 2), ("$server", "LibraryServer"),
                ("$database", "Library"), ("$after", "f"), ("$limit", 20) })
                command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, line => line.Contains("IX_Favorites_ScopeId"));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
        }
    }

    [Fact]
    public async Task SearchScanBudgetEndsPagesEarlyAndResumesWithoutGapsOrDuplicates()
    {
        using var store = new SqliteTestStore();
        var repository = await SqliteTestRepository.OpenAsync(store.Path, Token, searchBudget: new SqliteSearchBudget(3, long.MaxValue));
        var query = await CreateQuery(store, repository);
        var matching = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            // 名稱與說明各命中一部分，其餘只有 SQL 以外的文字，逼讀取端把預算花在不命中的列上。
            var name = i % 5 == 0 ? "借閱報表 " + i : "其他 " + i;
            var description = i % 7 == 3 ? "含借閱報表說明" : null;
            if (name.Contains("借閱報表") || description != null) matching.Add(id.ToString("N"));
            await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query with { FavoriteId = id,
                Name = name, Description = description }), Token);
        }
        var actual = new List<string>();
        string? cursor = null;
        var pages = 0;
        var partial = 0;
        do
        {
            var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(2, search: "借閱報表", cursor: cursor), Token);
            pages++;
            Assert.InRange(page.Items.Count, 0, 2);
            Assert.Null(page.SearchedThrough);
            if (page.IsSearchPartial) { partial++; Assert.NotNull(page.NextCursor); }
            actual.AddRange(page.Items.Select(item => item.Favorite.FavoriteId.ToString("N")));
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(matching.OrderByDescending(id => id, StringComparer.Ordinal), actual);
        // 每頁至多檢查 3 筆：12 筆收藏至少要 4 頁，且一定有頁因預算而提早結束。
        Assert.True(pages >= 4, pages.ToString());
        Assert.True(partial > 0);
    }

    private const string EditedSql = "SELECT * FROM Lib_Tag WHERE TagId=1;";

    private static async Task<SqlFavoriteItem> CreateFavorite(SqliteTestStore store, SqliteTestRepository repository,
        SqlFavorite? query = null)
    {
        query ??= await CreateQuery(store, repository);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.WriteFavoriteAsync(new SqlFavoriteWrite(query), Token));
        var favorite = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        return favorite;
    }

    [Fact]
    public async Task EditingSqlSwapsCurrentRevisionInOneTransactionWithoutHistoryOrSession()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        var edit = new SqlFavoriteSqlEdit(favorite.Favorite.FavoriteId, favorite.Version, Guid.NewGuid(), EditedSql, SqliteTestStore.Start.AddHours(1));
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.EditFavoriteSqlAsync(edit, Token));
        var edited = await (await store.Open(Token)).ReadFavoriteAsync(favorite.Favorite.FavoriteId, Token);
        Assert.NotNull(edited);
        Assert.Equal(favorite.Favorite with { CurrentRevisionId = edit.RevisionId }, edited.Favorite);
        Assert.Equal(SqlContent.Create(EditedSql).ContentId, edited.ContentId);
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
        Assert.Equal(favorite.Favorite.CurrentRevisionId, state.LatestRevision.RevisionId);
        Assert.Equal(1L, state.Version);
        Assert.Equal(Id(edit.RevisionId), store.Scalar("SELECT RevisionId FROM Revisions WHERE FavoriteId IS NOT NULL;"));
        Assert.Equal((long)SqlRevisionReason.FavoriteEdit,
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
            repository.EditFavoriteSqlAsync(new SqlFavoriteSqlEdit(favorite.Favorite.FavoriteId, favorite.Version, Guid.NewGuid(),
                EditedSql, SqliteTestStore.Start.AddHours(1)), Token),
            other.EditFavoriteSqlAsync(new SqlFavoriteSqlEdit(favorite.Favorite.FavoriteId, favorite.Version, Guid.NewGuid(),
                "SELECT * FROM Loan;", SqliteTestStore.Start.AddHours(2)), Token));
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Committed);
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Conflict);
        // 敗方連版本與內容都不留下，重送不冪等，呼叫端必須重讀。
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await repository.EditFavoriteSqlAsync(
            new SqlFavoriteSqlEdit(Guid.NewGuid(), favorite.Version, Guid.NewGuid(), EditedSql, SqliteTestStore.Start), Token));
        var current = await repository.ReadFavoriteAsync(favorite.Favorite.FavoriteId, Token);
        Assert.NotNull(current);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.EditFavoriteSqlAsync(
            new SqlFavoriteSqlEdit(favorite.Favorite.FavoriteId, current.Version, Guid.NewGuid(), "SELECT * FROM Branch;",
                SqliteTestStore.Start.AddHours(3)), source.Token));
        Assert.Equal(current, await repository.ReadFavoriteAsync(favorite.Favorite.FavoriteId, Token));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task EditedSqlReusesScopeContextAndIsSearchableInItsOwnScope()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        var scoped = query with { Scope = SqlFavoriteScope.Database, Connection = new SqlConnectionLabel("LibraryServer", "Library") };
        var favorite = await CreateFavorite(store, repository, scoped);
        var edit = new SqlFavoriteSqlEdit(scoped.FavoriteId, favorite.Version, Guid.NewGuid(), EditedSql, SqliteTestStore.Start.AddHours(1));
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.EditFavoriteSqlAsync(edit, Token));
        // 新版本沿用收藏自己的連線內容，不另建 Contexts 列，也不從舊版本帶進別的連線。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contexts;"));
        Assert.Equal(store.Scalar("SELECT ContextId FROM Favorites;"),
            store.Scalar("SELECT ContextId FROM Revisions WHERE FavoriteId IS NOT NULL;"));
        async Task<int> Search(string search) => (await repository.ReadFavoritesAsync(
            new SqlFavoriteRequest(5, SqlFavoriteScope.Database, "LibraryServer", "Library", search), Token)).Items.Count;
        Assert.Equal(1, await Search("TagId=1"));
        Assert.Equal(0, await Search("Lib_Reader"));
        Assert.Empty((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.All, "TagId=1"), Token)).Items);
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
WHERE FavoriteId='a' AND FavoriteId IS NOT NULL ORDER BY CreatedAt DESC LIMIT 1 OFFSET 4;";
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("IX_Revisions_Favorite"));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
    }
}
