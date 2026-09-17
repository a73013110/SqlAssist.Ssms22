using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class IsolatedRepositoryTests
{
    [Fact]
    public async Task FailedInitializationCanBeFollowedByAnotherRepository()
    {
        using var store = new SqliteTestStore();
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.Path, "保留損壞資料庫，不重建");
        var token = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token));
        // 分類與原始錯誤碼跨 AppDomain 保留：SQLITE_NOTADB 是損毀，不是可重試的忙碌。
        Assert.Equal(SqlMemoryStorageErrorKind.Corrupt, error.Kind);
        Assert.Equal(26, error.ErrorCode);
        Assert.Equal("SqliteException", error.SourceType);
        Assert.False(error.IsTransient);
        Assert.Equal("保留損壞資料庫，不重建", File.ReadAllText(store.Path));
        using var reopened = await IsolatedSqlMemoryStore.OpenAsync(Path.Combine(store.DirectoryPath, "new.db"), null, token);
        Assert.Contains("3.53.4", await reopened.ProbeAsync(token));
    }

    [Fact]
    public async Task StorageErrorKindsSurviveTheAppDomainBoundary()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        using (var repository = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token, busyTimeoutSeconds: 1))
        {
            using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
            {
                blocker.Open();
                using var transaction = blocker.BeginTransaction(deferred: false);
                var write = new SqlCapturePlanner().Prepare(store.Capture(), null, SqliteTestStore.Policy);
                Assert.NotNull(write);
                var busy = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.CommitAsync(write, null, token));
                Assert.Equal(SqlMemoryStorageErrorKind.Busy, busy.Kind);
                Assert.Equal(5, busy.ErrorCode);
                Assert.True(busy.IsTransient);
            }

            var cursor = await Assert.ThrowsAsync<SqlMemoryStorageException>(() =>
                repository.ReadHistoryAsync(new SqlHistoryRequest(1, cursor: "!invalid!"), token));
            Assert.Equal(SqlMemoryStorageErrorKind.InvalidCursor, cursor.Kind);
            var argument = await Assert.ThrowsAsync<SqlMemoryStorageException>(() =>
                repository.ReadExpiredLeasesAsync(SqliteTestStore.Start, 0, null, token));
            Assert.Equal(SqlMemoryStorageErrorKind.InvalidArgument, argument.Kind);
            Assert.Equal("ArgumentOutOfRangeException", argument.SourceType);
        }

        store.Scalar("PRAGMA user_version=100;");
        var incompatible = await Assert.ThrowsAsync<SqlMemoryStorageException>(() =>
            IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token));
        Assert.Equal(SqlMemoryStorageErrorKind.Incompatible, incompatible.Kind);
    }

    [Fact]
    public async Task WriterRidesOutAnotherConnectionHoldingTheWriteLock()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        using var repository = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token, busyTimeoutSeconds: 1);
        var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        blocker.Open();
        var transaction = blocker.BeginTransaction(deferred: false);
        var failures = 0;
        var writer = new SqlCaptureQueue(
            new SqlCaptureCommitter(repository, new SqlCapturePlanner(),
                busyDelays: new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }),
            8, 10000, (_, _) => Interlocked.Increment(ref failures));
        try
        {
            Assert.Equal(SqlCaptureEnqueueResult.Accepted, writer.TryEnqueue(store.Capture(), SqliteTestStore.Policy));
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

    /// <summary>找不到的字面搜尋；每列都得掃完整份大 SQL，沒有取消就要跑很久。</summary>
    private static readonly SqlHistoryRequest SlowSearch = new(20, search: "Lib_Branch 不存在");

    /// <summary>
    /// 一份約 1 MB 的 SQL 由數千列 History 引用：資料庫不大，但無篩選的全文搜尋要逐列解出並掃過 BLOB，
    /// 開發機上完整跑完要十幾秒，遠長於下面的等待與取消時限。
    /// </summary>
    private static async Task SeedSlowSearch(SqliteTestStore store, IsolatedSqlMemoryStore repository, CancellationToken token)
    {
        await new SqlCaptureCommitter(repository, new SqlCapturePlanner())
            .ProcessAsync(store.Capture(sql: "SELECT * FROM Lib_Reader WHERE Note = '" + new string('x', 500_000) + "';"),
                SqliteTestStore.Policy, token);
        store.Scalar(@"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000)
INSERT INTO History(EntryKey,SessionId,RevisionId,ContentId,CreatedAt,Kind,Server,DatabaseName,ExecutionCount,FirstExecutedAt)
SELECT 'x' || printf('%032d', i), h.SessionId, h.RevisionId, h.ContentId, h.CreatedAt, h.Kind, h.Server, h.DatabaseName,
 h.ExecutionCount, h.FirstExecutedAt
FROM n, (SELECT * FROM History LIMIT 1) h;");
    }

    /// <summary>放大搜尋預算，讓整份種子資料都在同一頁掃完；預設預算下這個搜尋很快就會以部分結果返回。</summary>
    private static Task<IsolatedSqlMemoryStore> OpenForSlowSearch(SqliteTestStore store, CancellationToken token) =>
        IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token, 5, 1_000_000, long.MaxValue);

    /// <summary>給派送一點時間進入 worker；之後仍未完成才代表它真的在隔離 AppDomain 內執行。</summary>
    private static async Task<Task<T>> StartInFlight<T>(Func<Task<T>> operation, CancellationToken token)
    {
        var running = operation();
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        Assert.False(running.IsCompleted, "搜尋太快結束，無法驗證並行；請加大種子資料。");
        return running;
    }

    [Fact]
    public async Task ReadsAndCommitsProceedDuringLongRunningSearch()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        using var repository = await OpenForSlowSearch(store, token);
        await SeedSlowSearch(store, repository, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var search = await StartInFlight(() => repository.ReadHistoryAsync(SlowSearch, cancellation.Token), token);

        // 舊設計每個操作互斥，下面兩個呼叫會排在搜尋之後；WAL 下讀取與提交都不必等長讀取。
        var other = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        var commit = new SqlCaptureCommitter(repository, new SqlCapturePlanner())
            .ProcessAsync(store.Capture(sql: "SELECT * FROM Loan;", session: other), SqliteTestStore.Policy, token);
        await Within(commit, TimeSpan.FromSeconds(10));
        var read = repository.ReadSessionAsync(other.SessionId, token);
        await Within(read, TimeSpan.FromSeconds(10));
        Assert.NotNull(await read);
        Assert.False(search.IsCompleted, "提交與讀取應在長搜尋結束前完成。");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task CancellationStopsSearchInsideIsolatedDomain()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        using var repository = await OpenForSlowSearch(store, token);
        await SeedSlowSearch(store, repository, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var search = await StartInFlight(() => repository.ReadHistoryAsync(SlowSearch, cancellation.Token), token);

        cancellation.Cancel();
        // 已派送的搜尋要在 worker 端的 KMP 掃描中停下，而不是跑完整輪；也不得變成儲存錯誤。
        var stopped = await Task.WhenAny(search, Task.Delay(TimeSpan.FromSeconds(5), token));
        Assert.Same(search, stopped);
        var error = await Record.ExceptionAsync(() => search);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(cancellation.Token, ((OperationCanceledException)error!).CancellationToken);
        Assert.True(search.IsCanceled);

        // 取消只結束那一個操作；同一個 worker 之後照常可用。
        Assert.NotNull(await repository.ReadSessionAsync(store.Session.SessionId, token));
    }

    [Fact]
    public async Task DisposeWaitsForOperationsInFlight()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        var repository = await OpenForSlowSearch(store, token);
        await SeedSlowSearch(store, repository, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var search = await StartInFlight(() => repository.ReadHistoryAsync(SlowSearch, cancellation.Token), token);

        var disposing = Task.Run(repository.Dispose, token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        Assert.False(disposing.IsCompleted, "卸載不得越過仍在隔離 AppDomain 內的操作。");
        Assert.False(search.IsCompleted);
        // 卸載開始後拒絕新操作，不排在進行中的操作後面。
        await Assert.ThrowsAsync<ObjectDisposedException>(() => repository.ReadSessionAsync(store.Session.SessionId, token));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        await Within(disposing, TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => repository.ReadSessionAsync(store.Session.SessionId, token));
        using var reopened = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token);
        Assert.NotNull(await reopened.ReadSessionAsync(store.Session.SessionId, token));
    }

    [Fact]
    public async Task CoreDtosRoundTripThroughIsolatedDomainAndDomainCanReopen()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        var repository = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token);
        try
        {
            var processor = new SqlCaptureCommitter(repository, new SqlCapturePlanner());
            await processor.ProcessAsync(store.Capture(selected: "SELECT 1"), SqliteTestStore.Policy, token);
            var state = await repository.ReadSessionAsync(store.Session.SessionId, token);
            Assert.NotNull(state);
            var page = await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Executions), token);
            var entry = Assert.Single(page.Items);
            Assert.Equal("SELECT 1", (await repository.ReadContentAsync(entry.ContentId, token))?.SqlText);
            Assert.Contains("3.53.4", await repository.ProbeAsync(token));
        }
        finally { repository.Dispose(); }
        repository.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => repository.ReadSessionAsync(store.Session.SessionId, token));
        using var reopened = await IsolatedSqlMemoryStore.OpenAsync(store.Path, null, token);
        Assert.NotNull(await reopened.ReadSessionAsync(store.Session.SessionId, token));
    }
}
