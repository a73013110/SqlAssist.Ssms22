using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;
using Xunit;
using static SqlAssist.SqlMemory.Sqlite.Tests.SqliteTestStore;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteMaintenanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<SqlRevision> SeedLeaf(SqliteTestStore store, SqliteTestRepository repository)
    {
        await store.Process(repository, store.Capture(selected: "SELECT * FROM Lib_Tag;",
            context: new SqlConnectionLabel("LibraryServer", "Library")), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestExecutionRevision);
        await store.Process(repository, store.Capture(2, selected: "SELECT * FROM Loan;", seconds: 1), Token);
        return state.LatestExecutionRevision;
    }

    private static async Task<List<SqlRevision>> SeedAutoDrafts(SqliteTestStore store, SqliteTestRepository repository,
        SqlSession? session, int count, int startSeconds)
    {
        var drafts = new List<SqlRevision>();
        for (var index = 0; index < count; index++)
        {
            await store.Process(repository, store.Capture(index + 1, "SELECT * FROM Loan WHERE Branch=" + (startSeconds + index) + ";",
                SqlCaptureKind.DraftIdle, seconds: startSeconds + 600 * index, session: session), Token);
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
        var result = await Drain(repository, new SqlRetentionPolicy(null, null, null, 2));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM History WHERE Kind=1;"));
        // 超額執行的選取版本一併回收，配額不會只留下無法回收的孤立版本。
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions WHERE IsExecutionSelection=1;"));
        Assert.Equal(store.Scalar("SELECT SUM(2 * Length) FROM Contents;"), result.Usage.ContentBytes);
        Assert.Equal(SqlMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ExecutionQuotaCountsEventsAndRecomputesTheMergedRow()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var sequence = 1; sequence <= 5; sequence++)
            await store.Process(repository, store.Capture(sequence, selected: "SELECT CopyNo FROM Cat_BookCopy;", seconds: sequence), Token);
        var merged = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        Assert.Equal(5, merged.ExecutionCount);

        // 配額算事件：留下最新兩次，列本身還在，次數與首次時間依剩下的執行重算，最後時間不變。
        await Drain(repository, new SqlRetentionPolicy(null, null, null, 2), budget: 1);
        var kept = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        Assert.Equal(merged.ItemId, kept.ItemId);
        Assert.Equal(2, kept.ExecutionCount);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(4), kept.FirstExecutedAt);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(5), kept.CreatedAt);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));

        // 最後一筆執行過期才刪投影；選取版本與內容跟著失去引用。
        await Drain(repository, Expired());
        Assert.Empty((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Executions;"));
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
        await Reference(repository, Guid.ParseExact((string)oldest!, "N"));
        // 四次執行共用同一個時間，第 1 新的界線不比任何列新，配額因此不刪任何一列。
        await Drain(repository, new SqlRetentionPolicy(null, null, null, 1));
        Assert.Equal(4L, store.Scalar("SELECT count(*) FROM Executions;"));
        // 有截止時間時 Favorite 引用的執行仍受保護，配額不會越過保護根。
        await Drain(repository, new SqlRetentionPolicy(null, SqliteTestStore.Start.AddDays(1), null, 1));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(oldest, store.Scalar("SELECT RevisionId FROM Executions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task AutoRevisionQuotaTrimsEachSessionDraftListWithoutCollapsingProtectedChains()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var second = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        var first = await SeedAutoDrafts(store, repository, null, 3, 0);
        var other = await SeedAutoDrafts(store, repository, second, 2, 1800);
        var revisions = store.Scalar("SELECT count(*) FROM Revisions;");
        // 只設每 Session 配額：較舊 Session 的最新草稿不因另一個 Session 更新而被淘汰。
        await Drain(repository, new SqlRetentionPolicy(null, null, null, null, 1));
        foreach (var (session, kept) in new[] { (store.Session.SessionId, new[] { first[2] }), (second.SessionId, new[] { other[1] }) })
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
        Assert.Equal(0, (int)SqlRevisionReason.AutoCheckpoint);
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
        Assert.Equal(SqlMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
        Assert.Equal(store.Scalar("SELECT SUM(2 * Length) FROM Contents;"), result.Usage.ContentBytes);
        var replay = store.Capture(3, selected: "SELECT * FROM Loan;", seconds: 2);
        var write = new SqlCapturePlanner().Prepare(replay, before, SqliteTestStore.Policy);
        Assert.NotNull(write);
        await repository.CommitAsync(write, null, Token);
        await Drain(repository, Expired());
        Assert.Equal(SqlHistoryCommitResult.AlreadyCommitted, await repository.CommitAsync(write, null, Token));
    }

    [Theory]
    [InlineData("favorite")]
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
                await Reference(repository, leaf.RevisionId);
                break;
            case "parent":
                store.Scalar("UPDATE Revisions SET ParentRevisionId='" + id + "' WHERE RevisionId=(SELECT LatestExecutionRevisionId FROM Sessions);");
                break;
        }
        var result = await Drain(repository, Expired(0), 1);
        Assert.NotNull(await repository.ReadContentAsync(leaf.ContentId, Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + id + "';"));
        Assert.Equal(SqlMemoryCapacityStatus.CannotReclaimWithinPolicy, result.CapacityStatus);
        Assert.True(result.Usage.ContentBytes > 0);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task RecoveryIsNotExpiredByAgeAndReplacedContentUpdatesUsage()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var policy = new SqlCapturePolicy(true, false, TimeSpan.FromMinutes(10), false, true);
        await store.Process(repository, store.Capture(kind: SqlCaptureKind.DraftIdle), Token, policy);
        var usage = await repository.ReadUsageAsync(Token);
        Assert.Equal(2 * "SELECT * FROM Lib_Reader;".Length, usage.ContentBytes);
        var result = await Drain(repository, Expired(0));
        Assert.Equal(usage.ContentBytes, result.Usage.ContentBytes);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM History;"));
        await store.Process(repository, store.Capture(2, "SELECT * FROM Loan;", SqlCaptureKind.DraftIdle), Token, policy);
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
        var policy = new SqlRetentionPolicy(enabled ? SqliteTestStore.Start : null,
            enabled ? SqliteTestStore.Start : null, 0);
        var result = await Drain(repository, policy);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(SqlMemoryCapacityStatus.CannotReclaimWithinPolicy, result.CapacityStatus);
    }

    [Fact]
    public async Task CursorSurvivesReopenAndRejectsDifferentStorePolicyAndMalformedInput()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedLeaf(store, repository);
        var policy = Expired();
        var first = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 1), Token);
        Assert.NotNull(first.Cursor);
        using var other = new SqliteTestStore();
        var otherRepository = await other.Open(Token);
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => otherRepository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 1, first.Cursor), Token));
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(0), 1, first.Cursor), Token));
        var quota = new SqlRetentionPolicy(policy.DraftBefore, policy.ExecutionBefore, null, 10, 50);
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.MaintainAsync(new SqlMemoryMaintenanceRequest(quota, 1, first.Cursor), Token));
        foreach (var cursor in new[] { "!", Convert.ToBase64String(new byte[20]), new string('a', 1025) })
            await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 1, cursor), Token));
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
        await Assert.ThrowsAsync<SqliteException>(() => repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 500), Token));
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
        var first = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 1), Token);
        var count = store.Scalar("SELECT count(*) FROM Executions;");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cancellation = new CancellationTokenSource();
        var pending = repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 1, first.Cursor), cancellation.Token);
        cancellation.Cancel();
        transaction.Rollback();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(count, store.Scalar("SELECT count(*) FROM Executions;"));
        await Drain(await store.Open(Token), Expired(), 1, first.Cursor);
    }

    [Fact]
    public async Task FavoriteUpdateRacingMaintenanceNeverLeavesDanglingReferenceOrPartialWrite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestExecutionRevision);
        var favorite = await Reference(repository, state.LatestExecutionRevision.RevisionId);
        var original = await repository.ReadFavoriteAsync(favorite.FavoriteId, Token);
        Assert.NotNull(original);
        var other = await store.Open(Token);
        var changed = favorite with { CurrentRevisionId = leaf.RevisionId, Server = "BranchServer", Database = "Library" };
        var update = Record.ExceptionAsync(async () => Assert.Equal(SqlFavoriteWriteResult.Committed,
            await other.SaveFavoriteAsync(new SqlFavoriteSave(changed, original.Version, SqliteTestStore.Start.AddHours(1)), Token)));
        await Drain(repository, Expired(), 500);
        var error = await update;
        if (error != null) Assert.Equal(19, Assert.IsType<SqliteException>(error).SqliteErrorCode);
        var current = await other.ReadFavoriteAsync(favorite.FavoriteId, Token);
        Assert.NotNull(current);
        Assert.Equal(error == null ? changed : favorite, current.Favorite);
        Assert.NotNull(await other.ReadContentAsync(current.ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FavoriteReferenceAddedBetweenBatchesProtectsAlreadyVisitedCandidate()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);
        // 第一批只處理兩個 Execution；下一批必須重新檢查新加入的 Favorite 根。
        var first = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 2), Token);
        Assert.NotNull(first.Cursor);
        await Reference(repository, leaf.RevisionId);
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
        using var isolated = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, Token);
        var before = await isolated.ReadUsageAsync(Token);
        Assert.True(before.DatabaseFileBytes > 0);
        Assert.True(before.WalFileBytes >= 0);
        var after = await Drain(isolated, Expired(0), 1);
        Assert.True(after.Usage.ContentBytes < before.ContentBytes);
        Assert.Null(await isolated.ReadContentAsync(leaf.ContentId, Token));
        Assert.Equal(SqlMemoryCapacityStatus.CannotReclaimWithinPolicy, after.CapacityStatus);
        // 輪次、Claim 與狀態都要能跨 AppDomain 序列化；租約交回也走同一個邊界。
        var owner = new SqlMemoryLeaseOwner("LIBRARYPC", 4242, SqliteTestStore.Start);
        Assert.True(await isolated.TryAcquireMaintenanceLeaseAsync(owner, SqliteTestStore.Start, SqliteTestStore.Start, Token));
        var round = new SqlMemoryMaintenanceRound("retention1|isolated", SqliteTestStore.Start, 0, true, SqlMemoryMaintenanceScan.Full, 0);
        var claimed = await isolated.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(0), 500, null,
            SqlMemoryMaintenanceScan.Full, new SqlMemoryMaintenanceClaim(owner, 0, round)), Token);
        Assert.Equal(new SqlMemoryMaintenanceState(1, round, claimed.Cursor, claimed.RequiresAnotherPass, claimed.CapacityStatus),
            await isolated.ReadMaintenanceStateAsync(Token));
        Assert.True(await isolated.ReleaseMaintenanceLeaseAsync(owner, Token));
        Assert.False(await isolated.ReleaseMaintenanceLeaseAsync(owner, Token));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => isolated.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 1), cancellation.Token));
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
            ("Executions", "RevisionId"), ("History", "RevisionId"), ("Favorites", "CurrentRevisionId"),
        };
        probes.AddRange(new[] { "Revisions", "Recovery", "History" }.Select(table => (table, "ContentId")));
        foreach (var probe in probes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT 1 FROM " + probe.Table + " WHERE " + probe.Column + "='test';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Contains("SEARCH", reader.GetString(3));
            Assert.Contains("INDEX", reader.GetString(3));
        }
        foreach (var stage in new[] { SqliteMaintenanceStage.Revisions, SqliteMaintenanceStage.Contents })
        {
            var plan = Explain(connection, SqliteMaintenanceStages.ByKey(stage));
            Assert.Contains("SEARCH", Assert.Single(plan));
        }
    }

    /// <summary>
    /// 索引巡查的每個候選與分組查詢都必須是索引範圍搜尋：不掃整張表、不建暫存排序，
    /// 部分索引的條件也要與查詢逐字相同才會命中。
    /// </summary>
    [Fact]
    public async Task IndexedCandidateQueriesSeekTheirTimeIndexesWithoutScanningOrSorting()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        foreach (var (sql, index) in SqliteMaintenanceStages.CandidateQueries())
        {
            var detail = Assert.Single(Explain(connection, sql));
            Assert.StartsWith("SEARCH", detail);
            Assert.Contains(index, detail);
            Assert.DoesNotContain("TEMP B-TREE", detail);
            // 截止時間必須是索引範圍的上界，而不是讀完範圍再逐列過濾。
            if (sql.Contains("$cutoff")) Assert.Contains(">(?,?) AND ", detail);
            if (sql.Contains("$cutoff")) Assert.EndsWith("<?)", detail);
        }
    }

    private static List<string> Explain(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        return details;
    }

    /// <summary>
    /// 期限內與配額內的資料再多，索引巡查一輪也只花分組探測的工作量；舊的逐主鍵巡查要逐列看過一遍。
    /// </summary>
    [Fact]
    public async Task UnexpiredBacklogIsNeverExaminedByAnIndexedRound()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var sequence = 1; sequence <= 40; sequence++)
            await store.Process(repository, store.Capture(sequence,
                selected: "SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo=" + sequence + ";", seconds: sequence), Token);
        var other = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        await SeedAutoDrafts(store, repository, other, 12, 0);
        var policy = new SqlRetentionPolicy(SqliteTestStore.Start.AddDays(-1), SqliteTestStore.Start.AddDays(-1), 0,
            100, 50, 20, SqliteTestStore.Start.AddDays(-1));

        // 工作量只給 2：一輪仍在一批內結束，代表 40 次執行、它們的選取版本與草稿都不是候選。
        var indexed = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 2), Token);
        Assert.Null(indexed.Cursor);
        Assert.Equal(1, indexed.ExaminedCandidates);
        Assert.Equal(0, indexed.DeletedRows);
        Assert.Equal(SqlMemoryCapacityStatus.CannotReclaimWithinPolicy, indexed.CapacityStatus);

        var full = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 500, null, SqlMemoryMaintenanceScan.Full), Token);
        Assert.True(full.ExaminedCandidates > 80);
        Assert.Equal(40L, store.Scalar("SELECT count(*) FROM Executions;"));
    }

    /// <summary>刪掉版本的同一批就回收它的內容，不必等排在最後、而且很少跑到的內容階段。</summary>
    [Fact]
    public async Task DeletingARevisionReclaimsItsContentInTheSameBatch()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var basis = await SeedBaseRevision(store, repository);
        var query = await SeedFavorite(repository, basis, "讀者收藏");
        await EditFavorite(repository, query.FavoriteId, "B", 3, 60);
        var before = await repository.ReadUsageAsync(Token);

        var result = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(
            new SqlRetentionPolicy(null, null, null, null, null, 1), 500), Token);

        Assert.Null(result.Cursor);
        // 一個收藏的分組探測，加上兩個超額版本。
        Assert.Equal(3, result.ExaminedCandidates);
        Assert.Equal(4, result.DeletedRows);
        Assert.Null(await repository.ReadContentAsync(SqlContent.Create(EditSql("B", 0)).ContentId, Token));
        Assert.Null(await repository.ReadContentAsync(SqlContent.Create(EditSql("B", 1)).ContentId, Token));
        Assert.Equal(before.ContentBytes - 2L * (EditSql("B", 0).Length + EditSql("B", 1).Length), result.Usage.ContentBytes);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    /// <summary>過期的執行釋出選取版本，版本階段刪掉之後內容也在同一批回收。</summary>
    [Fact]
    public async Task ExpiredExecutionsReleaseSelectionRevisionsAndContentWithinOneBatch()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var leaf = await SeedLeaf(store, repository);

        var result = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 500), Token);

        Assert.Null(result.Cursor);
        Assert.Null(await repository.ReadContentAsync(leaf.ContentId, Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + leaf.RevisionId.ToString("N") + "';"));
        Assert.Equal(store.Scalar("SELECT SUM(2 * Length) FROM Contents;"), result.Usage.ContentBytes);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    private static readonly SqlMemoryLeaseOwner MaintenanceOwner = new("LIBRARYPC", 4242, SqliteTestStore.Start);

    /// <summary>
    /// 輪次、游標與政策指紋寫在狀態表：重開 repository 後照樣接續，
    /// 而晚到的批次（版本過期或租約易手）整批回復、不蓋掉別人推進過的游標。
    /// </summary>
    [Fact]
    public async Task PersistedStateLetsAReopenedRepositoryResumeAndRejectsStaleOrForeignBatches()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var sequence = 1; sequence <= 6; sequence++)
            await store.Process(repository, store.Capture(sequence,
                selected: "SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo=" + sequence + ";", seconds: sequence), Token);
        Assert.Null(await repository.ReadMaintenanceStateAsync(Token));
        Assert.True(await repository.TryAcquireMaintenanceLeaseAsync(MaintenanceOwner, SqliteTestStore.Start, SqliteTestStore.Start, Token));
        var policy = Expired();
        var round = new SqlMemoryMaintenanceRound("retention1|test", SqliteTestStore.Start, 1, false, SqlMemoryMaintenanceScan.Indexed, 3);
        SqlMemoryMaintenanceRequest Claimed(long version, string? cursor, SqlMemoryLeaseOwner? owner = null) =>
            new(policy, 2, cursor, SqlMemoryMaintenanceScan.Indexed, new SqlMemoryMaintenanceClaim(owner ?? MaintenanceOwner, version, round));

        var first = await repository.MaintainAsync(Claimed(0, null), Token);
        Assert.NotNull(first.Cursor);
        var reopened = await store.Open(Token);
        var state = await reopened.ReadMaintenanceStateAsync(Token);
        Assert.Equal(new SqlMemoryMaintenanceState(1, round, first.Cursor, false, first.CapacityStatus), state);

        var executions = store.Scalar("SELECT count(*) FROM Executions;");
        foreach (var stale in new[] { Claimed(0, first.Cursor), Claimed(1, first.Cursor, MaintenanceOwner with { ProcessId = 77 }) })
        {
            var conflict = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => reopened.MaintainAsync(stale, Token));
            Assert.Equal(SqlMemoryStorageErrorKind.Conflict, conflict.Kind);
        }
        Assert.Equal(executions, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(state, await reopened.ReadMaintenanceStateAsync(Token));

        var result = first;
        for (var version = 1L; result.Cursor != null || result.RequiresAnotherPass; version++)
        {
            Assert.True(version < 50);
            var resumed = (await reopened.ReadMaintenanceStateAsync(Token))!;
            Assert.Equal(version, resumed.Version);
            result = await reopened.MaintainAsync(Claimed(version, resumed.Cursor), Token);
        }
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Null((await reopened.ReadMaintenanceStateAsync(Token))?.Cursor);

        // 交回租約之後，原持有者的批次也不再能寫回。
        Assert.True(await reopened.ReleaseMaintenanceLeaseAsync(MaintenanceOwner, Token));
        var version2 = (await reopened.ReadMaintenanceStateAsync(Token))!.Version;
        Assert.Equal(SqlMemoryStorageErrorKind.Conflict, (await Assert.ThrowsAsync<SqlMemoryStorageException>(() =>
            reopened.MaintainAsync(Claimed(version2, null), Token))).Kind);
        Assert.Throws<ArgumentException>(() => new SqlMemoryMaintenanceRequest(policy, 2, null, SqlMemoryMaintenanceScan.Full,
            new SqlMemoryMaintenanceClaim(MaintenanceOwner, 0, round)));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FullScanCursorIsNotAcceptedByAnIndexedBatch()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedLeaf(store, repository);
        var full = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 1, null, SqlMemoryMaintenanceScan.Full), Token);
        Assert.NotNull(full.Cursor);
        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() =>
            repository.MaintainAsync(new SqlMemoryMaintenanceRequest(Expired(), 1, full.Cursor), Token));
        Assert.Equal(SqlMemoryStorageErrorKind.InvalidCursor, error.Kind);
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
            var content = SqlContent.Create("SELECT CopyNo FROM Cat_BookCopy WHERE CopyNo=" + i + ";");
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Contents VALUES($id,$bytes,$length,$preview);";
            insert.Parameters.AddWithValue("$id", content.ContentId);
            insert.Parameters.AddWithValue("$bytes", Encoding.Unicode.GetBytes(content.SqlText));
            insert.Parameters.AddWithValue("$length", content.Length);
            insert.Parameters.AddWithValue("$preview", content.SqlText);
            insert.ExecuteNonQuery();
            bytes += 2 * content.Length;
        }
        Assert.Equal(bytes, (await repository.ReadUsageAsync(Token)).ContentBytes);
        store.Scalar(@"CREATE TRIGGER FailSecondContent BEFORE DELETE ON Contents
 WHEN (SELECT count(*) FROM Contents)<17 BEGIN SELECT RAISE(ABORT,'測試整批回復'); END;");
        var policy = new SqlRetentionPolicy(null, null, 0);
        // 沒有任何列被刪，就沒有引用可以帶出這些孤立內容；索引巡查不為它們掃全表。
        var indexed = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 3), Token);
        Assert.Null(indexed.Cursor);
        Assert.Equal(0, indexed.ExaminedCandidates);
        Assert.Equal(SqlMemoryCapacityStatus.CannotReclaimWithinPolicy, indexed.CapacityStatus);
        var full = SqlMemoryMaintenanceScan.Full;
        await Assert.ThrowsAsync<SqliteException>(() => repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 3, null, full), Token));
        Assert.Equal(17L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(bytes, (await repository.ReadUsageAsync(Token)).ContentBytes);
        store.Scalar("DROP TRIGGER FailSecondContent;");
        var first = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, 3, null, full), Token);
        Assert.Equal(3, first.ExaminedCandidates);
        Assert.Equal(3, first.DeletedRows);
        Assert.Equal(14L, store.Scalar("SELECT count(*) FROM Contents;"));
        var result = await Drain(await store.Open(Token), policy, 2, first.Cursor, full);
        Assert.Equal(0, result.Usage.ContentBytes);
        Assert.Equal(SqlMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task MultiplePassesCollectUnrootedParentChainWithoutRewritingSurvivors()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var revisions = new List<SqlRevision>();
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
    public async Task DraftCutoffIsExclusiveAndIndependentFromExecutions()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(kind: SqlCaptureKind.DraftIdle), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;", SqlCaptureKind.DraftIdle, seconds: 600), Token);
        var auto = (await repository.ReadSessionAsync(store.Session.SessionId, Token))?.LatestRevision;
        Assert.NotNull(auto);
        await store.Process(repository, store.Capture(3, "SELECT * FROM Loan;", SqlCaptureKind.DraftIdle, seconds: 1200), Token);
        await store.Process(repository, store.Capture(4, "SELECT * FROM Loan;", selected: "SELECT * FROM LoanDetail;", seconds: 1201), Token);
        var boundary = new SqlRetentionPolicy(SqliteTestStore.Start.AddSeconds(600), null, null);
        await Drain(repository, boundary);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM History WHERE EntryKey='r" + auto.RevisionId.ToString("N") + "';"));
        await Drain(repository, new SqlRetentionPolicy(SqliteTestStore.Start.AddSeconds(601), null, null));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM History WHERE EntryKey='r" + auto.RevisionId.ToString("N") + "';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.NotNull(await repository.ReadContentAsync(auto.ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    private static async Task<Guid> SeedBaseRevision(SqliteTestStore store, SqliteTestRepository repository)
    {
        await store.Process(repository, store.Capture(), Token);
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        return state.LatestRevision.RevisionId;
    }

    private static async Task<SqlFavorite> SeedFavorite(SqliteTestRepository repository, Guid revisionId, string name)
    {
        var query = new SqlFavorite(Guid.NewGuid(), name, null, revisionId, null, null);
        Assert.Equal(SqlFavoriteWriteResult.Committed,
            await repository.SaveFavoriteAsync(new SqlFavoriteSave(query, null, SqliteTestStore.Start), Token));
        return query;
    }

    /// <summary>以收藏引用一份既有版本，讓它成為保護根。</summary>
    private static Task<SqlFavorite> Reference(SqliteTestRepository repository, Guid revisionId) =>
        SeedFavorite(repository, revisionId, "讀者收藏");

    private static string EditSql(string tag, int index) => "SELECT * FROM Loan WHERE Branch='" + tag + index + "';";

    private static async Task<List<Guid>> EditFavorite(SqliteTestRepository repository, Guid favoriteId,
        string tag, int count, int startSeconds, int stepSeconds = 60)
    {
        var revisions = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var favorite = await repository.ReadFavoriteAsync(favoriteId, Token);
            Assert.NotNull(favorite);
            var revision = Guid.NewGuid();
            Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(new SqlFavoriteSave(
                favorite.Favorite with { CurrentRevisionId = revision }, favorite.Version,
                SqliteTestStore.Start.AddSeconds(startSeconds + index * stepSeconds), EditSql(tag, index)), Token));
            revisions.Add(revision);
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
        var edits = await EditFavorite(repository, query.FavoriteId, "A", 4, 60);
        // 另一個收藏指著中段版本；配額不得越過任何 Favorite 引用。
        await SeedFavorite(repository, edits[0], "讀者備份");
        var result = await Drain(repository, new SqlRetentionPolicy(null, null, null, null, null, 2));
        Assert.Equal(new[] { edits[0], edits[2], edits[3] }.Select(Key).OrderBy(id => id, StringComparer.Ordinal),
            store.Query("SELECT RevisionId FROM Revisions WHERE FavoriteId IS NOT NULL ORDER BY RevisionId;"));
        Assert.Null(await repository.ReadContentAsync(SqlContent.Create(EditSql("A", 1)).ContentId, Token));
        // 配額不碰擷取產生的版本，也不改變容量狀態的定義。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + Key(basis) + "';"));
        Assert.Equal(SqlMemoryCapacityStatus.WithinLimit, result.CapacityStatus);
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
        await EditFavorite(repository, tied.FavoriteId, "T", 3, 60, 0);
        var stepped = await SeedFavorite(repository, basis, "遞增收藏");
        var steps = await EditFavorite(repository, stepped.FavoriteId, "S", 3, 600);
        await Drain(repository, new SqlRetentionPolicy(null, null, null, null, null, 1));
        // 三個版本同一時間，第 1 新的界線不比任何列新，配額因此不刪任何一列。
        Assert.Equal(3L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteId='" + Key(tied.FavoriteId) + "';"));
        // 界線逐個收藏解析；快取不會把別的收藏算進同一份配額。
        Assert.Equal(new[] { Key(steps[2]) }, store.Query("SELECT RevisionId FROM Revisions WHERE FavoriteId='" + Key(stepped.FavoriteId) + "';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FavoriteEditsIgnoreDraftRetentionUntilTheFavoriteIsDeleted()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var basis = await SeedBaseRevision(store, repository);
        var query = await SeedFavorite(repository, basis, "讀者收藏");
        await EditFavorite(repository, query.FavoriteId, "D", 2, 60);
        // 收藏還在、沒有配額就是不限；草稿期限不回收它的 SQL 版本。
        await Drain(repository, Expired());
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteId IS NOT NULL;"));
        var favorite = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.DeleteFavoriteAsync(query.FavoriteId, favorite.Version, Token));
        // 收藏消失後標記成為孤立資料，改依草稿期限回收，不需要另開刪除路徑。
        await Drain(repository, Expired());
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteId IS NOT NULL;"));
        Assert.Null(await repository.ReadContentAsync(SqlContent.Create(EditSql("D", 0)).ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FavoritesCreatedFromSqlSurviveWithoutASessionAndAreReclaimedOnceRemoved()
    {
        const string sql = "SELECT CopyNo FROM Cat_BookCopy;";
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var query = new SqlFavorite(Guid.NewGuid(), "館藏複本", null, Guid.NewGuid(), null, null);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(
            new SqlFavoriteSave(query, null, SqliteTestStore.Start, sql), Token));
        // 沒有 Session 可以算配額界線，維護仍要走得完；收藏還在就一列都不刪。
        await Drain(repository, Expired());
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE SessionId IS NULL;"));
        Assert.NotNull(await repository.ReadContentAsync(SqlContent.Create(sql).ContentId, Token));
        var favorite = await repository.ReadFavoriteAsync(query.FavoriteId, Token);
        Assert.NotNull(favorite);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.DeleteFavoriteAsync(query.FavoriteId, favorite.Version, Token));
        await Drain(repository, Expired());
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.Null(await repository.ReadContentAsync(SqlContent.Create(sql).ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }
}
