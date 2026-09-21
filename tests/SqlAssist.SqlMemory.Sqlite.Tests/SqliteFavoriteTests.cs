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
    private const string EditedSql = "SELECT * FROM Lib_Tag WHERE TagId=1;";
    private const string DraftSql = "SELECT CopyNo FROM Cat_BookCopy;";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int seconds) => SqliteTestStore.Start.AddSeconds(seconds);

    private static string Id(Guid id) => id.ToString("N");

    private static async Task<SqlFavorite> CreateQuery(SqliteTestStore store, SqliteTestRepository repository)
    {
        await store.Process(repository, store.Capture(), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        return new SqlFavorite(Guid.NewGuid(), "讀者查詢", "圖書館範例", state.LatestRevision.RevisionId, null, null);
    }

    private static Task<SqlFavoriteWriteResult> Save(SqliteTestRepository repository, SqlFavorite favorite, Guid? version = null,
        int seconds = 0, string? sql = null, CancellationToken? token = null) =>
        repository.SaveFavoriteAsync(new SqlFavoriteSave(favorite, version, At(seconds), sql), token ?? Token);

    private static async Task<SqlFavoriteItem> CreateFavorite(SqliteTestStore store, SqliteTestRepository repository,
        SqlFavorite? query = null)
    {
        query ??= await CreateQuery(store, repository);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await Save(repository, query));
        var favorite = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        return favorite;
    }

    [Fact]
    public async Task CreateUpdateDeleteAndReopenDoNotMutateHistory()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await Save(repository, query, seconds: 10));
        var favorite = await (await store.Open(Token)).ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        Assert.Equal(query, favorite.Favorite);
        Assert.Equal(At(10), favorite.UpdatedAt);
        Assert.Equal("SELECT * FROM Lib_Reader;", favorite.Preview);
        Assert.NotEqual(Guid.Empty, favorite.Version);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        // 只標資料庫是合法的標註：同名資料庫散在多台伺服器時不必先挑一台。
        var changed = query with { Name = "標籤查詢", CurrentRevisionId = state.LatestRevision.RevisionId, Database = "Library" };
        Assert.Equal(SqlFavoriteWriteResult.Committed, await Save(repository, changed, favorite.Version, 20));
        var updated = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(updated);
        Assert.Equal(changed, updated.Favorite);
        Assert.Equal(At(20), updated.UpdatedAt);
        Assert.NotEqual(favorite.Version, updated.Version);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.DeleteFavoriteAsync(query.FavoriteId, updated.Version, Token));
        Assert.Null(await repository.ReadFavoriteAsync(query.FavoriteId, Token));
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ConcurrentEditorsAndRecreatedIdsRejectStaleVersions()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await Save(repository, query);
        var original = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(original);
        var other = await store.Open(Token);
        var results = await Task.WhenAll(Save(repository, query with { Name = "讀者一" }, original.Version, 1),
            other.SaveFavoriteAsync(new SqlFavoriteSave(query with { Name = "讀者二" }, original.Version, At(1)), Token));
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Committed);
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Conflict);
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await Save(repository, query with { Server = "LibraryServer" }, original.Version, 2));
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await repository.DeleteFavoriteAsync(query.FavoriteId, original.Version, Token));
        var current = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(current);
        Assert.Null(current.Favorite.Server);
        await repository.DeleteFavoriteAsync(query.FavoriteId, current.Version, Token);
        await Save(repository, query);
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await Save(repository, query, current.Version));
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await Save(repository, query));
    }

    [Fact]
    public async Task MissingRevisionRollsBackTheWholeSave()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await Save(repository, query);
        var original = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(original);
        var invalid = query with { CurrentRevisionId = Guid.NewGuid(), Server = "LibraryServer", Database = "Library" };
        await Assert.ThrowsAsync<SqliteException>(() => Save(repository, invalid, original.Version, 5));
        Assert.Equal(original, await repository.ReadFavoriteAsync(query.FavoriteId, Token));
    }

    [Fact]
    public async Task SchemaRejectsEmptyTagsInsteadOfTreatingThemAsAnotherName()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        // 空字串與 NULL 是兩種「不限」，同時存在會讓精確篩選各漏一半；正規化在契約，schema 再擋一次。
        Assert.Throws<SqliteException>(() => store.Scalar("UPDATE Favorites SET Server='';"));
        Assert.Throws<SqliteException>(() => store.Scalar("UPDATE Favorites SET DatabaseName='';"));
        Assert.Equal(favorite, await repository.ReadFavoriteAsync(favorite.Favorite.FavoriteId, Token));
    }

    [Fact]
    public async Task FavoriteForeignKeyProtectsRevisionAndContentWithoutAnyOtherIncomingReference()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        // fixture 移除其他根引用，確保不是 Session head 或 Execution 代替 Favorite 擋住刪除。
        store.Scalar("UPDATE Sessions SET LatestRevisionId=NULL,LatestExecutionRevisionId=NULL; DELETE FROM Executions; DELETE FROM Recovery; DELETE FROM History;");
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions;"));
        Assert.Throws<SqliteException>(() => store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Contents;"));
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
        await repository.DeleteFavoriteAsync(favorite.Favorite.FavoriteId, favorite.Version, Token);
        store.Scalar("PRAGMA foreign_keys=ON; DELETE FROM Revisions; DELETE FROM Contents;");
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task RecoveryReplacementAndClosePreserveFavoriteContent()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;", SqlCaptureKind.DraftIdle), Token);
        await store.Process(repository, store.Capture(3, "SELECT * FROM Lib_Tag;", SqlCaptureKind.EditorClosed), Token);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
        Assert.Equal(favorite, await repository.ReadFavoriteAsync(favorite.Favorite.FavoriteId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task PagesFollowLastSaveAndTagsFilterExactlyWithoutReadingSqlBlobs()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        var untagged = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            var id = Guid.NewGuid(); untagged.Add(id);
            await Save(repository, query with { FavoriteId = id }, seconds: i);
        }
        // 同一秒儲存的兩筆以 FavoriteId 決勝，順序仍然穩定。
        var tied = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in tied) await Save(repository, query with { FavoriteId = id }, seconds: 5);
        var server = query with { FavoriteId = Guid.NewGuid(), Server = "LibraryServer" };
        var both = query with { FavoriteId = Guid.NewGuid(), Server = "LibraryServer", Database = "Library" };
        var database = query with { FavoriteId = Guid.NewGuid(), Database = "Library" };
        var otherCase = query with { FavoriteId = Guid.NewGuid(), Server = "libraryserver", Database = "library" };
        foreach (var tagged in new[] { server, both, database, otherCase }) await Save(repository, tagged, seconds: 0);
        // 最早的一筆被改過名字，儲存時間變新，就排到最前面。
        var first = await repository.ReadFavoriteAsync(untagged[0], Token);
        Assert.NotNull(first);
        await Save(repository, first.Favorite with { Name = "重新命名" }, first.Version, 100);

        // 列表不可讀取或驗證全文 BLOB；損壞全文應留到按需讀取時才失敗。
        store.Scalar("UPDATE Contents SET SqlBytes=zeroblob(Length*2);");
        var actual = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(2, cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 2);
            actual.AddRange(page.Items.Select(item => item.Favorite.FavoriteId)); cursor = page.NextCursor;
        } while (cursor != null);
        var expected = new[] { untagged[0] }
            .Concat(tied.Append(untagged[4]).OrderByDescending(Id, StringComparer.Ordinal))
            .Concat(new[] { untagged[3], untagged[2], untagged[1] })
            .Concat(new[] { server, both, database, otherCase }.Select(item => item.FavoriteId).OrderByDescending(Id, StringComparer.Ordinal));
        Assert.Equal(expected, actual);

        async Task<Guid[]> Find(string? serverName, string? databaseName) =>
            (await repository.ReadFavoritesAsync(new SqlFavoriteRequest(20, SqlConnectionNames.One(serverName), SqlConnectionNames.One(databaseName)), Token))
            .Items.Select(item => item.Favorite.FavoriteId).OrderBy(Id, StringComparer.Ordinal).ToArray();
        Guid[] Sorted(params SqlFavorite[] items) => items.Select(item => item.FavoriteId).OrderBy(Id, StringComparer.Ordinal).ToArray();
        // 未指定的一邊不限，但不代表「未標註」；名稱精確、區分大小寫。
        Assert.Equal(Sorted(server, both), await Find("LibraryServer", null));
        Assert.Equal(Sorted(both, database), await Find(null, "Library"));
        Assert.Equal(Sorted(both), await Find("LibraryServer", "Library"));
        Assert.Equal(Sorted(otherCase), await Find("libraryserver", null));
    }

    [Fact]
    public async Task SearchMatchesNameDescriptionAndSqlInsideTheTagFilter()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var reader = await CreateQuery(store, repository);
        await Save(repository, reader, seconds: 1);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;"), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        // 說明為 NULL 的收藏仍須靠 SQL 命中；instr 對 NULL 回傳 NULL，不能因此整列消失。
        var tag = reader with { FavoriteId = Guid.NewGuid(), Name = "標籤清單", Description = null,
            CurrentRevisionId = state.LatestRevision.RevisionId };
        await Save(repository, tag, seconds: 2);
        var tagged = reader with { FavoriteId = Guid.NewGuid(), Server = "LibraryServer" };
        await Save(repository, tagged, seconds: 3);

        async Task<Guid[]> Find(string? search, string? server = null) =>
            (await repository.ReadFavoritesAsync(new SqlFavoriteRequest(20, SqlConnectionNames.One(server), search: search), Token))
            .Items.Select(item => item.Favorite.FavoriteId).ToArray();

        Assert.Equal(new[] { tagged.FavoriteId, reader.FavoriteId }, await Find("圖書館範例"));
        Assert.Equal(new[] { tag.FavoriteId }, await Find("Lib_Tag"));
        Assert.Equal(3, (await Find("SELECT * FROM")).Length);
        Assert.Equal(3, (await Find(null)).Length);
        Assert.Equal(3, (await Find("")).Length);
        foreach (var missing in new[] { "lib_reader", "讀者查詢 ", "' OR 1=1--", "Library.sql" })
            Assert.Empty(await Find(missing));
        // 搜尋只在標註篩選之內；同一段 SQL 在沒標註的收藏也不會被帶進來。
        Assert.Equal(new[] { tagged.FavoriteId }, await Find("Lib_Reader", "LibraryServer"));
        Assert.Empty(await Find("Lib_Tag", "LibraryServer"));
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
            if (wanted) matching.Insert(0, id);
            await Save(repository, query with { FavoriteId = id, Name = wanted ? "借閱報表 " + i : "其他 " + i }, seconds: i);
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
        Assert.Equal(matching, actual);
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
            await Save(repository, query with { FavoriteId = Guid.NewGuid() }, seconds: i);
        using var source = new CancellationTokenSource();
        // 取消可能落在派送前或 BLOB 掃描中；無論哪一種都不得讓 SqliteException 外流。
        var reading = repository.ReadFavoritesAsync(new SqlFavoriteRequest(200, search: "Lib_Reader"), source.Token);
        source.Cancel();
        var error = await Record.ExceptionAsync(() => reading);
        Assert.True(error is null or OperationCanceledException, error?.ToString());
    }

    [Fact]
    public async Task CursorsRejectOtherStoresFiltersHistoryAndMalformedInput()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = await CreateQuery(store, repository);
        await Save(repository, query, seconds: 1);
        await Save(repository, query with { FavoriteId = Guid.NewGuid() }, seconds: 2);
        var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(1), Token);
        Assert.NotNull(page.NextCursor);
        using var second = new SqliteTestStore();
        var other = await second.Open(Token);
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => other.ReadFavoritesAsync(new SqlFavoriteRequest(1, cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadFavoritesAsync(new SqlFavoriteRequest(1, new[] { "LibraryServer" }, cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadFavoritesAsync(new SqlFavoriteRequest(1, databases: new[] { "LibraryServer" }, cursor: page.NextCursor), Token));
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
        var original = await CreateFavorite(store, repository);
        var query = original.Favorite;
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Save(repository, query, original.Version, 1, token: source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Save(repository, query with { CurrentRevisionId = Guid.NewGuid() },
            original.Version, 1, "SELECT * FROM Branch;", source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.DeleteFavoriteAsync(query.FavoriteId, original.Version, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadFavoritesAsync(new SqlFavoriteRequest(1), source.Token));
        Assert.Equal(original, await repository.ReadFavoriteAsync(query.FavoriteId, Token));
    }

    [Fact]
    public async Task EveryTagFilterStreamsAlongItsTimeIndexWithoutTemporarySort()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // 搜尋在讀取端逐列比對；候選查詢仍不得退回全表掃描或暫存排序，否則預算限制不了讀入的 BLOB。
        foreach (var (server, database, index) in new[] { ((string?)null, (string?)null, "IX_Favorites_Time"),
            ("LibraryServer", null, "IX_Favorites_ServerTime"), ("LibraryServer", "Library", "IX_Favorites_ServerDatabaseTime"),
            (null, "Library", "IX_Favorites_DatabaseTime") })
        foreach (var (after, includeSql) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var conditions = new List<string>();
            var parameters = new List<(string Name, object? Value)> { ("$limit", 20) };
            SqliteConnectionFilter.Append(conditions, parameters, "f", server, database);
            if (after)
            {
                conditions.Add("(f.UpdatedAt,f.FavoriteId) < ($time,$key)");
                parameters.Add(("$time", 1L)); parameters.Add(("$key", "f"));
            }
            command.CommandText = "EXPLAIN QUERY PLAN " + SqliteFavoriteStore.FavoritePageSql(conditions, includeSql);
            command.Parameters.Clear();
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, line => line.Split(' ').Contains(index));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
        }
    }

    [Fact]
    public async Task SearchScanBudgetEndsPagesEarlyAndResumesWithoutGapsOrDuplicates()
    {
        using var store = new SqliteTestStore();
        var repository = await SqliteTestRepository.OpenAsync(store.Path, Token, searchBudget: new SqliteSearchBudget(3, long.MaxValue));
        var query = await CreateQuery(store, repository);
        var matching = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            // 名稱與說明各命中一部分，其餘只有 SQL 以外的文字，逼讀取端把預算花在不命中的列上。
            var name = i % 5 == 0 ? "借閱報表 " + i : "其他 " + i;
            var description = i % 7 == 3 ? "含借閱報表說明" : null;
            if (name.Contains("借閱報表") || description != null) matching.Insert(0, id);
            await Save(repository, query with { FavoriteId = id, Name = name, Description = description }, seconds: i);
        }
        var actual = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        var partial = 0;
        do
        {
            var page = await repository.ReadFavoritesAsync(new SqlFavoriteRequest(2, search: "借閱報表", cursor: cursor), Token);
            pages++;
            Assert.InRange(page.Items.Count, 0, 2);
            if (page.IsSearchPartial)
            {
                partial++;
                Assert.NotNull(page.NextCursor);
                // 與 History 相同，部分搜尋回報檢查到的最後儲存時間。
                Assert.NotNull(page.SearchedThrough);
            }
            else Assert.Null(page.SearchedThrough);
            actual.AddRange(page.Items.Select(item => item.Favorite.FavoriteId));
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(matching, actual);
        // 每頁至多檢查 3 筆：12 筆收藏至少要 4 頁，且一定有頁因預算而提早結束。
        Assert.True(pages >= 4, pages.ToString());
        Assert.True(partial > 0);
    }

    [Fact]
    public async Task SavingSqlWritesContentRevisionAndFavoriteWithoutTouchingCaptures()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(), Token);
        var query = new SqlFavorite(Guid.NewGuid(), "館藏複本", "查詢視窗直接收藏", Guid.NewGuid(), "LibraryServer", "Library");
        Assert.Equal(SqlFavoriteWriteResult.Committed, await Save(repository, query, seconds: 3600, sql: DraftSql));
        var favorite = await (await store.Open(Token)).ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        Assert.Equal(query, favorite.Favorite);
        Assert.Equal(DraftSql, favorite.Preview);
        Assert.Equal(SqlContent.Create(DraftSql).ContentId, favorite.ContentId);
        Assert.Equal(DraftSql, (await repository.ReadContentAsync(favorite.ContentId, Token))?.SqlText);
        // 收藏自己的版本：不屬於任何 Session、不進 History、不建 Capture，head 與序號也不動。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + Id(query.CurrentRevisionId) +
            "' AND SessionId IS NULL AND ParentRevisionId IS NULL AND IsExecutionSelection=0 AND Reason=" +
            (int)SqlRevisionReason.Favorite + " AND FavoriteId='" + Id(query.FavoriteId) + "' AND CreatedAt=" + At(3600).UtcTicks + ";"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Sessions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Captures;"));
        // 擷取那一筆留下的草稿與執行投影，收藏沒有再加一列。
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM History;"));
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        Assert.NotEqual(query.CurrentRevisionId, state.LatestRevision.RevisionId);
        Assert.Equal(1L, state.Version);
        Assert.Equal(1L, state.LastSequence);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task SavingSqlWorksWithoutAnyCaptureAndNeverOverwritesAnExistingFavorite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        // 擷取關著（或這個視窗還沒擷取過）也收得起來：收藏不依賴任何 Session 或文件列。
        var tagged = new SqlFavorite(Guid.NewGuid(), "借閱明細", null, Guid.NewGuid(), "LibraryServer", "Library");
        Assert.Equal(SqlFavoriteWriteResult.Committed, await Save(repository, tagged, sql: DraftSql));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Sessions;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Documents;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM History;"));
        Assert.Single((await repository.ReadFavoritesAsync(new SqlFavoriteRequest(5, new[] { "LibraryServer" }, new[] { "Library" }, "Cat_BookCopy"), Token)).Items);
        // 同一個收藏重送不覆寫；名稱、標註與版本都留原樣，敗方的版本與內容也不留下。
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await Save(repository,
            tagged with { Name = "改名", Server = null, CurrentRevisionId = Guid.NewGuid() }, seconds: 1, sql: "SELECT * FROM Branch;"));
        var current = await repository.ReadFavoriteAsync(tagged.FavoriteId, Token);
        Assert.NotNull(current);
        Assert.Equal(tagged, current.Favorite);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task EditingDataAndSqlTogetherIsOneTransactionWithoutHistoryOrSession()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        // 名稱、標註與 SQL 一次儲存：兩個版本檢查的舊流程第一次成功就會讓第二次衝突。
        var edited = favorite.Favorite with { Name = "標籤查詢", Server = "LibraryServer", CurrentRevisionId = Guid.NewGuid() };
        Assert.Equal(SqlFavoriteWriteResult.Committed, await Save(repository, edited, favorite.Version, 3600, EditedSql));
        var read = await (await store.Open(Token)).ReadFavoriteAsync(favorite.Favorite.FavoriteId, Token);
        Assert.NotNull(read);
        Assert.Equal(edited, read.Favorite);
        Assert.Equal(At(3600), read.UpdatedAt);
        Assert.Equal(SqlContent.Create(EditedSql).ContentId, read.ContentId);
        Assert.Equal(EditedSql, read.Preview);
        Assert.NotEqual(favorite.Version, read.Version);
        // 舊版本與其歷史都留著；新版本不進 History、不屬於任何 Session，也不改 head 或序號。
        Assert.NotNull(await repository.ReadContentAsync(favorite.ContentId, Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Sessions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Captures;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM History;"));
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        Assert.Equal(favorite.Favorite.CurrentRevisionId, state.LatestRevision.RevisionId);
        Assert.Equal(1L, state.Version);
        Assert.Equal(Id(edited.CurrentRevisionId), store.Scalar("SELECT RevisionId FROM Revisions WHERE FavoriteId IS NOT NULL;"));
        Assert.Empty((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.All, "TagId=1"), Token)).Items);
        Assert.Single((await repository.ReadFavoritesAsync(new SqlFavoriteRequest(5, new[] { "LibraryServer" }, search: "TagId=1"), Token)).Items);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task EditingSqlRejectsStaleVersionsMissingQueriesAndCancellationWithoutPartialWrites()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = await CreateFavorite(store, repository);
        var other = await store.Open(Token);
        var results = await Task.WhenAll(
            Save(repository, favorite.Favorite with { CurrentRevisionId = Guid.NewGuid() }, favorite.Version, 3600, EditedSql),
            other.SaveFavoriteAsync(new SqlFavoriteSave(favorite.Favorite with { CurrentRevisionId = Guid.NewGuid() },
                favorite.Version, At(7200), "SELECT * FROM Loan;"), Token));
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Committed);
        Assert.Single(results, result => result == SqlFavoriteWriteResult.Conflict);
        // 敗方連版本與內容都不留下，重送不冪等，呼叫端必須重讀。
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await Save(repository,
            favorite.Favorite with { FavoriteId = Guid.NewGuid(), CurrentRevisionId = Guid.NewGuid() }, favorite.Version, sql: EditedSql));
        var current = await repository.ReadFavoriteAsync(favorite.Favorite.FavoriteId, Token);
        Assert.NotNull(current);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
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
