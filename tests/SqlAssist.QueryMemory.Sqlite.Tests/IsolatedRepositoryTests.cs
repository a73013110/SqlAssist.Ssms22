using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;
using SqlAssist.QueryMemory.Hosting;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class IsolatedRepositoryTests
{
    [Fact]
    public async Task FailedInitializationCanBeFollowedByAnotherRepository()
    {
        using var store = new SqliteTestStore();
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.Path, "保留損壞資料庫，不重建");
        var token = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAsync<QueryMemoryStorageException>(() => IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token));
        // 分類與原始錯誤碼跨 AppDomain 保留：SQLITE_NOTADB 是損毀，不是可重試的忙碌。
        Assert.Equal(QueryMemoryStorageErrorKind.Corrupt, error.Kind);
        Assert.Equal(26, error.ErrorCode);
        Assert.Equal("SqliteException", error.SourceType);
        Assert.False(error.IsTransient);
        Assert.Equal("保留損壞資料庫，不重建", File.ReadAllText(store.Path));
        using var reopened = await IsolatedQueryMemoryRepository.OpenAsync(Path.Combine(store.DirectoryPath, "new.db"), null, token);
        Assert.Contains("3.53.4", await reopened.ProbeAsync(token));
    }

    [Fact]
    public async Task StorageErrorKindsSurviveTheAppDomainBoundary()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        using (var repository = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token, busyTimeoutSeconds: 1))
        {
            using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
            {
                blocker.Open();
                using var transaction = blocker.BeginTransaction(deferred: false);
                var write = new QueryRevisionEngine().Prepare(store.Capture(), null, SqliteTestStore.Policy);
                Assert.NotNull(write);
                var busy = await Assert.ThrowsAsync<QueryMemoryStorageException>(() => repository.CommitAsync(write, null, token));
                Assert.Equal(QueryMemoryStorageErrorKind.Busy, busy.Kind);
                Assert.Equal(5, busy.ErrorCode);
                Assert.True(busy.IsTransient);
            }

            var cursor = await Assert.ThrowsAsync<QueryMemoryStorageException>(() =>
                repository.ReadHistoryAsync(new QueryHistoryRequest(1, cursor: "!invalid!"), token));
            Assert.Equal(QueryMemoryStorageErrorKind.InvalidCursor, cursor.Kind);
            var argument = await Assert.ThrowsAsync<QueryMemoryStorageException>(() =>
                repository.ReadExpiredLeasesAsync(SqliteTestStore.Start, 0, token));
            Assert.Equal(QueryMemoryStorageErrorKind.InvalidArgument, argument.Kind);
            Assert.Equal("ArgumentOutOfRangeException", argument.SourceType);
        }

        store.Scalar("PRAGMA user_version=100;");
        var incompatible = await Assert.ThrowsAsync<QueryMemoryStorageException>(() =>
            IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token));
        Assert.Equal(QueryMemoryStorageErrorKind.Incompatible, incompatible.Kind);
    }

    [Fact]
    public async Task WriterRidesOutAnotherConnectionHoldingTheWriteLock()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        using var repository = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token, busyTimeoutSeconds: 1);
        var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        blocker.Open();
        var transaction = blocker.BeginTransaction(deferred: false);
        var failures = 0;
        var writer = new QueryMemoryBackgroundWriter(
            new QueryMemoryProcessor(repository, new QueryRevisionEngine(),
                busyDelays: new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }),
            8, 10000, (_, _) => Interlocked.Increment(ref failures));
        try
        {
            Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(store.Capture(), SqliteTestStore.Policy));
            // 持鎖超過一次 busy timeout，模擬另一個程序的整理；writer 必須以退避撐過去而不是 fault。
            await Task.Delay(TimeSpan.FromSeconds(1.5), token);
        }
        finally
        {
            transaction.Dispose();
            blocker.Dispose();
        }
        await Within(writer.WaitForIdleAsync(token), TimeSpan.FromSeconds(30));
        Assert.False(writer.Completion.IsCompleted);
        Assert.Equal(0, failures);
        Assert.NotNull(await repository.ReadSessionAsync(store.Session.SessionId, token));
        await Within(writer.CompleteAsync(), TimeSpan.FromSeconds(10));
    }

    private static async Task Within(Task task, TimeSpan timeout)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(timeout)));
        await task;
    }

    [Fact]
    public async Task CoreDtosRoundTripThroughIsolatedDomainAndDomainCanReopen()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        var repository = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token);
        try
        {
            var processor = new QueryMemoryProcessor(repository, new QueryRevisionEngine());
            await processor.ProcessAsync(store.Capture(selected: "SELECT 1"), SqliteTestStore.Policy, token);
            var state = await repository.ReadSessionAsync(store.Session.SessionId, token);
            Assert.NotNull(state);
            var page = await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Executed), token);
            var entry = Assert.Single(page.Items);
            Assert.Equal("SELECT 1", (await repository.ReadContentAsync(entry.ContentId, token))?.SqlText);
            Assert.Contains("3.53.4", await repository.ProbeAsync(token));
        }
        finally { repository.Dispose(); }
        repository.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => repository.ReadSessionAsync(store.Session.SessionId, token));
        using var reopened = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token);
        Assert.NotNull(await reopened.ReadSessionAsync(store.Session.SessionId, token));
    }
}
