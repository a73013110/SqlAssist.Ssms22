using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;
using SqlAssist.QueryMemory.Hosting;
using Xunit;
using static SqlAssist.QueryMemory.Sqlite.Tests.SqliteTestStore;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteMaintenanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<QueryRevision> SeedLeaf(SqliteTestStore store, SqliteQueryMemoryRepository repository)
    {
        await store.Process(repository, store.Capture(selected: "SELECT * FROM Lib_Tag;",
            context: new QueryConnectionContext("LibraryServer", "Library")), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestExecutionRevision);
        await store.Process(repository, store.Capture(2, selected: "SELECT * FROM Loan;", seconds: 1), Token);
        return state.LatestExecutionRevision;
    }

    private static async Task<List<QueryRevision>> SeedAutoDrafts(SqliteTestStore store, SqliteQueryMemoryRepository repository,
        QuerySession? session, int count, int startSeconds)
    {
        var drafts = new List<QueryRevision>();
        for (var index = 0; index < count; index++)
        {
            await store.Process(repository, store.Capture(index + 1, "SELECT * FROM Loan WHERE Branch=" + (startSeconds + index) + ";",
                QueryCaptureKind.DraftIdle, seconds: startSeconds + 600 * index, session: session), Token);
            var state = await repository.ReadSessionAsync((session ?? store.Session).SessionId, Token);
            Assert.NotNull(state?.LatestRevision);
            drafts.Add(state.LatestRevision);
        }
        return drafts;
    }

    [Fact]
    public async Task ExecutionQuotaReclaimsInDateEventsAndTheirSelectionRevisions()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var sequence = 1; sequence <= 6; sequence++)
            await store.Process(repository, store.Capture(sequence,
                selected: "SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo=" + sequence + ";", seconds: sequence), Token);
        Assert.Equal(6L, store.Scalar("SELECT count(*) FROM Executions;"));
        // 只給筆數配額、不給截止時間：期限內但超額的執行仍該回收。
        var result = await Drain(repository, new QueryMemoryMaintenancePolicy(null, null, null, 2));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM History WHERE Kind=1;"));
        // 超額執行的選取版本一併回收，配額不會只留下無法回收的孤立版本。
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions WHERE IsExecutionSelection=1;"));
        Assert.Equal(store.Scalar("SELECT SUM(2 * Length) FROM Contents;"), result.Usage.ContentBytes);
        Assert.Equal(QueryMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ExecutionQuotaKeepsTiedTimestampsAndProtectedRootsAboveTheLimit()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var sequence = 1; sequence <= 4; sequence++)
            await store.Process(repository, store.Capture(sequence,
                selected: "SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo=" + sequence + ";"), Token);
        var oldest = store.Scalar("SELECT RevisionId FROM Executions ORDER BY ExecutedAt,ExecutionId LIMIT 1;");
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(new FavoriteQuery(Guid.NewGuid(), "讀者收藏", null,
            Guid.ParseExact((string)oldest!, "N"), FavoriteQueryScope.Global, null)), Token);
        // 四次執行共用同一個時間，第 1 新的界線不比任何列新，配額因此不刪任何一列。
        await Drain(repository, new QueryMemoryMaintenancePolicy(null, null, null, 1));
        Assert.Equal(4L, store.Scalar("SELECT count(*) FROM Executions;"));
        // 有截止時間時 Favorite 引用的執行仍受保護，配額不會越過保護根。
        await Drain(repository, new QueryMemoryMaintenancePolicy(null, SqliteTestStore.Start.AddDays(1), null, 1));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(oldest, store.Scalar("SELECT RevisionId FROM Executions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task AutoRevisionQuotaTrimsEachSessionDraftListWithoutCollapsingProtectedChains()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var second = new QuerySession(Guid.NewGuid(), store.Document.DocumentId, SqliteTestStore.Start);
        var first = await SeedAutoDrafts(store, repository, null, 3, 0);
        var other = await SeedAutoDrafts(store, repository, second, 2, 1800);
        store.Scalar("UPDATE History SET Pinned=1 WHERE EntryKey='r" + first[0].RevisionId.ToString("N") + "';");
        var revisions = store.Scalar("SELECT count(*) FROM Revisions;");
        // 只設每 Session 配額：較舊 Session 的最新草稿不因另一個 Session 更新而被淘汰。
        await Drain(repository, new QueryMemoryMaintenancePolicy(null, null, null, null, 1));
        foreach (var (session, kept) in new[] { (store.Session.SessionId, new[] { first[0], first[2] }), (second.SessionId, new[] { other[1] }) })
            Assert.Equal(kept.Select(draft => "r" + draft.RevisionId.ToString("N")).OrderBy(key => key, StringComparer.Ordinal),
                store.Query("SELECT EntryKey FROM History WHERE SessionId='" + session.ToString("N") +
                    "' AND EntryKey LIKE 'r%' ORDER BY EntryKey;"));
        // ParentRevision 鏈仍保護版本本身；配額只縮短清單投影，不代表版本鏈已回收。
        Assert.Equal(revisions, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.NotNull(await repository.ReadContentAsync(first[1].ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task QuotaBoundariesReadOnlyTheNewestRowsThroughIndexesWithoutTemporarySorts()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        Assert.Equal(0, (int)QueryRevisionReason.AutoCheckpoint);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        var plans = new[]
        {
            ("SELECT ExecutedAt FROM Executions ORDER BY ExecutedAt DESC LIMIT 1 OFFSET 9999;", "IX_Executions_Time"),
            ("SELECT CreatedAt FROM Revisions WHERE SessionId='test' AND Reason=0 AND IsExecutionSelection=0" +
                " ORDER BY CreatedAt DESC LIMIT 1 OFFSET 49;", "IX_Revisions_SessionAuto"),
        };
        foreach (var (sql, index) in plans)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Contains(index, reader.GetString(3));
            Assert.False(reader.Read());
        }
    }

    [Fact]
    public async Task ExpiredLeafAndOrphansAreReclaimedButHeadsAndCaptureReplaySurvive()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        var before = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        var result = await Drain(repository, Expired());
        Assert.Null(await repository.ReadContentAsync(leaf.ContentId, Token));
        Assert.Equal(before, await repository.ReadSessionAsync(store.Session.SessionId, Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Captures;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
        Assert.Equal(QueryMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
        Assert.Equal(store.Scalar("SELECT SUM(2 * Length) FROM Contents;"), result.Usage.ContentBytes);
        var replay = store.Capture(3, selected: "SELECT * FROM Loan;", seconds: 2);
        var write = new QueryRevisionEngine().Prepare(replay, before, SqliteTestStore.Policy);
        Assert.NotNull(write);
        await repository.CommitAsync(write, null, Token);
        await Drain(repository, Expired());
        Assert.Equal(QueryMemoryCommitResult.AlreadyCommitted, await repository.CommitAsync(write, null, Token));
    }

    [Theory]
    [InlineData("favorite")]
    [InlineData("pinned")]
    [InlineData("manual")]
    [InlineData("parent")]
    public async Task EveryRevisionRootSurvivesEvenWhenNotAHead(string root)
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        var id = leaf.RevisionId.ToString("N");
        switch (root)
        {
            case "favorite":
                await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(new FavoriteQuery(Guid.NewGuid(), "讀者收藏", null,
                    leaf.RevisionId, FavoriteQueryScope.Global, null)), Token);
                break;
            case "pinned": store.Scalar("UPDATE History SET Pinned=1 WHERE RevisionId='" + id + "';"); break;
            case "manual": store.Scalar("UPDATE Revisions SET Reason=3 WHERE RevisionId='" + id + "';"); break;
            case "parent":
                store.Scalar("UPDATE Revisions SET ParentRevisionId='" + id + "' WHERE RevisionId=(SELECT LatestExecutionRevisionId FROM Sessions);");
                break;
        }
        var result = await Drain(repository, Expired(0), 1);
        Assert.NotNull(await repository.ReadContentAsync(leaf.ContentId, Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + id + "';"));
        Assert.Equal(QueryMemoryCapacityStatus.CannotReclaimWithinPolicy, result.CapacityStatus);
        Assert.True(result.Usage.ContentBytes > 0);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task RecoveryIsNotExpiredByAgeAndReplacedContentUpdatesUsage()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var policy = new QueryMemoryPolicy(true, false, TimeSpan.FromMinutes(10), false, true);
        await store.Process(repository, store.Capture(kind: QueryCaptureKind.DraftIdle), Token, policy);
        var usage = await repository.ReadUsageAsync(Token);
        Assert.Equal(2 * "SELECT * FROM Lib_Reader;".Length, usage.ContentBytes);
        var result = await Drain(repository, Expired(0));
        Assert.Equal(usage.ContentBytes, result.Usage.ContentBytes);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM History;"));
        await store.Process(repository, store.Capture(2, "SELECT * FROM Loan;", QueryCaptureKind.DraftIdle), Token, policy);
        Assert.Equal(2 * "SELECT * FROM Loan;".Length, (await repository.ReadUsageAsync(Token)).ContentBytes);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledAndExclusiveCutoffsDoNotEvictRecentDataForCapacity(bool enabled)
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedLeaf(store, repository);
        var policy = new QueryMemoryMaintenancePolicy(enabled ? SqliteTestStore.Start : null,
            enabled ? SqliteTestStore.Start : null, 0);
        var result = await Drain(repository, policy);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(QueryMemoryCapacityStatus.CannotReclaimWithinPolicy, result.CapacityStatus);
    }

    [Fact]
    public async Task CursorSurvivesReopenAndRejectsDifferentStorePolicyAndMalformedInput()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedLeaf(store, repository);
        var policy = Expired();
        var first = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 1), Token);
        Assert.NotNull(first.Cursor);
        using var other = new SqliteTestStore();
        var otherRepository = await other.Open(Token);
        await Assert.ThrowsAsync<QueryMemoryStorageException>(() => otherRepository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 1, first.Cursor), Token));
        await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(0), 1, first.Cursor), Token));
        var quota = new QueryMemoryMaintenancePolicy(policy.DraftBefore, policy.ExecutionBefore, null, 10, 50);
        await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.MaintainAsync(new QueryMemoryMaintenanceRequest(quota, 1, first.Cursor), Token));
        foreach (var cursor in new[] { "!", Convert.ToBase64String(new byte[20]), new string('a', 1025) })
            await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 1, cursor), Token));
        var reopened = await store.Open(Token);
        await Drain(reopened, policy, 3, first.Cursor);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FailureAfterHistoryDeleteRollsBackBatchAndCanRetry()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedLeaf(store, repository);
        store.Scalar("CREATE TRIGGER FailDelete BEFORE DELETE ON Executions BEGIN SELECT RAISE(ABORT,'保留測試證據'); END;");
        var before = await repository.ReadUsageAsync(Token);
        await Assert.ThrowsAsync<SqliteException>(() => repository.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(), 500), Token));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM History WHERE Kind=1;"));
        Assert.Equal(before.ContentBytes, (await repository.ReadUsageAsync(Token)).ContentBytes);
        store.Scalar("DROP TRIGGER FailDelete;");
        await Drain(await store.Open(Token), Expired());
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Executions;"));
    }

    [Fact]
    public async Task CancellationWhileWaitingForWriterDoesNotCommitAndCursorCanResume()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedLeaf(store, repository);
        var first = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(), 1), Token);
        var count = store.Scalar("SELECT count(*) FROM Executions;");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cancellation = new CancellationTokenSource();
        var pending = repository.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(), 1, first.Cursor), cancellation.Token);
        cancellation.Cancel();
        transaction.Rollback();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(count, store.Scalar("SELECT count(*) FROM Executions;"));
        await Drain(await store.Open(Token), Expired(), 1, first.Cursor);
    }

    [Fact]
    public async Task FavoriteUpdateRacingMaintenanceNeverLeavesDanglingReferenceOrPartialContext()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestExecutionRevision);
        var favorite = new FavoriteQuery(Guid.NewGuid(), "讀者收藏", null, state.LatestExecutionRevision.RevisionId, FavoriteQueryScope.Global, null);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(favorite), Token);
        var original = await repository.ReadFavoriteQueryAsync(favorite.FavoriteQueryId, Token);
        Assert.NotNull(original);
        var other = await store.Open(Token);
        var changed = favorite with { CurrentRevisionId = leaf.RevisionId, Scope = FavoriteQueryScope.Database,
            Connection = new QueryConnectionContext("BranchServer", "Library") };
        var update = Record.ExceptionAsync(async () =>
            Assert.Equal(FavoriteQueryWriteResult.Committed, await other.WriteFavoriteQueryAsync(new FavoriteQueryWrite(changed, original.Version), Token)));
        await Drain(repository, Expired(), 500);
        var error = await update;
        if (error != null) Assert.Equal(19, Assert.IsType<SqliteException>(error).SqliteErrorCode);
        var current = await other.ReadFavoriteQueryAsync(favorite.FavoriteQueryId, Token);
        Assert.NotNull(current);
        Assert.Equal(error == null ? changed : favorite, current.Query);
        Assert.NotNull(await other.ReadContentAsync(current.ContentId, Token));
        Assert.Equal(error == null ? 1L : 0L, store.Scalar("SELECT count(*) FROM Contexts WHERE Server='BranchServer';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FavoriteReferenceAddedBetweenBatchesProtectsAlreadyVisitedCandidate()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        // 第一批只處理兩個 Execution；下一批必須重新檢查新加入的 Favorite 根。
        var first = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(), 2), Token);
        Assert.NotNull(first.Cursor);
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(new FavoriteQuery(Guid.NewGuid(), "讀者收藏", null,
            leaf.RevisionId, FavoriteQueryScope.Global, null)), Token);
        await Drain(await store.Open(Token), Expired(), 1, first.Cursor);
        Assert.NotNull(await repository.ReadContentAsync(leaf.ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task IsolatedMaintenanceDtosRoundTripAndReportLogicalVersusPhysicalUsage()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        using var isolated = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, Token);
        var before = await isolated.ReadUsageAsync(Token);
        Assert.True(before.DatabaseFileBytes > 0);
        Assert.True(before.WalFileBytes >= 0);
        var after = await Drain(isolated, Expired(0), 1);
        Assert.True(after.Usage.ContentBytes < before.ContentBytes);
        Assert.Null(await isolated.ReadContentAsync(leaf.ContentId, Token));
        Assert.Equal(QueryMemoryCapacityStatus.CannotReclaimWithinPolicy, after.CapacityStatus);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => isolated.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(), 1), cancellation.Token));
    }

    [Fact]
    public async Task KeysetAndForeignKeyProbesUseIndexesWithoutTemporarySorts()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        var probes = new List<(string Table, string Column)>
        {
            ("Revisions", "ParentRevisionId"), ("Sessions", "LatestRevisionId"), ("Sessions", "LatestExecutionRevisionId"),
            ("Executions", "RevisionId"), ("History", "RevisionId"), ("FavoriteQueries", "CurrentRevisionId"),
        };
        probes.AddRange(new[] { "Revisions", "Executions", "Recovery", "History", "FavoriteQueries" }.Select(table => (table, "ContextId")));
        foreach (var probe in probes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT 1 FROM " + probe.Table + " WHERE " + probe.Column + "='test';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Contains("SEARCH", reader.GetString(3));
            Assert.Contains("INDEX", reader.GetString(3));
        }
        using var candidate = connection.CreateCommand();
        candidate.CommandText = "EXPLAIN QUERY PLAN SELECT RevisionId FROM Revisions WHERE RevisionId>'test' ORDER BY RevisionId LIMIT 1;";
        using var plan = candidate.ExecuteReader();
        Assert.True(plan.Read());
        Assert.Contains("SEARCH", plan.GetString(3));
        Assert.False(plan.Read());
    }

    [Fact]
    public async Task PinProbeSeeksBothColumnsInsteadOfScanningAllExecutionsOfARevision()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT 1 FROM History WHERE RevisionId='test' AND Pinned=1;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Contains("RevisionId=? AND Pinned=?", reader.GetString(3));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task OrphanCollectionIsBoundedEvenWithRetentionDisabledAndUsageRollsBackWithDeletes()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        long bytes = 0;
        for (var i = 0; i < 17; i++)
        {
            var content = QueryContent.Create("SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo=" + i + ";");
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Contents VALUES($id,$hash,$bytes,$length,$preview);";
            insert.Parameters.AddWithValue("$id", content.ContentId);
            insert.Parameters.AddWithValue("$hash", content.ContentHash);
            insert.Parameters.AddWithValue("$bytes", Encoding.Unicode.GetBytes(content.SqlText));
            insert.Parameters.AddWithValue("$length", content.Length);
            insert.Parameters.AddWithValue("$preview", content.SqlText);
            insert.ExecuteNonQuery();
            bytes += 2 * content.Length;
        }
        store.Scalar("INSERT INTO Contexts VALUES('orphan','LibraryServer','Library',NULL);");
        Assert.Equal(bytes, (await repository.ReadUsageAsync(Token)).ContentBytes);
        store.Scalar(@"CREATE TRIGGER FailSecondContent BEFORE DELETE ON Contents
 WHEN (SELECT count(*) FROM Contents)<17 BEGIN SELECT RAISE(ABORT,'測試整批回復'); END;");
        var policy = new QueryMemoryMaintenancePolicy(null, null, 0);
        await Assert.ThrowsAsync<SqliteException>(() => repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 3), Token));
        Assert.Equal(17L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(bytes, (await repository.ReadUsageAsync(Token)).ContentBytes);
        store.Scalar("DROP TRIGGER FailSecondContent;");
        var first = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 3), Token);
        Assert.Equal(3, first.ExaminedCandidates);
        Assert.Equal(3, first.DeletedRows);
        Assert.Equal(14L, store.Scalar("SELECT count(*) FROM Contents;"));
        var result = await Drain(await store.Open(Token), policy, 2, first.Cursor);
        Assert.Equal(0, result.Usage.ContentBytes);
        Assert.Equal(QueryMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task MultiplePassesCollectUnrootedParentChainWithoutRewritingSurvivors()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var revisions = new List<QueryRevision>();
        for (var sequence = 1; sequence <= 5; sequence++)
        {
            await store.Process(repository, store.Capture(sequence,
                selected: "SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo=" + sequence + ";"), Token);
            var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
            Assert.NotNull(state?.LatestExecutionRevision);
            revisions.Add(state.LatestExecutionRevision);
        }
        // 最後一個 selection 保持 head；其餘形成沒有保護根的不可變鏈 fixture。
        for (var i = 1; i < 4; i++)
            store.Scalar("UPDATE Revisions SET ParentRevisionId='" + revisions[i - 1].RevisionId.ToString("N") +
                "' WHERE RevisionId='" + revisions[i].RevisionId.ToString("N") + "';");
        var before = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        await Drain(repository, Expired(), 1);
        foreach (var revision in revisions.Take(4)) Assert.Null(await repository.ReadContentAsync(revision.ContentId, Token));
        Assert.Equal(before, await repository.ReadSessionAsync(store.Session.SessionId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FavoriteScopeChangesLeaveContextForBoundedGcRatherThanScanningOnWrite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        var query = new FavoriteQuery(Guid.NewGuid(), "讀者收藏", null, leaf.RevisionId, FavoriteQueryScope.Database,
            new QueryConnectionContext("BranchServer", "Library"));
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token);
        var favorite = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(favorite);
        await Drain(repository, Expired());
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contexts WHERE Server='BranchServer';"));
        await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query with { Scope = FavoriteQueryScope.Global, Connection = null }, favorite.Version), Token);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contexts WHERE Server='BranchServer';"));
        await Drain(repository, Expired());
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contexts WHERE Server='BranchServer';"));
        Assert.NotNull(await repository.ReadContentAsync(leaf.ContentId, Token));
    }

    [Fact]
    public async Task DraftCutoffIsExclusiveAndIndependentFromExecutionsWhileManualHistorySurvives()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(kind: QueryCaptureKind.ManualSnapshot), Token);
        var manual = (await repository.ReadSessionAsync(store.Session.SessionId, Token))?.LatestRevision;
        Assert.NotNull(manual);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;", QueryCaptureKind.DraftIdle, seconds: 600), Token);
        var auto = (await repository.ReadSessionAsync(store.Session.SessionId, Token))?.LatestRevision;
        Assert.NotNull(auto);
        await store.Process(repository, store.Capture(3, "SELECT * FROM Loan;", QueryCaptureKind.DraftIdle, seconds: 1200), Token);
        await store.Process(repository, store.Capture(4, "SELECT * FROM Loan;", selected: "SELECT * FROM LoanDetail;", seconds: 1201), Token);
        var boundary = new QueryMemoryMaintenancePolicy(SqliteTestStore.Start.AddSeconds(600), null, null);
        await Drain(repository, boundary);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM History WHERE EntryKey='r" + auto.RevisionId.ToString("N") + "';"));
        await Drain(repository, new QueryMemoryMaintenancePolicy(SqliteTestStore.Start.AddSeconds(601), null, null));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM History WHERE EntryKey='r" + auto.RevisionId.ToString("N") + "';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM History WHERE EntryKey='r" + manual.RevisionId.ToString("N") + "';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.NotNull(await repository.ReadContentAsync(auto.ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    private static async Task<Guid> SeedBaseRevision(SqliteTestStore store, SqliteQueryMemoryRepository repository)
    {
        await store.Process(repository, store.Capture(), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        return state.LatestRevision.RevisionId;
    }

    private static async Task<FavoriteQuery> SeedFavorite(SqliteQueryMemoryRepository repository, Guid revisionId, string name)
    {
        var query = new FavoriteQuery(Guid.NewGuid(), name, null, revisionId, FavoriteQueryScope.Global, null);
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(query), Token));
        return query;
    }

    private static string EditSql(string tag, int index) => "SELECT * FROM Loan WHERE Branch='" + tag + index + "';";

    private static async Task<List<Guid>> EditFavorite(SqliteQueryMemoryRepository repository, Guid favoriteQueryId,
        string tag, int count, int startSeconds, int stepSeconds = 60)
    {
        var revisions = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var favorite = await repository.ReadFavoriteQueryAsync(favoriteQueryId, Token);
            Assert.NotNull(favorite);
            var edit = new FavoriteQueryEdit(favoriteQueryId, favorite.Version, Guid.NewGuid(), EditSql(tag, index),
                SqliteTestStore.Start.AddSeconds(startSeconds + index * stepSeconds));
            Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.EditFavoriteQuerySqlAsync(edit, Token));
            revisions.Add(edit.RevisionId);
        }
        return revisions;
    }

    private static string Key(Guid id) => id.ToString("N");

    [Fact]
    public async Task FavoriteRevisionQuotaTrimsOldEditsButKeepsCurrentAndOtherFavoriteReferences()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var basis = await SeedBaseRevision(store, repository);
        var query = await SeedFavorite(repository, basis, "讀者收藏");
        var edits = await EditFavorite(repository, query.FavoriteQueryId, "A", 4, 60);
        // 另一個收藏指著中段版本；配額不得越過任何 Favorite 引用。
        await SeedFavorite(repository, edits[0], "讀者備份");
        var result = await Drain(repository, new QueryMemoryMaintenancePolicy(null, null, null, null, null, 2));
        Assert.Equal(new[] { edits[0], edits[2], edits[3] }.Select(Key).OrderBy(id => id, StringComparer.Ordinal),
            store.Query("SELECT RevisionId FROM Revisions WHERE FavoriteQueryId IS NOT NULL ORDER BY RevisionId;"));
        Assert.Null(await repository.ReadContentAsync(QueryContent.Create(EditSql("A", 1)).ContentId, Token));
        // 配額不碰擷取產生的版本，也不改變容量狀態的定義。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + Key(basis) + "';"));
        Assert.Equal(QueryMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
        Assert.Equal(store.Scalar("SELECT SUM(2 * Length) FROM Contents;"), result.Usage.ContentBytes);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FavoriteRevisionQuotaCountsEachQuerySeparatelyAndKeepsTiedTimestamps()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var basis = await SeedBaseRevision(store, repository);
        var tied = await SeedFavorite(repository, basis, "同刻收藏");
        await EditFavorite(repository, tied.FavoriteQueryId, "T", 3, 60, 0);
        var stepped = await SeedFavorite(repository, basis, "遞增收藏");
        var steps = await EditFavorite(repository, stepped.FavoriteQueryId, "S", 3, 600);
        await Drain(repository, new QueryMemoryMaintenancePolicy(null, null, null, null, null, 1));
        // 三個版本同一時間，第 1 新的界線不比任何列新，配額因此不刪任何一列。
        Assert.Equal(3L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteQueryId='" + Key(tied.FavoriteQueryId) + "';"));
        // 界線逐個收藏解析；快取不會把別的收藏算進同一份配額。
        Assert.Equal(new[] { Key(steps[2]) }, store.Query("SELECT RevisionId FROM Revisions WHERE FavoriteQueryId='" + Key(stepped.FavoriteQueryId) + "';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FavoriteEditsIgnoreDraftRetentionUntilTheFavoriteQueryIsDeleted()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var basis = await SeedBaseRevision(store, repository);
        var query = await SeedFavorite(repository, basis, "讀者收藏");
        await EditFavorite(repository, query.FavoriteQueryId, "D", 2, 60);
        // 收藏還在、沒有配額就是不限；草稿期限不回收它的 SQL 版本。
        await Drain(repository, Expired());
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteQueryId IS NOT NULL;"));
        var favorite = await repository.ReadFavoriteQueryAsync(query.FavoriteQueryId, Token);
        Assert.NotNull(favorite);
        Assert.Equal(FavoriteQueryWriteResult.Committed, await repository.DeleteFavoriteQueryAsync(query.FavoriteQueryId, favorite.Version, Token));
        // 收藏消失後標記成為孤立資料，改依草稿期限回收，不需要另開刪除路徑。
        await Drain(repository, Expired());
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteQueryId IS NOT NULL;"));
        Assert.Null(await repository.ReadContentAsync(QueryContent.Create(EditSql("D", 0)).ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }
}
