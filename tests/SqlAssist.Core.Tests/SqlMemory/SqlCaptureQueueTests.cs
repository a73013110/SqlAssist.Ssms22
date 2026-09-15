using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.Core.Tests.SqlMemory.SqlMemoryTestData;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlCaptureQueueTests
{
    [Fact]
    public async Task EnqueueNeverMaterializesSnapshotOnCallerThread()
    {
        var store = new RecordingSqlHistoryStore();
        var queue = CreateQueue(store);
        using var enqueuing = new ThreadLocal<bool>(() => false);
        var snapshot = new ThreadCheckingSnapshot(enqueuing);
        var capture = new SqlCapture(Guid.NewGuid(), Document, Session, 1, Start, SqlCaptureKind.DraftIdle, snapshot);
        enqueuing.Value = true;
        Assert.Equal(SqlCaptureEnqueueResult.Accepted, queue.TryEnqueue(capture, Policy));
        enqueuing.Value = false;
        await queue.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(snapshot.Read);
        Assert.Single(store.Writes);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task DraftsCoalesceButCannotCrossExecutionBarrier()
    {
        var entered = Signal();
        var release = Signal();
        var store = BlockFirstRead(entered, release);
        var queue = CreateQueue(store);
        try
        {
            queue.TryEnqueue(Capture(), Policy);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(SqlCaptureEnqueueResult.Accepted, queue.TryEnqueue(Capture(2), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.Coalesced, queue.TryEnqueue(Capture(3), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.Stale, queue.TryEnqueue(Capture(2), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.Accepted, queue.TryEnqueue(Capture(4, kind: SqlCaptureKind.BeforeExecute), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.Accepted, queue.TryEnqueue(Capture(5), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.Coalesced, queue.TryEnqueue(Capture(6), Policy));
            Assert.Equal(4, queue.PendingCount);
        }
        finally
        {
            release.TrySetResult(true);
            await queue.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.Equal(new long[] { 1, 3, 4, 6 }, store.Writes.Select(w => w.State.LastSequence));
        Assert.Single(store.Writes, w => w.Execution != null);
    }

    [Fact]
    public async Task CapacityIncludesInFlightTextAndRejectsWithoutDroppingAcceptedWork()
    {
        var entered = Signal();
        var release = Signal();
        var store = BlockFirstRead(entered, release);
        var queue = CreateQueue(store, 2, 40);
        try
        {
            Assert.Equal(SqlCaptureEnqueueResult.Accepted, queue.TryEnqueue(Capture(text: "SELECT 1"), Policy));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(SqlCaptureEnqueueResult.Accepted, queue.TryEnqueue(Capture(2, "SELECT 2"), Policy));
            Assert.Equal(32, queue.PendingTextBytes);
            Assert.Equal(SqlCaptureEnqueueResult.QueueFull, queue.TryEnqueue(Capture(3, "SELECT 3", SqlCaptureKind.BeforeExecute), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.QueueFull, queue.TryEnqueue(Capture(3, new string('x', 13)), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.SnapshotTooLarge, queue.TryEnqueue(Capture(3, new string('x', 21)), Policy));
            Assert.Equal(SqlCaptureEnqueueResult.Coalesced, queue.TryEnqueue(Capture(3, "SELECT 3"), Policy));
        }
        finally
        {
            release.TrySetResult(true);
            await queue.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.Equal(new long[] { 1, 3 }, store.Writes.Select(w => w.State.LastSequence));
        Assert.Equal(0, queue.PendingTextBytes);
    }

    [Fact]
    public async Task CompletionDrainsAndRepeatedShutdownIsSafe()
    {
        var store = new RecordingSqlHistoryStore();
        var queue = CreateQueue(store);
        queue.TryEnqueue(Capture(kind: SqlCaptureKind.BeforeExecute), Policy);
        queue.TryEnqueue(Capture(2, kind: SqlCaptureKind.EditorClosed), Policy);
        var completion = queue.CompleteAsync();
        Assert.Equal(SqlCaptureEnqueueResult.Stopped, queue.TryEnqueue(Capture(3), Policy));
        Assert.Same(completion, queue.CompleteAsync());
        await completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, store.Writes.Count);
        Assert.True(store.Writes[1].DeleteRecovery);
        await queue.CompleteAsync();
    }

    [Fact]
    public async Task EmptyWriterCanStopWithoutHanging()
    {
        var queue = CreateQueue(new RecordingSqlHistoryStore());
        await queue.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailureFaultsCompletionAndStopsAccepting()
    {
        var failure = new InvalidOperationException("測試：儲存失敗");
        var store = new RecordingSqlHistoryStore { CommitException = failure };
        var queue = CreateQueue(store);
        queue.TryEnqueue(Capture(), Policy);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => queue.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        Assert.Equal(SqlCaptureEnqueueResult.Stopped, queue.TryEnqueue(Capture(2), Policy));
        Assert.Empty(store.Writes);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task TransientFailureDropsOnlyThatCaptureAndWriterKeepsRunning()
    {
        var store = new RecordingSqlHistoryStore();
        // 一次原始嘗試加一次重試都忙碌：第一筆放棄，之後的擷取照常保存。
        store.CommitFailures.Enqueue(Busy());
        store.CommitFailures.Enqueue(Busy());
        var dropped = new System.Collections.Concurrent.ConcurrentQueue<(SqlCapture Capture, SqlMemoryStorageException Error)>();
        var queue = new SqlCaptureQueue(
            new SqlCaptureCommitter(store, new SqlCapturePlanner(), busyDelays: new[] { TimeSpan.Zero }),
            10, 10000, (capture, error) => dropped.Enqueue((capture, error)));
        var first = Capture(kind: SqlCaptureKind.BeforeExecute);
        queue.TryEnqueue(first, Policy);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(queue.Completion.IsCompleted);
        Assert.Equal(1, queue.DroppedCount);
        var (capture, error) = Assert.Single(dropped);
        Assert.Same(first, capture);
        Assert.Equal(5, error.ErrorCode);

        Assert.Equal(SqlCaptureEnqueueResult.Accepted, queue.TryEnqueue(Capture(2, kind: SqlCaptureKind.BeforeExecute), Policy));
        await queue.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(store.Writes).State.LastSequence);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task FatalStorageKindsStillFaultTheWriter()
    {
        var failure = new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Corrupt, "測試：資料庫損毀", 11);
        var store = new RecordingSqlHistoryStore { CommitException = failure };
        var calls = 0;
        var queue = new SqlCaptureQueue(
            new SqlCaptureCommitter(store, new SqlCapturePlanner(), busyDelays: new[] { TimeSpan.Zero }),
            10, 10000, (_, _) => Interlocked.Increment(ref calls));
        queue.TryEnqueue(Capture(), Policy);
        Assert.Same(failure, await Assert.ThrowsAsync<SqlMemoryStorageException>(() => queue.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        Assert.Equal(SqlCaptureEnqueueResult.Stopped, queue.TryEnqueue(Capture(2), Policy));
        Assert.Equal(1, store.CommitAttempts);
        Assert.Equal(0, calls);
        // 停止後等待閒置必須立即完成，手動整理不能卡在已經 fault 的 queue 上。
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForIdleCoversInFlightWorkAndHonorsCancellation()
    {
        var entered = Signal();
        var release = Signal();
        var queue = CreateQueue(BlockFirstRead(entered, release));
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        queue.TryEnqueue(Capture(), Policy);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var idle = queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.False(idle.IsCompleted);
        using (var cancellation = new CancellationTokenSource())
        {
            var canceled = queue.WaitForIdleAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        }
        release.TrySetResult(true);
        await idle.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await queue.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static SqlMemoryStorageException Busy() =>
        new(SqlMemoryStorageErrorKind.Busy, "測試：資料庫忙碌", 5, 5, "SqliteException");

    private static SqlCaptureQueue CreateQueue(RecordingSqlHistoryStore store, int count = 10, long bytes = 10000) =>
        new(new SqlCaptureCommitter(store, new SqlCapturePlanner()), count, bytes);

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RecordingSqlHistoryStore BlockFirstRead(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release)
    {
        var count = 0;
        return new RecordingSqlHistoryStore
        {
            BeforeRead = async () =>
            {
                if (Interlocked.Increment(ref count) != 1) return;
                entered.TrySetResult(true);
                await release.Task;
            },
        };
    }

    private sealed class ThreadCheckingSnapshot(ThreadLocal<bool> enqueuing) : ISqlTextSnapshot
    {
        public bool Read { get; private set; }
        public int Length => 8;
        public string GetText()
        {
            Assert.False(enqueuing.Value);
            Read = true;
            return "SELECT 1";
        }
    }
}
