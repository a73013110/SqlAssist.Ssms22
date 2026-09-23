using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.SqlMemory.Sqlite.Tests.SqliteTestStore;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteUsageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SqlCapturePolicy DraftsOnly = new(true, false, TimeSpan.FromMinutes(10), false, true);
    private static readonly SqlConnectionLabel BranchA = new("BranchA", "Main");
    private static readonly SqlConnectionLabel BranchB = new("BranchB", "Archive");

    private static SqlFavorite NewFavorite() => new(Guid.NewGuid(), "館藏複本", null, Guid.NewGuid(), null, null);

    private static async Task SeedExecutions(SqliteTestStore store, SqliteTestRepository repository)
    {
        // 內容各不相同，連續相同的執行才不會合併成同一列 History。
        for (var i = 1; i <= 6; i++)
            await store.Process(repository, store.Capture(i, "SELECT * FROM Loan WHERE CopyNo=" + i + ";", seconds: i * 60,
                context: i % 3 == 0 ? BranchB : BranchA), Token);
    }

    private static async Task<Guid> CreateFavoriteWithEdits(SqliteTestRepository repository, int edits)
    {
        var favorite = NewFavorite();
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(
            new SqlFavoriteSave(favorite, null, Start, "SELECT CopyNo FROM Cat_BookCopy;"), Token));
        for (var i = 1; i <= edits; i++)
        {
            var item = await repository.ReadFavoriteAsync(favorite.FavoriteId, Token);
            Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(new SqlFavoriteSave(
                item!.Favorite with { CurrentRevisionId = Guid.NewGuid() }, item.Version, Start.AddMinutes(i),
                "SELECT CopyNo FROM Cat_BookCopy WHERE Branch=" + i + ";"), Token));
        }
        return favorite.FavoriteId;
    }

    [Fact]
    public async Task ReportCountsEveryCategoryFromOneSnapshot()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedExecutions(store, repository);
        var other = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        await store.Process(repository, store.Capture(1, "SELECT * FROM Branch;", SqlCaptureKind.DraftIdle, seconds: 900,
            session: other), Token, DraftsOnly);
        await CreateFavoriteWithEdits(repository, 3);
        await CreateFavoriteWithEdits(repository, 1);

        var report = await repository.ReadUsageReportAsync(Token);
        var counts = report.Counts;
        Assert.Equal(store.Scalar("SELECT count(*) FROM History WHERE Kind=1;"), counts.ExecutionEntries);
        Assert.Equal(6L, counts.ExecutionEvents);
        Assert.Equal(store.Scalar("SELECT count(*) FROM History WHERE Kind=2;"), counts.Drafts);
        // 執行的那個 Session 也有自己的回復內容。
        Assert.Equal(2L, counts.RecoveryItems);
        Assert.Equal(0L, counts.OpenRecoveryItems);
        Assert.Equal(2L, counts.Sessions);
        Assert.Equal(2L, counts.Favorites);
        Assert.Equal(6L, counts.FavoriteRevisions);
        Assert.Equal(4L, counts.LargestFavoriteRevisions);
        Assert.Equal(store.Scalar("SELECT count(*) FROM Contents;"), counts.Contents);
        Assert.Equal((await repository.ReadUsageAsync(Token)).ContentBytes, report.Usage.ContentBytes);

        // 伺服器分布依 History 列數排序（含最後一次執行留下的回復內容投影），沒有連線的草稿不計入。
        Assert.Equal(new[] { ("BranchA", 4L), ("BranchB", 3L) }, report.Servers.Select(share => (share.Name, share.Count)));
        Assert.Equal(Start.AddMinutes(1), report.OldestAt);
        Assert.Equal(Start.AddSeconds(900), report.NewestAt);
    }

    [Fact]
    public async Task EmptyDatabaseReportsZeroWithoutRange()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var report = await repository.ReadUsageReportAsync(Token);
        Assert.Equal(0L, report.Counts.ExecutionEntries);
        Assert.Equal(0L, report.Counts.LargestFavoriteRevisions);
        Assert.Empty(report.Servers);
        Assert.Null(report.OldestAt);
        Assert.Null(report.NewestAt);
    }

    [Fact]
    public async Task EstimateMatchesTheRowsCleanupDeletesAndFiltersAreExact()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedExecutions(store, repository);
        var request = new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Executions, Start.AddMinutes(5), "BranchA", "Main");

        var estimate = await repository.EstimateCleanupAsync(request, Token);
        // BranchA 是第 1、2、4、5 分鐘；早於第 5 分鐘的只有前三筆。
        Assert.Equal(new SqlMemoryCleanupEstimate(3, 0, 0, 0), estimate);

        long deleted = 0;
        string? cursor = null;
        do
        {
            // 每批只給一筆，驗證游標接續不重複也不遺漏。
            var batch = await repository.CleanupHistoryAsync(request, cursor, 1, Token);
            Assert.InRange(batch.Examined, 0, 1);
            deleted += batch.DeletedEntries;
            cursor = batch.Cursor;
        }
        while (cursor != null);

        Assert.Equal(estimate.ExecutionEntries, deleted);
        Assert.Equal(new SqlMemoryCleanupEstimate(0, 0, 0, 0), await repository.EstimateCleanupAsync(request, Token));
        Assert.Equal(3L, store.Scalar("SELECT count(*) FROM History WHERE Kind=1;"));
        Assert.Equal(3L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM History WHERE Kind=1 AND Server='BranchB';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
        // 釋出的內容同一交易回收，計量跟著下降。
        Assert.Equal(store.Scalar("SELECT coalesce(sum(2*Length),0) FROM Contents;"),
            (await repository.ReadUsageAsync(Token)).ContentBytes);
    }

    /// <summary>
    /// 只清空白 SQL：舊版本留下來的空白列清得掉，有內容的同一種列一列都不動。
    /// </summary>
    /// <remarks>
    /// 現在的擷取不再產生空白列（<c>SqlContent.IsBlank</c>），所以這裡的前置直接寫進
    /// 資料庫——那正是使用者升級之前累積下來的形狀。
    /// </remarks>
    [Fact]
    public async Task BlankOnlyCleanupRemovesEmptyRowsAndKeepsTheRest()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedExecutions(store, repository);

        // 一列全空、一列只有空白字元（UTF-16LE 的兩個半形空白）。
        store.Execute("INSERT INTO Contents(ContentId,SqlBytes,Length,Preview) VALUES('blank:empty',x'',0,'');");
        store.Execute("INSERT INTO Contents(ContentId,SqlBytes,Length,Preview) VALUES('blank:spaces',x'20002000',2,'  ');");
        store.Execute("UPDATE History SET ContentId='blank:empty' WHERE EntryKey=(SELECT EntryKey FROM History WHERE Kind=1 ORDER BY CreatedAt LIMIT 1);");
        store.Execute("UPDATE History SET ContentId='blank:spaces' WHERE EntryKey=" +
            "(SELECT EntryKey FROM History WHERE Kind=1 ORDER BY CreatedAt DESC LIMIT 1);");

        var request = new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Executions, onlyBlank: true);
        Assert.Equal(new SqlMemoryCleanupEstimate(2, 0, 0, 0), await repository.EstimateCleanupAsync(request, Token));

        long deleted = 0;
        string? cursor = null;
        do
        {
            var batch = await repository.CleanupHistoryAsync(request, cursor, 1, Token);
            deleted += batch.DeletedEntries;
            cursor = batch.Cursor;
        }
        while (cursor != null);

        Assert.Equal(2L, deleted);
        Assert.Equal(4L, store.Scalar("SELECT count(*) FROM History WHERE Kind=1;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contents WHERE ContentId LIKE 'blank:%';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));

        // 同樣的對象不加這個條件時，剩下的四列一列都不是空白的。
        Assert.Equal(new SqlMemoryCleanupEstimate(4, 0, 0, 0),
            await repository.EstimateCleanupAsync(new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Executions), Token));
        Assert.Equal(new SqlMemoryCleanupEstimate(0, 0, 0, 0), await repository.EstimateCleanupAsync(request, Token));
    }

    [Fact]
    public async Task CursorBelongsToTheRequestThatCreatedIt()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedExecutions(store, repository);
        var first = await repository.CleanupHistoryAsync(new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Executions), null, 1, Token);
        Assert.NotNull(first.Cursor);
        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.CleanupHistoryAsync(
            new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Executions, server: "BranchB"), first.Cursor, 1, Token));
        Assert.Equal(SqlMemoryStorageErrorKind.InvalidCursor, error.Kind);
    }

    [Fact]
    public async Task ClosedRecoveryCleanupLeavesSessionsThatStillHoldALease()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var lease = await repository.OpenLeaseAsync(new SqlMemoryLeaseOwner("LIBRARYPC", 4242, Start), Start, Token);
        var open = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        var closed = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        await store.Process(repository, store.Capture(1, "SELECT * FROM Lib_Reader;", SqlCaptureKind.DraftIdle, session: open),
            Token, DraftsOnly, lease);
        await store.Process(repository, store.Capture(1, "SELECT * FROM Lib_Tag;", SqlCaptureKind.DraftIdle, seconds: 5, session: closed),
            Token, DraftsOnly);
        var request = new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.ClosedRecovery);

        // 試算與清理用同一組候選條件；仍有租約的那一份兩邊都不算。
        Assert.Equal(new SqlMemoryCleanupEstimate(0, 0, 1, 0), await repository.EstimateCleanupAsync(request, Token));
        var result = await SqlMemoryCleanup.RunAsync(repository, request, null, Token);

        Assert.Equal(1L, result.DeletedEntries);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(open.SessionId.ToString("N"), store.Scalar("SELECT SessionId FROM Recovery;"));
        Assert.Equal(2 * "SELECT * FROM Lib_Tag;".Length, result.ReleasedContentBytes);
        Assert.Equal(1L, (await repository.ReadUsageReportAsync(Token)).Counts.OpenRecoveryItems);
    }

    [Fact]
    public async Task FavoriteRevisionCleanupKeepsTheNewestAndTheCurrentRevision()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favoriteId = await CreateFavoriteWithEdits(repository, 5);
        var current = (await repository.ReadFavoriteAsync(favoriteId, Token))!.Favorite.CurrentRevisionId;
        var request = new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.FavoriteRevisions, keepFavoriteRevisions: 2);

        Assert.Equal(4L, (await repository.EstimateCleanupAsync(request, Token)).FavoriteRevisions);
        var progress = new RecordingProgress();
        var result = await SqlMemoryCleanup.RunAsync(repository, request, progress, Token);

        Assert.Equal(0L, result.DeletedEntries);
        Assert.True(result.DeletedRows >= 4);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteId IS NOT NULL;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE RevisionId='" + current.ToString("N") + "';"));
        Assert.Equal(0L, (await repository.EstimateCleanupAsync(request, Token)).FavoriteRevisions);
    }

    [Fact]
    public async Task ManualMaintenanceDrainsTheWholeRoundAndReportsReleasedBytes()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedExecutions(store, repository);
        var before = await repository.ReadUsageAsync(Token);
        var result = await SqlMemoryCleanup.MaintainAsync(repository, Expired(), null, Token);
        Assert.True(result.DeletedRows > 0);
        Assert.Equal(before.ContentBytes - result.Usage.ContentBytes, result.ReleasedContentBytes);
        // 只剩保護根：Session 的文件與執行 head。
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Executions;"));
    }

    [Fact]
    public async Task BackupWritesAReadableCopyAndRefusesToOverwrite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedExecutions(store, repository);
        var target = Path.Combine(store.DirectoryPath, "backup.db");

        var length = await repository.BackupAsync(target, Token);
        Assert.Equal(new FileInfo(target).Length, length);
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            copy.Open();
            using var command = copy.CreateCommand();
            command.CommandText = "SELECT count(*) FROM History;";
            Assert.Equal(store.Scalar("SELECT count(*) FROM History;"), command.ExecuteScalar());
            command.CommandText = "PRAGMA application_id;";
            Assert.Equal(0x534d454dL, command.ExecuteScalar());
        }

        foreach (var path in new[] { target, store.Path, "relative.db" })
        {
            var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.BackupAsync(path, Token));
            Assert.Equal(SqlMemoryStorageErrorKind.InvalidArgument, error.Kind);
        }
    }

    [Fact]
    public void RequestRejectsEmptyTargetsAndKeepingNoFavoriteRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlMemoryCleanupRequest((SqlMemoryCleanupTargets)64));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.FavoriteRevisions, keepFavoriteRevisions: 0));
        var request = new SqlMemoryCleanupRequest(SqlMemoryCleanupTargets.Drafts, server: "  ", database: " Main ");
        Assert.Null(request.Server);
        Assert.Equal("Main", request.Database);
    }

    [Fact]
    public async Task ServerSharesGroupAlongTheIndexInsteadOfSortingHistory()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteUsageStore.ServerSharesSql;
        command.Parameters.AddWithValue("$limit", 5);
        using var reader = command.ExecuteReader();
        var plan = new System.Text.StringBuilder();
        while (reader.Read()) plan.AppendLine(reader.GetString(3));
        // 出現暫存 B-tree 分組代表計數要先把整張 History 依伺服器排序一次。
        Assert.Contains("IX_History_ServerTime", plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE FOR GROUP BY", plan.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingProgress : IProgress<long>
    {
        public System.Collections.Generic.List<long> Values { get; } = new();

        public void Report(long value) => Values.Add(value);
    }
}
