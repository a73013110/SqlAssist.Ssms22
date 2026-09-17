using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.SqlMemory.Sqlite.Tests.SqliteTestStore;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteCompactionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // 每筆內容都要跨多個頁面，刪除後才有可觀測的自由頁讓 VACUUM 歸還。
    private static string Bulk(int sequence) => "SELECT '" + new string((char)('A' + sequence % 26), 4000) +
        "' FROM Lib_Reader WHERE ReaderId=" + sequence + ";";

    private static SqliteConnection Reader(SqliteTestStore store)
    {
        // 最後一個連線關閉時 SQLite 會 checkpoint 並刪掉 WAL；留一條連線才量得到 WAL。
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        // 只開啟還不會掛上 WAL 索引；先讀一次才算得上「另一個連線」。
        using var attach = connection.CreateCommand();
        attach.CommandText = "SELECT count(*) FROM sqlite_master;";
        attach.ExecuteScalar();
        return connection;
    }

    private static async Task Seed(SqliteTestStore store, SqliteTestRepository repository, int from, int count)
    {
        for (var sequence = from; sequence < from + count; sequence++)
            await store.Process(repository, store.Capture(sequence, Bulk(sequence), seconds: sequence), Token);
    }

    [Fact]
    public async Task CheckpointTruncatesTheWalAndLeavesLogicalContentUntouched()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        using var idle = Reader(store);
        await Seed(store, repository, 1, 4);
        var before = await repository.ReadUsageAsync(Token);
        Assert.True(before.WalFileBytes > 0);
        var result = await repository.CheckpointAsync(Token);
        Assert.True(result.Truncated);
        Assert.Equal(0L, result.Usage.WalFileBytes);
        // checkpoint 只搬動 WAL 影格，不是清理；邏輯容量與歷史必須原封不動。
        Assert.Equal(before.ContentBytes, result.Usage.ContentBytes);
        Assert.Equal(4, (await repository.ReadHistoryAsync(new SqlHistoryRequest(50, SqlHistoryFilter.Executions), Token)).Items.Count);
    }

    [Fact]
    public async Task CheckpointReportsNotTruncatedWhileAnotherConnectionHoldsAnOlderSnapshot()
    {
        using var store = new SqliteTestStore();
        // busy timeout 決定 checkpoint 等多久才放棄；測試取最短值，不改動產品預設。
        var repository = await SqliteTestRepository.OpenAsync(store.Path, Token, busyTimeoutSeconds: 1);
        await Seed(store, repository, 1, 2);
        using var reader = Reader(store);
        using var snapshot = reader.BeginTransaction(deferred: true);
        using (var command = reader.CreateCommand())
        {
            command.Transaction = snapshot;
            command.CommandText = "SELECT count(*) FROM History;";
            command.ExecuteScalar();
        }
        await Seed(store, repository, 3, 4);
        var result = await repository.CheckpointAsync(Token);
        // 不中斷還在讀舊快照的連線；只回報未截斷，由排程下一輪再試。
        Assert.False(result.Truncated);
        Assert.True(result.Usage.WalFileBytes > 0);
        snapshot.Rollback();
        Assert.True((await repository.CheckpointAsync(Token)).Truncated);
    }

    [Fact]
    public async Task CompactRebuildsTheDatabaseAndReturnsFreePagesToTheFileSystem()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await Seed(store, repository, 1, 24);
        await Drain(repository, Expired(), 500);
        var reclaimed = await repository.ReadUsageAsync(Token);
        // 刪除只把頁面還給 SQLite 重用；檔案要等 VACUUM 重建才會縮小。
        Assert.True((long)store.Scalar("PRAGMA freelist_count;")! > 0);
        var usage = await repository.CompactAsync(Token);
        Assert.Equal(0L, store.Scalar("PRAGMA freelist_count;"));
        Assert.True(usage.DatabaseFileBytes < reclaimed.DatabaseFileBytes);
        Assert.Equal(0L, usage.WalFileBytes);
        Assert.Equal(reclaimed.ContentBytes, usage.ContentBytes);
        // VACUUM 重建整個資料庫，但識別碼、schema 版本與外鍵完整性都必須沿用。
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
        Assert.Equal(5L, store.Scalar("PRAGMA user_version;"));
        Assert.Equal(0x534d454dL, store.Scalar("PRAGMA application_id;"));
        Assert.NotNull(await repository.ReadSessionAsync(store.Session.SessionId, Token));
    }
}
