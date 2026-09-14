using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReopenPreservesContentSessionAndExecutionWithConnection()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var context = new QueryConnectionContext("LibraryServer", "Library", "ReaderIdentity");
        await store.Process(repository, store.Capture(context: context), Token);
        var reopened = await store.Open(Token);
        var session = await reopened.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(session);
        Assert.Equal(1, session.Version);
        var page = await reopened.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Executed), Token);
        var entry = Assert.Single(page.Items);
        Assert.Equal(context, entry.Connection);
        Assert.Equal(session.LatestRevision?.RevisionId, entry.RevisionId);
        Assert.Equal("SELECT * FROM Lib_Reader;", (await reopened.ReadContentAsync(entry.ContentId, Token))?.SqlText);
        Assert.Equal("wal", store.Scalar("PRAGMA journal_mode;"));
        Assert.Equal(1L, store.Scalar("PRAGMA user_version;"));
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
        var write = new QueryRevisionEngine().Prepare(capture, state, SqliteTestStore.Policy);
        Assert.NotNull(write);
        Assert.Equal(QueryMemoryCommitResult.Committed, await repository.CommitAsync(write, null, Token));
        Assert.Equal(QueryMemoryCommitResult.AlreadyCommitted, await (await store.Open(Token)).CommitAsync(write, null, Token));
        Assert.Equal(21L, store.Scalar("SELECT count(*) FROM Executions;"));
    }

    [Fact]
    public async Task RecoveryOnlyKeepsLatestContentWithoutOrphanGrowth()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var policy = new QueryMemoryPolicy(true, false, TimeSpan.FromMinutes(10), true, true);
        for (var i = 1; i <= 20; i++)
            await store.Process(repository, store.Capture(i, "SELECT " + i, QueryCaptureKind.DraftIdle), Token, policy);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Revisions;"));
        var draft = Assert.Single((await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Drafts), Token)).Items);
        Assert.Equal("SELECT 20", (await repository.ReadContentAsync(draft.ContentId, Token))?.SqlText);
        await store.Process(repository, store.Capture(21, "SELECT 21", QueryCaptureKind.EditorClosed), Token, policy);
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
        var second = new QuerySession(Guid.NewGuid(), store.Document.DocumentId, SqliteTestStore.Start);
        var policy = new QueryMemoryPolicy(true, false, TimeSpan.FromMinutes(10), true, true);
        await store.Process(repository, store.Capture(sql: "SELECT 1", kind: QueryCaptureKind.DraftIdle), Token, policy);
        await store.Process(repository, store.Capture(sql: "SELECT 1", kind: QueryCaptureKind.DraftIdle, session: second), Token, policy);
        await store.Process(repository, store.Capture(2, "SELECT 2", QueryCaptureKind.DraftIdle), Token, policy);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.NotNull(await repository.ReadContentAsync(QueryContent.Create("SELECT 1").ContentId, Token));
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
        var executions = await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Executed), Token);
        Assert.All(executions.Items, item => Assert.Equal(QueryContent.Create("SELECT 1").ContentId, item.ContentId));
        var draft = Assert.Single((await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Drafts), Token)).Items);
        Assert.Equal("SELECT * FROM Lib_Reader;", (await repository.ReadContentAsync(draft.ContentId, Token))?.SqlText);
    }

    [Fact]
    public async Task CompareAndSwapRejectsStalePlanWithoutPartialRows()
    {
        using var store = new SqliteTestStore();
        var first = await store.Open(Token);
        var second = await store.Open(Token);
        var engine = new QueryRevisionEngine();
        var write = engine.Prepare(store.Capture(), null, SqliteTestStore.Policy);
        var stale = engine.Prepare(store.Capture(2, "SELECT 2"), null, SqliteTestStore.Policy);
        Assert.NotNull(write); Assert.NotNull(stale);
        Assert.Equal(QueryMemoryCommitResult.Committed, await first.CommitAsync(write, null, Token));
        Assert.Equal(QueryMemoryCommitResult.Conflict, await second.CommitAsync(stale, null, Token));
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
        Assert.Equal(1L, store.Scalar("SELECT Sequence FROM Recovery;"));
    }

    [Fact]
    public async Task ContentCorruptionIsDetectedOnReadAndDeduplication()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(sql: "SELECT 1"), Token);
        var id = QueryContent.Create("SELECT 1").ContentId;
        store.Scalar("UPDATE Contents SET SqlBytes=zeroblob(Length*2);");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadContentAsync(id, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Process(repository, store.Capture(2, "SELECT 1"), Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
    }

    [Fact]
    public async Task EmbeddedNullAndUnpairedSurrogateRoundTripExactly()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var sql = "SELECT N'讀者📚';\0" + new string((char)0xd800, 1) + "\r\n";
        await store.Process(repository, store.Capture(sql: sql), Token);
        var restored = await repository.ReadContentAsync(QueryContent.Create(sql).ContentId, Token);
        Assert.Equal(sql, restored?.SqlText);
    }

    [Fact]
    public async Task SimultaneousInitializationAndWritersUseIndependentConnections()
    {
        using var store = new SqliteTestStore();
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        await Task.WhenAll(repositories.Select(repository => store.Process(repository,
            store.Capture(session: new QuerySession(Guid.NewGuid(), store.Document.DocumentId, SqliteTestStore.Start)), Token)));
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
        Assert.Contains("FavoriteQueries", store.Query("SELECT name FROM sqlite_master WHERE type='table';"));
        Assert.Contains("IX_FavoriteQueries_ScopeId", store.Query("SELECT name FROM sqlite_master WHERE type='index';"));
        Assert.Contains("IX_Revisions_Favorite", store.Query("SELECT name FROM sqlite_master WHERE type='index';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM pragma_foreign_key_list('Sessions') WHERE \"table\"='Leases' AND \"from\"='LeaseId';"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM pragma_table_info('FavoriteQueries') WHERE name='Pinned';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
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
        var error = await Assert.ThrowsAsync<QueryMemoryStorageException>(open);
        Assert.Equal(QueryMemoryStorageErrorKind.Incompatible, error.Kind);
    }
}
