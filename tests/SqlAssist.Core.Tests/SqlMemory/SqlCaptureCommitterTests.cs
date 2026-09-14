using System;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.Core.Tests.SqlMemory.SqlMemoryTestData;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlCaptureCommitterTests
{
    [Fact]
    public async Task RetryingSameCaptureDoesNotDuplicateExecution()
    {
        var store = new RecordingSqlHistoryStore();
        var committer = new SqlCaptureCommitter(store, new SqlCapturePlanner());
        var capture = Capture(kind: SqlCaptureKind.BeforeExecute);
        await committer.ProcessAsync(capture, Policy, CancellationToken.None);
        await committer.ProcessAsync(capture, Policy, CancellationToken.None);
        Assert.Single(store.Writes);
        Assert.Single(store.Contents);
    }

    [Fact]
    public async Task CompareAndSwapConflictRetriesWithoutPartialWrite()
    {
        var store = new RecordingSqlHistoryStore { ConflictsRemaining = 2 };
        var committer = new SqlCaptureCommitter(store, new SqlCapturePlanner());
        await committer.ProcessAsync(Capture(), Policy, CancellationToken.None);
        Assert.Equal(3, store.CommitAttempts);
        Assert.Single(store.Writes);
    }

    [Fact]
    public async Task PersistentConflictIsBoundedAndVisible()
    {
        var store = new RecordingSqlHistoryStore { ConflictsRemaining = 10 };
        var committer = new SqlCaptureCommitter(store, new SqlCapturePlanner(), 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => committer.ProcessAsync(Capture(), Policy, CancellationToken.None));
        Assert.Equal(2, store.CommitAttempts);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task BusyStorageBacksOffAndRetriesTheSameCapture()
    {
        var store = new RecordingSqlHistoryStore();
        store.CommitFailures.Enqueue(Busy());
        store.CommitFailures.Enqueue(Busy());
        var committer = new SqlCaptureCommitter(store, new SqlCapturePlanner(), leaseId: () => "lease-1",
            busyDelays: new[] { TimeSpan.Zero, TimeSpan.Zero });
        await committer.ProcessAsync(Capture(kind: SqlCaptureKind.BeforeExecute), Policy, CancellationToken.None);
        Assert.Equal(3, store.CommitAttempts);
        Assert.Single(store.Writes);
        Assert.Equal("lease-1", Assert.Single(store.LeaseIds));
    }

    [Fact]
    public async Task BusyRetriesAreBoundedAndOtherKindsAreNeverRetried()
    {
        var store = new RecordingSqlHistoryStore();
        for (var i = 0; i < 5; i++) store.CommitFailures.Enqueue(Busy());
        var committer = new SqlCaptureCommitter(store, new SqlCapturePlanner(), busyDelays: new[] { TimeSpan.Zero, TimeSpan.Zero });
        var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() => committer.ProcessAsync(Capture(), Policy, CancellationToken.None));
        Assert.True(error.IsTransient);
        Assert.Equal(3, store.CommitAttempts);

        var corrupt = new RecordingSqlHistoryStore
        {
            CommitException = new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Corrupt, "測試：頁面損毀", 11),
        };
        committer = new SqlCaptureCommitter(corrupt, new SqlCapturePlanner(), busyDelays: new[] { TimeSpan.Zero });
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => committer.ProcessAsync(Capture(), Policy, CancellationToken.None));
        Assert.Equal(1, corrupt.CommitAttempts);
    }

    [Fact]
    public async Task StorageFailureAndCancellationAreNotSwallowed()
    {
        var failure = new InvalidOperationException("測試：磁碟不可用");
        var store = new RecordingSqlHistoryStore { CommitException = failure };
        var committer = new SqlCaptureCommitter(store, new SqlCapturePlanner());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => committer.ProcessAsync(Capture(), Policy, CancellationToken.None)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => committer.ProcessAsync(Capture(), Policy, cancellation.Token));
        Assert.Empty(store.Writes);
    }

    private static SqlMemoryStorageException Busy() =>
        new(SqlMemoryStorageErrorKind.Busy, "測試：資料庫忙碌", 5, 5, "SqliteException");
}
