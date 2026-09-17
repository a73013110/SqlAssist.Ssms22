using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.SqlMemory.Isolation;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class StorageSelfTestTests
{
    [Fact]
    public async Task SelfTestProducesReportAndCanRunAgainWithoutLeakingDatabaseHandle()
    {
        using var store = new SqliteTestStore();
        // 其他 integration tests 共用此程序，先載入它們的 provider，避免把測試初始化當成宿主洩漏。
        await store.Open(TestContext.Current.CancellationToken);
        for (var i = 0; i < 2; i++)
        {
            var directory = Path.Combine(store.DirectoryPath, i.ToString());
            await SqlMemoryStorageSelfTest.RunAsync(directory, null, TestContext.Current.CancellationToken);
            var report = File.ReadAllText(Path.Combine(directory, SqlMemoryStorageSelfTest.ReportFileName));
            Assert.Contains("PASS |", report);
            Assert.Contains("21 次執行與冪等重送", report);
            Assert.Contains("SQL Favorite CRUD、標註篩選、搜尋、版本衝突與刪除後歷史保留", report);
            Assert.Contains("有界維護續跑、筆數配額、容量量測與無法回收時保護 Session head／Recovery", report);
            Assert.Contains("Session 心跳租約在租約還在時保護未存檔回復內容", report);
            Assert.Contains("回收失效租約後才清除未存檔回復內容，WAL 截斷與整理保留其餘內容", report);
            Assert.Contains("最後一次卸載與資料庫檔案釋放", report);
            Assert.Contains("宿主 AppDomain 未新增 SQLite provider", report);
            Assert.DoesNotContain("FAIL |", report);
            Assert.DoesNotContain("\r", report);
            using var database = File.Open(Path.Combine(directory, "self-test.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [Fact]
    public async Task ExistingReportIsNeverOverwritten()
    {
        using var store = new SqliteTestStore();
        Directory.CreateDirectory(store.DirectoryPath);
        var report = Path.Combine(store.DirectoryPath, SqlMemoryStorageSelfTest.ReportFileName);
        File.WriteAllText(report, "保留診斷證據");
        await Assert.ThrowsAsync<IOException>(() => SqlMemoryStorageSelfTest.RunAsync(store.DirectoryPath, null, TestContext.Current.CancellationToken));
        Assert.Equal("保留診斷證據", File.ReadAllText(report));
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "self-test.db")));
    }

    [Fact]
    public async Task ExistingDatabaseFailsWithReportInsteadOfReplacingData()
    {
        using var store = new SqliteTestStore();
        Directory.CreateDirectory(store.DirectoryPath);
        var database = Path.Combine(store.DirectoryPath, "self-test.db");
        File.WriteAllText(database, "必須保留");
        await Assert.ThrowsAsync<IOException>(() => SqlMemoryStorageSelfTest.RunAsync(store.DirectoryPath, null, TestContext.Current.CancellationToken));
        Assert.Equal("必須保留", File.ReadAllText(database));
        Assert.Contains("FAIL |", File.ReadAllText(Path.Combine(store.DirectoryPath, SqlMemoryStorageSelfTest.ReportFileName)));
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotCreateArtifacts()
    {
        using var store = new SqliteTestStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlMemoryStorageSelfTest.RunAsync(store.DirectoryPath, null, cancellation.Token));
        Assert.False(Directory.Exists(store.DirectoryPath));
    }
}
