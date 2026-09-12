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

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteMaintenanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static QueryMemoryMaintenancePolicy Expired(long? capacity = null) =>
        new(SqliteTestStore.Start.AddDays(1), SqliteTestStore.Start.AddDays(1), capacity);

    private static async Task<QueryRevision> SeedLeaf(SqliteTestStore store, SqliteQueryMemoryRepository repository)
    {
        await store.Process(repository, store.Capture(selected: "SELECT * FROM Lib_Tag;",
            context: new QueryConnectionContext("LibraryServer", "Library")), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestExecutionRevision);
        await store.Process(repository, store.Capture(2, selected: "SELECT * FROM Loan;", seconds: 1), Token);
        return state.LatestExecutionRevision;
    }

    private static async Task<QueryMemoryMaintenanceResult> Drain(IQueryMemoryMaintenanceRepository repository,
        QueryMemoryMaintenancePolicy policy, int budget = 2, string? cursor = null)
    {
        for (var batch = 0; batch < 200; batch++)
        {
            var result = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, budget, cursor), Token);
            Assert.InRange(result.ExaminedCandidates, 0, budget);
            Assert.InRange(result.DeletedRows, 0, 2 * result.ExaminedCandidates);
            if (result.Cursor == null && !result.RequiresAnotherPass) return result;
            cursor = result.Cursor;
        }
        throw new InvalidOperationException("維護未在測試上限內收斂。");
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
        await repository.CommitAsync(write, Token);
        await Drain(repository, Expired());
        Assert.Equal(QueryMemoryCommitResult.AlreadyCommitted, await repository.CommitAsync(write, Token));
    }

    [Theory]
    [InlineData("saved")]
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
            case "saved":
                await repository.WriteSavedQueryAsync(new SavedQueryWrite(new SavedQuery(Guid.NewGuid(), "讀者收藏", null,
                    leaf.RevisionId, SavedQueryScope.Global, null, false)), Token);
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
        await Assert.ThrowsAsync<ArgumentException>(() => otherRepository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 1, first.Cursor), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(0), 1, first.Cursor), Token));
        foreach (var cursor in new[] { "!", Convert.ToBase64String(new byte[20]), new string('a', 1025) })
            await Assert.ThrowsAsync<ArgumentException>(() => repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 1, cursor), Token));
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
    public async Task SavedUpdateRacingMaintenanceNeverLeavesDanglingReferenceOrPartialContext()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestExecutionRevision);
        var saved = new SavedQuery(Guid.NewGuid(), "讀者收藏", null, state.LatestExecutionRevision.RevisionId, SavedQueryScope.Global, null, false);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(saved), Token);
        var original = await repository.ReadSavedQueryAsync(saved.SavedQueryId, Token);
        Assert.NotNull(original);
        var other = await store.Open(Token);
        var changed = saved with { CurrentRevisionId = leaf.RevisionId, Scope = SavedQueryScope.Database,
            Connection = new QueryConnectionContext("BranchServer", "Library") };
        var update = Record.ExceptionAsync(async () =>
            Assert.Equal(SavedQueryWriteResult.Committed, await other.WriteSavedQueryAsync(new SavedQueryWrite(changed, original.Version), Token)));
        await Drain(repository, Expired(), 500);
        var error = await update;
        if (error != null) Assert.Equal(19, Assert.IsType<SqliteException>(error).SqliteErrorCode);
        var current = await other.ReadSavedQueryAsync(saved.SavedQueryId, Token);
        Assert.NotNull(current);
        Assert.Equal(error == null ? changed : saved, current.Query);
        Assert.NotNull(await other.ReadContentAsync(current.ContentId, Token));
        Assert.Equal(error == null ? 1L : 0L, store.Scalar("SELECT count(*) FROM Contexts WHERE Server='BranchServer';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task SavedReferenceAddedBetweenBatchesProtectsAlreadyVisitedCandidate()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        // 第一批只處理兩個 Execution；下一批必須重新檢查新加入的 Saved 根。
        var first = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(Expired(), 2), Token);
        Assert.NotNull(first.Cursor);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(new SavedQuery(Guid.NewGuid(), "讀者收藏", null,
            leaf.RevisionId, SavedQueryScope.Global, null, false)), Token);
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
            ("Executions", "RevisionId"), ("History", "RevisionId"), ("SavedQueries", "CurrentRevisionId"),
        };
        probes.AddRange(new[] { "Revisions", "Executions", "Recovery", "History", "SavedQueries" }.Select(table => (table, "ContextId")));
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
    public async Task SavedScopeChangesLeaveContextForBoundedGcRatherThanScanningOnWrite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        var query = new SavedQuery(Guid.NewGuid(), "讀者收藏", null, leaf.RevisionId, SavedQueryScope.Database,
            new QueryConnectionContext("BranchServer", "Library"), false);
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token);
        var saved = await repository.ReadSavedQueryAsync(query.SavedQueryId, Token);
        Assert.NotNull(saved);
        await Drain(repository, Expired());
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contexts WHERE Server='BranchServer';"));
        await repository.WriteSavedQueryAsync(new SavedQueryWrite(query with { Scope = SavedQueryScope.Global, Connection = null }, saved.Version), Token);
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
}
