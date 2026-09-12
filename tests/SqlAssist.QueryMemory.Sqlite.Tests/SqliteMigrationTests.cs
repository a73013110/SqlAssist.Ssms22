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
        Assert.Equal(3L, store.Scalar("PRAGMA user_version;"));
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
        Assert.Equal(3L, store.Scalar("PRAGMA user_version;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
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
        // 固定的 v2 增量 fixture 不依賴產品 schema，避免新舊版本一起改而失去 migration 證據。
        using var stream = typeof(SqliteMigrationTests).Assembly.GetManifestResourceStream("SqlAssist.QueryMemory.Sqlite.Tests.QueryMemorySchemaV2.sql")
            ?? throw new InvalidOperationException("缺少固定 v2 fixture。");
        using var reader = new StreamReader(stream);
        store.Scalar(reader.ReadToEnd());
        store.Scalar("INSERT INTO SavedQueries VALUES('33333333333333333333333333333333','讀者收藏',NULL,'" + RevisionId +
            "',0,NULL,NULL,NULL,0,'44444444444444444444444444444444');");
    }

    [Fact]
    public async Task PopulatedV2MigratesConcurrentlyAndSeedsExactUsageWithoutChangingSavedTokens()
    {
        using var store = new SqliteTestStore();
        CreateV2(store);
        var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Open(Token)));
        Assert.Equal(3L, store.Scalar("PRAGMA user_version;"));
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
        Assert.Equal(3L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(2 * Sql.Length, (await repository.ReadUsageAsync(Token)).ContentBytes);
    }
}
