using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReopenPreservesContentSessionAndExecutionWithConnection()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var context = new SqlConnectionLabel("LibraryServer", "Library");
        await store.Process(repository, store.Capture(context: context), Token);
        var reopened = await store.Open(Token);
        var session = await reopened.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(session);
        Assert.Equal(1, session.Version);
        var page = await reopened.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Executions), Token);
        var entry = Assert.Single(page.Items);
        Assert.Equal(context, entry.Connection);
        Assert.Equal(session.LatestRevision?.RevisionId, entry.RevisionId);
        Assert.Equal("SELECT * FROM Lib_Reader;", (await reopened.ReadContentAsync(entry.ContentId, Token))?.SqlText);
        Assert.Equal("wal", store.Scalar("PRAGMA journal_mode;"));
        Assert.Equal(5L, store.Scalar("PRAGMA user_version;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task TwentyExecutionsDeduplicateAcrossRepositoryInstancesAndReplay()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 20; i++)
            await store.Process(repository, store.Capture(i), Token);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Equal(20L, store.Scalar("SELECT count(*) FROM Executions;"));
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        var capture = store.Capture(21);
        var write = new SqlCapturePlanner().Prepare(capture, state, SqliteTestStore.Policy);
        Assert.NotNull(write);
        Assert.Equal(SqlHistoryCommitResult.Committed, await repository.CommitAsync(write, null, Token));
        Assert.Equal(SqlHistoryCommitResult.AlreadyCommitted, await (await store.Open(Token)).CommitAsync(write, null, Token));
        Assert.Equal(21L, store.Scalar("SELECT count(*) FROM Executions;"));
    }

    [Fact]
    public async Task RecoveryOnlyKeepsLatestContentWithoutOrphanGrowth()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var policy = new SqlCapturePolicy(true, false, TimeSpan.FromMinutes(10), true, true);
        for (var i = 1; i <= 20; i++)
            await store.Process(repository, store.Capture(i, "SELECT " + i, SqlCaptureKind.DraftIdle), Token, policy);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Revisions;"));
        var draft = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Drafts), Token)).Items);
        Assert.Equal("SELECT 20", (await repository.ReadContentAsync(draft.ContentId, Token))?.SqlText);
        await store.Process(repository, store.Capture(21, "SELECT 21", SqlCaptureKind.EditorClosed), Token, policy);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.NotNull((await repository.ReadSessionAsync(store.Session.SessionId, Token))?.Session.ClosedAt);
    }

    [Fact]
    public async Task RecoveryReplacementKeepsContentsReferencedByAnotherSession()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var second = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        var policy = new SqlCapturePolicy(true, false, TimeSpan.FromMinutes(10), true, true);
        await store.Process(repository, store.Capture(sql: "SELECT 1", kind: SqlCaptureKind.DraftIdle), Token, policy);
        await store.Process(repository, store.Capture(sql: "SELECT 1", kind: SqlCaptureKind.DraftIdle, session: second), Token, policy);
        await store.Process(repository, store.Capture(2, "SELECT 2", SqlCaptureKind.DraftIdle), Token, policy);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.NotNull(await repository.ReadContentAsync(SqlContent.Create("SELECT 1").ContentId, Token));
    }

    [Fact]
    public async Task SelectionAndDraftUseDifferentContentAndReuseSelectionRevision()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(selected: "SELECT 1"), Token);
        await store.Process(repository, store.Capture(2, selected: "SELECT 1"), Token);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        var executions = await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Executions), Token);
        Assert.All(executions.Items, item => Assert.Equal(SqlContent.Create("SELECT 1").ContentId, item.ContentId));
        var draft = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Drafts), Token)).Items);
        Assert.Equal("SELECT * FROM Lib_Reader;", (await repository.ReadContentAsync(draft.ContentId, Token))?.SqlText);
    }

    [Fact]
    public async Task CompareAndSwapRejectsStalePlanWithoutPartialRows()
    {
        using var store = new SqliteTestStore();
        var first = await store.Open(Token);
        var second = await store.Open(Token);
        var engine = new SqlCapturePlanner();
        var write = engine.Prepare(store.Capture(), null, SqliteTestStore.Policy);
        var stale = engine.Prepare(store.Capture(2, "SELECT 2"), null, SqliteTestStore.Policy);
        Assert.NotNull(write); Assert.NotNull(stale);
        Assert.Equal(SqlHistoryCommitResult.Committed, await first.CommitAsync(write, null, Token));
        Assert.Equal(SqlHistoryCommitResult.Conflict, await second.CommitAsync(stale, null, Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Captures;"));
    }

    [Fact]
    public async Task FailureAfterContentInsertionRollsBackWholeTransaction()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(), Token);
        store.Scalar("CREATE TRIGGER FailExecution BEFORE INSERT ON Executions BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.Process(repository, store.Capture(2, "SELECT 2"), Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Equal(1L, store.Scalar("SELECT Version FROM Sessions;"));
        Assert.Equal(SqliteTestStore.Start.UtcTicks, store.Scalar("SELECT CapturedAt FROM Recovery;"));
    }

    [Fact]
    public async Task ContentCorruptionIsAlwaysDetectedOnReadEvenAfterADedupHitWrite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(sql: "SELECT 1"), Token);
        var id = SqlContent.Create("SELECT 1").ContentId;
        store.Scalar("UPDATE Contents SET SqlBytes=zeroblob(Length*2);");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadContentAsync(id, Token));
        // 去重命中只比對 Length（ContentId 本身就是雜湊，見 docs/sql-memory-storage.md 的取捨），不再讀回整份
        // BLOB 比對；只有 SqlBytes 本體損壞、中繼資料仍相符時，寫入路徑不會擋下，留給下一次讀取抓到。
        await store.Process(repository, store.Capture(2, "SELECT 1"), Token);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadContentAsync(id, Token));
    }

    [Fact]
    public async Task MetadataCorruptionIsStillCaughtOnDedupHitWrite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(sql: "SELECT 1"), Token);
        // 長度與本體一起錯位（仍滿足長度 CHECK）時，寫入路徑的快速比對照樣擋下，不把新擷取掛到壞內容上。
        store.Scalar("UPDATE Contents SET Length=Length+1, SqlBytes=zeroblob(2 * (Length+1));");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Process(repository, store.Capture(2, "SELECT 1"), Token));
    }

    [Fact]
    public async Task EmbeddedNullAndUnpairedSurrogateRoundTripExactly()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var sql = "SELECT N'讀者📚';\0" + new string((char)0xd800, 1) + "\r\n";
        await store.Process(repository, store.Capture(sql: sql), Token);
        var restored = await repository.ReadContentAsync(SqlContent.Create(sql).ContentId, Token);
        Assert.Equal(sql, restored?.SqlText);
    }

    [Fact]
    public async Task SimultaneousInitializationAndWritersUseIndependentConnections()
    {
        using var store = new SqliteTestStore();
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        await Task.WhenAll(repositories.Select(repository => store.Process(repository,
            store.Capture(session: new SqlSession(Guid.NewGuid(), store.Document.DocumentId)), Token)));
        Assert.Equal(4L, store.Scalar("SELECT count(*) FROM Sessions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FreshSchemaCreatesFavoriteIndexesLeaseForeignKeyAndUsageTogether()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        Assert.Equal(0x534d454dL, store.Scalar("PRAGMA application_id;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM StoreInfo;"));
        Assert.Equal(0L, store.Scalar("SELECT ContentBytes FROM StorageUsage;"));
        Assert.Contains("Favorites", store.Query("SELECT name FROM sqlite_master WHERE type='table';"));
        foreach (var index in new[] { "IX_Favorites_Time", "IX_Favorites_ServerTime", "IX_Favorites_ServerDatabaseTime", "IX_Favorites_DatabaseTime" })
            Assert.Contains(index, store.Query("SELECT name FROM sqlite_master WHERE type='index';"));
        // 連線只留在 History 投影與收藏標註；不再有沒人讀的連線表或只寫不讀的欄位。
        Assert.DoesNotContain("Contexts", store.Query("SELECT name FROM sqlite_master WHERE type='table';"));
        Assert.Equal(0L, store.Scalar(@"SELECT (SELECT count(*) FROM pragma_table_info('Contents') WHERE name='ContentHash')
 + (SELECT count(*) FROM pragma_table_info('Documents') WHERE name='FilePath');"));
        Assert.Contains("IX_Revisions_Favorite", store.Query("SELECT name FROM sqlite_master WHERE type='index';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM pragma_foreign_key_list('Sessions') WHERE \"table\"='Leases' AND \"from\"='LeaseId';"));
        // 版本要嘛屬於一個 Session，要嘛屬於一個收藏；兩者皆空的列沒有任何配額界線可套用。
        Assert.Throws<SqliteException>(() => store.Scalar(
            "INSERT INTO Contents VALUES('c',x'4100',1,'A');" +
            "INSERT INTO Revisions VALUES('r',NULL,'c',NULL,0,0,0,NULL);"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task UnsupportedSchemaIsRejectedWithoutChangingContents(int version)
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(), Token);
        var identity = store.Scalar("SELECT StoreId FROM StoreInfo;");
        store.Scalar("PRAGMA user_version=" + version + ";");
        await AssertIncompatible(() => store.Open(Token));
        Assert.Equal((long)version, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(identity, store.Scalar("SELECT StoreId FROM StoreInfo;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
    }

    [Fact]
    public async Task FutureForeignAndCorruptDatabasesAreNotRecreated()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        store.Scalar("PRAGMA user_version=100;");
        await AssertIncompatible(() => store.Open(Token));
        Assert.Equal(100L, store.Scalar("PRAGMA user_version;"));
        File.WriteAllText(store.Path, "not a sqlite database");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.Open(Token));
        Assert.Equal("not a sqlite database", File.ReadAllText(store.Path));
        using var foreign = new SqliteTestStore();
        Directory.CreateDirectory(foreign.DirectoryPath);
        foreign.Scalar("CREATE TABLE Lib_Reader(Id INTEGER);");
        await AssertIncompatible(() => foreign.Open(Token));
        Assert.Equal("delete", foreign.Scalar("PRAGMA journal_mode;"));
    }

    private static async Task AssertIncompatible(Func<Task> open)
    {
        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(open);
        Assert.Equal(SqlMemoryStorageErrorKind.Incompatible, error.Kind);
    }
}
