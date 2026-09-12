using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteMigrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string StoreId = "11111111111111111111111111111111";
    private const string RevisionId = "22222222222222222222222222222222";
    private const string Sql = "SELECT * FROM Lib_Reader;";

    private static void CreateV1(SqliteTestStore store)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        using var stream = typeof(SqliteMigrationTests).Assembly.GetManifestResourceStream("SqlAssist.QueryMemory.Sqlite.Tests.QueryMemorySchemaV1.sql")
            ?? throw new InvalidOperationException("缺少固定 v1 fixture。");
        using var text = new StreamReader(stream);
        store.Scalar(text.ReadToEnd());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"PRAGMA application_id=1397836877; PRAGMA user_version=1;
INSERT INTO StoreInfo VALUES($store);
INSERT INTO Documents VALUES($document,'Library.sql',NULL);
INSERT INTO Contents VALUES($content,$hash,$bytes,$length,$preview);
INSERT INTO Sessions VALUES($session,$document,$time,NULL,1,1,$revision,NULL);
INSERT INTO Revisions VALUES($revision,NULL,$content,$session,$time,3,NULL,0);
INSERT INTO Recovery VALUES($session,$content,1,$time,NULL);
INSERT INTO Executions VALUES($execution,$revision,$time,NULL,0,1,NULL);
INSERT INTO Captures VALUES($capture,$session,1);
INSERT INTO History VALUES($history,$session,$revision,$content,$time,2,NULL,NULL,NULL,1);";
        var content = QueryContent.Create(Sql);
        command.Parameters.AddWithValue("$store", StoreId);
        command.Parameters.AddWithValue("$document", store.Document.DocumentId.ToString("N"));
        command.Parameters.AddWithValue("$session", store.Session.SessionId.ToString("N"));
        command.Parameters.AddWithValue("$revision", RevisionId);
        command.Parameters.AddWithValue("$content", content.ContentId);
        command.Parameters.AddWithValue("$hash", content.ContentHash);
        command.Parameters.AddWithValue("$bytes", Encoding.Unicode.GetBytes(Sql));
        command.Parameters.AddWithValue("$length", Sql.Length);
        command.Parameters.AddWithValue("$preview", Sql);
        command.Parameters.AddWithValue("$time", SqliteTestStore.Start.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("$execution", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$capture", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$history", "r" + RevisionId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    [Fact]
    public async Task PopulatedV1MigratesOnceUnderConcurrentOpenWithoutLosingAnyRoots()
    {
        using var store = new SqliteTestStore();
        CreateV1(store);
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(StoreId, store.Scalar("SELECT StoreId FROM StoreInfo;"));
        foreach (var table in new[] { "Documents", "Sessions", "Contents", "Revisions", "Recovery", "Executions", "Captures", "History" })
            Assert.Equal(1L, store.Scalar("SELECT count(*) FROM " + table + ";"));
        var repository = repositories[0];
        var state = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(state?.LatestRevision);
        Assert.Equal(Guid.ParseExact(RevisionId, "N"), state.LatestRevision.RevisionId);
        Assert.Equal(QueryRevisionReason.ManualSnapshot, state.LatestRevision.Reason);
        Assert.Equal(Sql, (await repository.ReadContentAsync(state.LatestRevision.ContentId, Token))?.SqlText);
        Assert.True(Assert.Single((await repository.ReadHistoryAsync(new QueryHistoryRequest(10, QueryHistoryKind.Pinned), Token)).Items).Pinned);
        var query = new SavedQuery(Guid.NewGuid(), "讀者查詢", null, state.LatestRevision.RevisionId, SavedQueryScope.Global, null, false);
        Assert.Equal(SavedQueryWriteResult.Committed, await repository.WriteSavedQueryAsync(new SavedQueryWrite(query), Token));
        Assert.NotNull(await (await store.Open(Token)).ReadSavedQueryAsync(query.SavedQueryId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FailedMigrationRollsBackDdlAndVersionAndCanRetryWithoutRebuilding()
    {
        using var store = new SqliteTestStore();
        CreateV1(store);
        // 在第二段 DDL 注入名稱衝突，證明先前建表也隨交易回復，而非只驗證版本數字。
        store.Scalar("CREATE INDEX IX_SavedQueries_ScopeId ON Documents(DisplayName);");
        await Assert.ThrowsAsync<SqliteException>(() => store.Open(Token));
        Assert.Equal(1L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM sqlite_master WHERE name='SavedQueries';"));
        Assert.Equal(StoreId, store.Scalar("SELECT StoreId FROM StoreInfo;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        store.Scalar("DROP INDEX IX_SavedQueries_ScopeId;");
        await store.Open(Token);
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    public async Task UnsupportedVersionsAreNotMigrated(int version)
    {
        using var store = new SqliteTestStore();
        CreateV1(store);
        store.Scalar("PRAGMA user_version=" + version + ";");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Open(Token));
        Assert.Equal((long)version, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM sqlite_master WHERE name='SavedQueries';"));
    }

    [Fact]
    public async Task CanceledOpenLeavesV1Untouched()
    {
        using var store = new SqliteTestStore();
        CreateV1(store);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Open(source.Token));
        Assert.Equal(1L, store.Scalar("PRAGMA user_version;"));
    }

    private static void CreateV2(SqliteTestStore store)
    {
        CreateV1(store);
        Apply(store, 2);
        store.Scalar("INSERT INTO SavedQueries VALUES('33333333333333333333333333333333','讀者收藏',NULL,'" + RevisionId +
            "',0,NULL,NULL,NULL,0,'44444444444444444444444444444444');");
    }

    private static void CreateV3(SqliteTestStore store)
    {
        CreateV2(store);
        Apply(store, 3);
    }

    // 固定的增量 fixture 不依賴產品 schema，避免新舊版本一起改而失去 migration 證據。
    private static void Apply(SqliteTestStore store, int version)
    {
        var name = "SqlAssist.QueryMemory.Sqlite.Tests.QueryMemorySchemaV" + version + ".sql";
        using var stream = typeof(SqliteMigrationTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("缺少固定 v" + version + " fixture。");
        using var reader = new StreamReader(stream);
        store.Scalar(reader.ReadToEnd());
    }

    [Fact]
    public async Task PopulatedV2MigratesConcurrentlyAndSeedsExactUsageWithoutChangingSavedTokens()
    {
        using var store = new SqliteTestStore();
        CreateV2(store);
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(StoreId, store.Scalar("SELECT StoreId FROM StoreInfo;"));
        Assert.Equal(2 * Sql.Length, (await repositories[0].ReadUsageAsync(Token)).ContentBytes);
        var saved = await repositories[0].ReadSavedQueryAsync(Guid.ParseExact("33333333333333333333333333333333", "N"), Token);
        Assert.NotNull(saved);
        Assert.Equal(Guid.ParseExact("44444444444444444444444444444444", "N"), saved.Version);
        Assert.Equal(Sql, (await repositories[0].ReadContentAsync(saved.ContentId, Token))?.SqlText);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FailedV3MigrationRollsBackCounterTriggersAndIndexesAndCanRetry()
    {
        using var store = new SqliteTestStore();
        CreateV2(store);
        store.Scalar("CREATE INDEX IX_History_Context ON Documents(DisplayName);");
        await Assert.ThrowsAsync<SqliteException>(() => store.Open(Token));
        Assert.Equal(2L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM sqlite_master WHERE name IN ('StorageUsage','TR_Contents_Insert','IX_Revisions_Parent');"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM SavedQueries;"));
        store.Scalar("DROP INDEX IX_History_Context;");
        var repository = await store.Open(Token);
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(2 * Sql.Length, (await repository.ReadUsageAsync(Token)).ContentBytes);
    }

    [Fact]
    public async Task PopulatedV3MigratesConcurrentlyAndKeepsUsageTokensAndContentWhileEnablingQuotas()
    {
        using var store = new SqliteTestStore();
        CreateV3(store);
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(StoreId, store.Scalar("SELECT StoreId FROM StoreInfo;"));
        Assert.Equal(2 * Sql.Length, (await repositories[0].ReadUsageAsync(Token)).ContentBytes);
        var saved = await repositories[0].ReadSavedQueryAsync(Guid.ParseExact("33333333333333333333333333333333", "N"), Token);
        Assert.NotNull(saved);
        Assert.Equal(Guid.ParseExact("44444444444444444444444444444444", "N"), saved.Version);
        Assert.Equal(Sql, (await repositories[0].ReadContentAsync(saved.ContentId, Token))?.SqlText);
        // 升級後配額界線才有索引可用；舊庫不會因為沒有索引而退回全表掃描。
        var quota = new QueryMemoryMaintenancePolicy(null, null, null, 1, 1);
        Assert.Equal(QueryMemoryCapacityStatus.WithinLimit,
            (await repositories[0].MaintainAsync(new QueryMemoryMaintenanceRequest(quota, 500), Token)).CapacityStatus);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FailedV4MigrationRollsBackTheAutoRevisionIndexAndCanRetry()
    {
        using var store = new SqliteTestStore();
        CreateV3(store);
        store.Scalar("CREATE INDEX IX_Revisions_SessionAuto ON Documents(DisplayName);");
        await Assert.ThrowsAsync<SqliteException>(() => store.Open(Token));
        Assert.Equal(3L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM SavedQueries;"));
        Assert.Equal(2L * Sql.Length, store.Scalar("SELECT ContentBytes FROM StorageUsage;"));
        store.Scalar("DROP INDEX IX_Revisions_SessionAuto;");
        var repository = await store.Open(Token);
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(2 * Sql.Length, (await repository.ReadUsageAsync(Token)).ContentBytes);
    }

    private static void CreateV4(SqliteTestStore store)
    {
        CreateV3(store);
        Apply(store, 4);
    }

    private static void CreateV5(SqliteTestStore store)
    {
        CreateV4(store);
        Apply(store, 5);
    }

    private const string SavedId = "33333333333333333333333333333333";
    private const string TagSql = "SELECT * FROM Lib_Tag;";

    [Fact]
    public async Task PopulatedV4MigratesConcurrentlyAndLeavesOldRevisionsUntaggedWhileEnablingSavedEdits()
    {
        using var store = new SqliteTestStore();
        CreateV4(store);
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(StoreId, store.Scalar("SELECT StoreId FROM StoreInfo;"));
        // 既有版本不會被追認成某個收藏的 SQL 編輯結果。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE SavedQueryId IS NULL;"));
        var savedId = Guid.ParseExact(SavedId, "N");
        var saved = await repositories[0].ReadSavedQueryAsync(savedId, Token);
        Assert.NotNull(saved);
        var edit = new SavedQueryEdit(savedId, saved.Version, Guid.NewGuid(), TagSql, SqliteTestStore.Start.AddDays(1));
        Assert.Equal(SavedQueryWriteResult.Committed, await repositories[0].EditSavedQuerySqlAsync(edit, Token));
        var edited = await repositories[0].ReadSavedQueryAsync(savedId, Token);
        Assert.NotNull(edited);
        Assert.Equal(edit.RevisionId, edited.Query.CurrentRevisionId);
        Assert.Equal(TagSql, edited.Preview);
        Assert.Equal(2 * (Sql.Length + TagSql.Length), (await repositories[0].ReadUsageAsync(Token)).ContentBytes);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FailedV5MigrationRollsBackTheSavedTagColumnAndCanRetry()
    {
        using var store = new SqliteTestStore();
        CreateV4(store);
        store.Scalar("CREATE INDEX IX_Revisions_Saved ON Documents(DisplayName);");
        await Assert.ThrowsAsync<SqliteException>(() => store.Open(Token));
        Assert.Equal(4L, store.Scalar("PRAGMA user_version;"));
        // ADD COLUMN 也隨交易回復，不是只有索引沒建起來。
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM pragma_table_info('Revisions') WHERE name='SavedQueryId';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM SavedQueries;"));
        store.Scalar("DROP INDEX IX_Revisions_Saved;");
        var repository = await store.Open(Token);
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM pragma_table_info('Revisions') WHERE name='SavedQueryId';"));
        Assert.Equal(2 * Sql.Length, (await repository.ReadUsageAsync(Token)).ContentBytes);
    }

    [Fact]
    public async Task PopulatedV5MigratesConcurrentlyAndLeavesOldSessionsWithoutALeaseWhileEnablingHeartbeats()
    {
        using var store = new SqliteTestStore();
        CreateV5(store);
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(StoreId, store.Scalar("SELECT StoreId FROM StoreInfo;"));
        // 升級不追認既有 Session 有人在用；它們的擁有權要等宿主重新開租約才會出現。
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Sessions WHERE LeaseId IS NOT NULL;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Leases;"));
        var lease = await repositories[0].OpenLeaseAsync(Owner, SqliteTestStore.Start, Token);
        Assert.Equal(lease, store.Scalar("SELECT LeaseId FROM Leases;"));
        Assert.Equal(2 * Sql.Length, (await repositories[0].ReadUsageAsync(Token)).ContentBytes);
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task FailedV6MigrationRollsBackTheLeaseTableAndColumnAndCanRetry()
    {
        using var store = new SqliteTestStore();
        CreateV5(store);
        store.Scalar("CREATE INDEX IX_Sessions_Lease ON Documents(DisplayName);");
        await Assert.ThrowsAsync<SqliteException>(() => store.Open(Token));
        Assert.Equal(5L, store.Scalar("PRAGMA user_version;"));
        // 建表、ADD COLUMN 與索引同屬一個交易；失敗不能留下半套租約 schema。
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM sqlite_master WHERE name='Leases';"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM pragma_table_info('Sessions') WHERE name='LeaseId';"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM SavedQueries;"));
        store.Scalar("DROP INDEX IX_Sessions_Lease;");
        var repository = await store.Open(Token);
        Assert.Equal(6L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM pragma_table_info('Sessions') WHERE name='LeaseId';"));
        Assert.Equal(2 * Sql.Length, (await repository.ReadUsageAsync(Token)).ContentBytes);
    }

    private static QueryMemoryLeaseOwner Owner => new("LIBRARYPC", 4242, SqliteTestStore.Start);
}
