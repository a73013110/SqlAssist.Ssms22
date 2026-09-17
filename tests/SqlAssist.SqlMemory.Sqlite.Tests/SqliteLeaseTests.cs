using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.SqlMemory.Sqlite.Tests.SqliteTestStore;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteLeaseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SqlMemoryLeaseOwner Owner => new("LIBRARYPC", 4242, Start);
    private static readonly SqlCapturePolicy DraftsOnly = new(true, false, TimeSpan.FromMinutes(10), false, true);

    /// <summary>連未存檔草稿都過期的政策；Recovery 期限是獨立的一個，不跟著草稿期限走。</summary>
    private static SqlRetentionPolicy ExpiredWithRecovery() =>
        new(Start.AddDays(1), Start.AddDays(1), null, null, null, null, Start.AddDays(1));

    private static Task SeedRecovery(SqliteTestStore store, SqliteTestRepository repository, string? lease = null,
        long sequence = 1) =>
        store.Process(repository, store.Capture(sequence, sequence == 1 ? "SELECT * FROM Lib_Reader;" : "SELECT * FROM Loan WHERE CopyNo > " + sequence + ";",
            SqlCaptureKind.DraftIdle), Token, DraftsOnly, lease);

    [Fact]
    public async Task OpenLeaseStampsSessionsAndTheSameProcessReusesItsRow()
    {
        using var store = new SqliteTestStore();
        var first = await store.Open(Token);
        var lease = await first.OpenLeaseAsync(Owner, Start, Token);
        await SeedRecovery(store, first, lease);
        Assert.Equal(lease, store.Scalar("SELECT LeaseId FROM Sessions;"));
        // 同一個程序重開 repository 必須沿用原本那一列，既有 Session 才不會落在沒人續的租約上。
        var second = await store.Open(Token);
        Assert.Equal(lease, await second.OpenLeaseAsync(Owner, Start.AddMinutes(5), Token));
        Assert.Equal(Start.AddMinutes(5).UtcTicks, store.Scalar("SELECT RenewedAt FROM Leases;"));
        var third = await store.Open(Token);
        Assert.NotEqual(lease, await third.OpenLeaseAsync(Owner with { ProcessId = 99 }, Start, Token));
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Leases;"));
        await Assert.ThrowsAsync<ArgumentException>(() => first.OpenLeaseAsync(Owner with { MachineName = " " }, Start, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => first.OpenLeaseAsync(null!, Start, Token));
    }

    [Fact]
    public async Task LeasedRecoverySurvivesAndOnlyBecomesReclaimableAfterTheLeaseIsReleased()
    {
        using var store = new SqliteTestStore();
        var owner = await store.Open(Token);
        var lease = await owner.OpenLeaseAsync(Owner, Start, Token);
        await SeedRecovery(store, owner, lease);
        await Drain(owner, ExpiredWithRecovery());
        // 租約還在就代表那個程序可能還開著這份草稿，期限到了也不能回收。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        var reaper = await store.Open(Token);
        var expired = await reaper.ReadExpiredLeasesAsync(Start.AddDays(1), 10, excludedLeaseId: null, Token);
        var found = Assert.Single(expired);
        Assert.Equal(lease, found.LeaseId);
        Assert.Equal(Owner, found.Owner);
        Assert.Equal(Start, found.RenewedAt);
        Assert.Equal(1, await reaper.ReleaseLeasesAsync(new[] { lease }, Start.AddDays(1), Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Sessions WHERE LeaseId IS NOT NULL;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Leases;"));
        await Drain(reaper, ExpiredWithRecovery());
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
        // 投影與內容跟著走，不留下指不到草稿的歷史列。
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM History;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task WithoutARecoveryCutoffAnUnleasedDraftIsStillNeverReclaimedByAge()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await SeedRecovery(store, repository);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Sessions WHERE LeaseId IS NOT NULL;"));
        // 宿主沒有在續心跳就不會給 Recovery 期限；沒有租約不等於可以刪掉使用者還開著的內容。
        await Drain(repository, Expired(0));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM History;"));
        await Drain(repository, ExpiredWithRecovery());
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
    }

    [Fact]
    public async Task ReleaseSkipsLeasesThatCameBackAndNeverTouchesTheMaintenanceLeaseOrTheExcludedOne()
    {
        using var store = new SqliteTestStore();
        var owner = await store.Open(Token);
        var lease = await owner.OpenLeaseAsync(Owner, Start, Token);
        var reaper = await store.Open(Token);
        var mine = await reaper.OpenLeaseAsync(Owner with { ProcessId = 77 }, Start, Token);
        Assert.True(await reaper.TryAcquireMaintenanceLeaseAsync(Owner with { ProcessId = 77 }, Start, Start, Token));
        // 維護租約永遠不在過期清單裡；呼叫端自己的租約要明確排除，儲存層不記得誰是「自己」。
        Assert.Equal(new[] { lease, mine }.OrderBy(id => id, StringComparer.Ordinal),
            (await reaper.ReadExpiredLeasesAsync(Start.AddDays(1), 10, excludedLeaseId: null, Token))
                .Select(found => found.LeaseId).OrderBy(id => id, StringComparer.Ordinal));
        var expired = await reaper.ReadExpiredLeasesAsync(Start.AddDays(1), 10, mine, Token);
        Assert.Equal(lease, Assert.Single(expired).LeaseId);
        Assert.True(await owner.RenewLeaseAsync(lease, Start.AddDays(2), Token));
        // 宿主判斷存活到這裡刪除之間，對方可能已經回來續約；交易內重查才不會誤刪。
        Assert.Equal(0, await reaper.ReleaseLeasesAsync(new[] { lease, "maintenance" }, Start.AddDays(1), Token));
        Assert.Equal(3L, store.Scalar("SELECT count(*) FROM Leases;"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => reaper.ReleaseLeasesAsync(null!, Start, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => reaper.ReleaseLeasesAsync(new string[] { null! }, Start, Token));
    }

    [Fact]
    public async Task MaintenanceLeaseIsExclusiveUntilItExpiresAndStaysOnOneRow()
    {
        using var store = new SqliteTestStore();
        var first = await store.Open(Token);
        var second = await store.Open(Token);
        var other = Owner with { ProcessId = 77 };
        Assert.True(await first.TryAcquireMaintenanceLeaseAsync(Owner, Start, Start, Token));
        Assert.False(await second.TryAcquireMaintenanceLeaseAsync(other, Start.AddMinutes(1), Start, Token));
        Assert.True(await first.TryAcquireMaintenanceLeaseAsync(Owner, Start.AddMinutes(2), Start, Token));
        // 只認過期：持有者停止續約之後，別的程序才接手同一列。
        Assert.True(await second.TryAcquireMaintenanceLeaseAsync(other, Start.AddMinutes(10), Start.AddMinutes(5), Token));
        Assert.False(await first.TryAcquireMaintenanceLeaseAsync(Owner, Start.AddMinutes(11), Start.AddMinutes(5), Token));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Leases;"));
        Assert.Equal(77L, store.Scalar("SELECT ProcessId FROM Leases WHERE LeaseId='maintenance';"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => first.TryAcquireMaintenanceLeaseAsync(null!, Start, Start, Token));
    }

    /// <summary>正常卸載交回維護租約，別人不必等過期；但只交得回自己持有的那一列。</summary>
    [Fact]
    public async Task ReleasingTheMaintenanceLeaseOnlyRemovesTheOwnersRowAndLetsOthersInImmediately()
    {
        using var store = new SqliteTestStore();
        var first = await store.Open(Token);
        var second = await store.Open(Token);
        var other = Owner with { ProcessId = 77 };
        Assert.True(await first.TryAcquireMaintenanceLeaseAsync(Owner, Start, Start, Token));
        Assert.False(await second.ReleaseMaintenanceLeaseAsync(other, Token));
        Assert.False(await second.TryAcquireMaintenanceLeaseAsync(other, Start.AddMinutes(1), Start, Token));

        Assert.True(await first.ReleaseMaintenanceLeaseAsync(Owner, Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Leases;"));
        Assert.True(await second.TryAcquireMaintenanceLeaseAsync(other, Start.AddMinutes(1), Start, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => first.ReleaseMaintenanceLeaseAsync(null!, Token));
    }

    [Fact]
    public async Task ExpiryAndOwnershipProbesSeekTheirIndexesInsteadOfScanningEveryLease()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        var plans = new[]
        {
            ("SELECT LeaseId FROM Leases WHERE RenewedAt<1 ORDER BY RenewedAt LIMIT 10;", "IX_Leases_Renewed"),
            // 釋放租約要解除該程序所有 Session 的標記，不能為此掃過整張 Sessions。
            ("SELECT SessionId FROM Sessions WHERE LeaseId='test';", "IX_Sessions_Lease"),
            // 維護每個 Recovery 候選都問一次擁有權；主鍵查一列就夠，不必再走第二個索引。
            ("SELECT 1 FROM Sessions WHERE SessionId='test' AND LeaseId IS NOT NULL;", "sqlite_autoindex_Sessions_1"),
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
    public async Task FailedOpenLeaseLeavesNoRowBehind()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        // 插入後、提交前失敗：交易回滾，不留下沒人持有識別碼的租約列。
        store.Scalar("CREATE TRIGGER Test_RejectLease BEFORE INSERT ON Leases BEGIN SELECT RAISE(ABORT,'測試：租約寫入失敗'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => repository.OpenLeaseAsync(Owner, Start, Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Leases;"));

        store.Scalar("DROP TRIGGER Test_RejectLease;");
        var busy = await SqliteTestRepository.OpenAsync(store.Path, Token, busyTimeoutSeconds: 1);
        using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
        {
            blocker.Open();
            using var transaction = blocker.BeginTransaction(deferred: false);
            Assert.Equal(5, (await Assert.ThrowsAsync<SqliteException>(() => busy.OpenLeaseAsync(Owner, Start, Token))).SqliteErrorCode);
        }
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Leases;"));
        var opened = await busy.OpenLeaseAsync(Owner, Start, Token);
        Assert.True(await busy.RenewLeaseAsync(opened, Start.AddMinutes(1), Token));
    }

    [Fact]
    public async Task CommitAfterAnotherProcessReclaimedTheLeaseWritesAnUnleasedSession()
    {
        using var store = new SqliteTestStore();
        var owner = await store.Open(Token);
        var lease = await owner.OpenLeaseAsync(Owner, Start, Token);
        await SeedRecovery(store, owner, lease);
        var reaper = await store.Open(Token);
        Assert.Equal(1, await reaper.ReleaseLeasesAsync(new[] { lease }, Start.AddDays(1), Token));

        // 下一次心跳之前 writer 仍帶著舊識別碼；外鍵不得讓擷取失敗，也不得憑空復活租約。
        await SeedRecovery(store, owner, lease, 2);
        var other = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        await store.Process(owner, store.Capture(kind: SqlCaptureKind.DraftIdle, session: other), Token, DraftsOnly, lease);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Sessions WHERE LeaseId IS NULL;"));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Leases;"));
        Assert.Equal(2L, store.Scalar("SELECT Version FROM Sessions WHERE SessionId='" + store.Session.SessionId.ToString("N") + "';"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));

        var reopened = await owner.OpenLeaseAsync(Owner, Start.AddMinutes(1), Token);
        await SeedRecovery(store, owner, reopened, 3);
        Assert.Equal(reopened, store.Scalar("SELECT LeaseId FROM Sessions WHERE SessionId='" + store.Session.SessionId.ToString("N") + "';"));
    }

    [Fact]
    public async Task RenewNeedsALeaseIdentifierAndReportsWhenTheRowWasReclaimed()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RenewLeaseAsync("", Start, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RenewLeaseAsync("maintenance", Start, Token));
        Assert.False(await repository.RenewLeaseAsync(Guid.NewGuid().ToString("N"), Start, Token));
        var lease = await repository.OpenLeaseAsync(Owner, Start, Token);
        // 契約無狀態：另一個 repository 拿同一個識別碼也續得到，沒有藏在實作裡的「自己的租約」。
        var reaper = await store.Open(Token);
        Assert.True(await reaper.RenewLeaseAsync(lease, Start.AddMinutes(1), Token));
        Assert.Equal(1, await reaper.ReleaseLeasesAsync(new[] { lease }, Start.AddDays(1), Token));
        // 續約失敗是「租約列已被回收」的唯一訊號；呼叫端必須重新開，不能繼續假裝擁有 Session。
        Assert.False(await repository.RenewLeaseAsync(lease, Start.AddMinutes(2), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reaper.ReadExpiredLeasesAsync(Start, 0, null, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reaper.ReadExpiredLeasesAsync(Start, 501, null, Token));
    }
}
