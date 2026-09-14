using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using Xunit;
using static SqlAssist.Core.Tests.QueryMemory.QueryMemoryTestData;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryBackgroundWriterTests
{
    [Fact]
    public async Task EnqueueNeverMaterializesSnapshotOnCallerThread()
    {
        var repository = new RecordingQueryMemoryRepository();
        var writer = CreateWriter(repository);
        using var enqueuing = new ThreadLocal<bool>(() => false);
        var snapshot = new ThreadCheckingSnapshot(enqueuing);
        var capture = new QueryMemoryCapture(Guid.NewGuid(), Document, Session, 1, Start, QueryCaptureKind.DraftIdle, snapshot);
        enqueuing.Value = true;
        Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(capture, Policy));
        enqueuing.Value = false;
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(snapshot.Read);
        Assert.Single(repository.Writes);
        Assert.Equal(0, writer.PendingCount);
    }

    [Fact]
    public async Task DraftsCoalesceButCannotCrossExecutionBarrier()
    {
        var entered = Signal();
        var release = Signal();
        var repository = BlockFirstRead(entered, release);
        var writer = CreateWriter(repository);
        try
        {
            writer.TryEnqueue(Capture(), Policy);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(Capture(2), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.Coalesced, writer.TryEnqueue(Capture(3), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.Stale, writer.TryEnqueue(Capture(2), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(Capture(4, kind: QueryCaptureKind.BeforeExecute), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(Capture(5), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.Coalesced, writer.TryEnqueue(Capture(6), Policy));
            Assert.Equal(4, writer.PendingCount);
        }
        finally
        {
            release.TrySetResult(true);
            await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.Equal(new long[] { 1, 3, 4, 6 }, repository.Writes.Select(w => w.State.LastSequence));
        Assert.Single(repository.Writes, w => w.Execution != null);
    }

    [Fact]
    public async Task CapacityIncludesInFlightTextAndRejectsWithoutDroppingAcceptedWork()
    {
        var entered = Signal();
        var release = Signal();
        var repository = BlockFirstRead(entered, release);
        var writer = CreateWriter(repository, 2, 40);
        try
        {
            Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(Capture(text: "SELECT 1"), Policy));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(Capture(2, "SELECT 2"), Policy));
            Assert.Equal(32, writer.PendingTextBytes);
            Assert.Equal(QueryMemoryEnqueueResult.QueueFull, writer.TryEnqueue(Capture(3, "SELECT 3", QueryCaptureKind.BeforeExecute), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.QueueFull, writer.TryEnqueue(Capture(3, new string('x', 13)), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.SnapshotTooLarge, writer.TryEnqueue(Capture(3, new string('x', 21)), Policy));
            Assert.Equal(QueryMemoryEnqueueResult.Coalesced, writer.TryEnqueue(Capture(3, "SELECT 3"), Policy));
        }
        finally
        {
            release.TrySetResult(true);
            await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.Equal(new long[] { 1, 3 }, repository.Writes.Select(w => w.State.LastSequence));
        Assert.Equal(0, writer.PendingTextBytes);
    }

    [Fact]
    public async Task CompletionDrainsAndRepeatedShutdownIsSafe()
    {
        var repository = new RecordingQueryMemoryRepository();
        var writer = CreateWriter(repository);
        writer.TryEnqueue(Capture(kind: QueryCaptureKind.BeforeExecute), Policy);
        writer.TryEnqueue(Capture(2, kind: QueryCaptureKind.EditorClosed), Policy);
        var completion = writer.CompleteAsync();
        Assert.Equal(QueryMemoryEnqueueResult.Stopped, writer.TryEnqueue(Capture(3), Policy));
        Assert.Same(completion, writer.CompleteAsync());
        await completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, repository.Writes.Count);
        Assert.True(repository.Writes[1].DeleteRecovery);
        await writer.CompleteAsync();
    }

    [Fact]
    public async Task EmptyWriterCanStopWithoutHanging()
    {
        var writer = CreateWriter(new RecordingQueryMemoryRepository());
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailureFaultsCompletionAndStopsAccepting()
    {
        var failure = new InvalidOperationException("測試：儲存失敗");
        var repository = new RecordingQueryMemoryRepository { CommitException = failure };
        var writer = CreateWriter(repository);
        writer.TryEnqueue(Capture(), Policy);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => writer.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        Assert.Equal(QueryMemoryEnqueueResult.Stopped, writer.TryEnqueue(Capture(2), Policy));
        Assert.Empty(repository.Writes);
        Assert.Equal(0, writer.PendingCount);
    }

    [Fact]
    public async Task TransientFailureDropsOnlyThatCaptureAndWriterKeepsRunning()
    {
        var repository = new RecordingQueryMemoryRepository();
        // 一次原始嘗試加一次重試都忙碌：第一筆放棄，之後的擷取照常保存。
        repository.CommitFailures.Enqueue(Busy());
        repository.CommitFailures.Enqueue(Busy());
        var dropped = new System.Collections.Concurrent.ConcurrentQueue<(QueryMemoryCapture Capture, QueryMemoryStorageException Error)>();
        var writer = new QueryMemoryBackgroundWriter(
            new QueryMemoryProcessor(repository, new QueryRevisionEngine(), busyDelays: new[] { TimeSpan.Zero }),
            10, 10000, (capture, error) => dropped.Enqueue((capture, error)));
        var first = Capture(kind: QueryCaptureKind.BeforeExecute);
        writer.TryEnqueue(first, Policy);
        await writer.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(writer.Completion.IsCompleted);
        Assert.Equal(1, writer.DroppedCount);
        var (capture, error) = Assert.Single(dropped);
        Assert.Same(first, capture);
        Assert.Equal(5, error.ErrorCode);

        Assert.Equal(QueryMemoryEnqueueResult.Accepted, writer.TryEnqueue(Capture(2, kind: QueryCaptureKind.BeforeExecute), Policy));
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(repository.Writes).State.LastSequence);
        Assert.Equal(0, writer.PendingCount);
    }

    [Fact]
    public async Task FatalStorageKindsStillFaultTheWriter()
    {
        var failure = new QueryMemoryStorageException(QueryMemoryStorageErrorKind.Corrupt, "測試：資料庫損毀", 11);
        var repository = new RecordingQueryMemoryRepository { CommitException = failure };
        var calls = 0;
        var writer = new QueryMemoryBackgroundWriter(
            new QueryMemoryProcessor(repository, new QueryRevisionEngine(), busyDelays: new[] { TimeSpan.Zero }),
            10, 10000, (_, _) => Interlocked.Increment(ref calls));
        writer.TryEnqueue(Capture(), Policy);
        Assert.Same(failure, await Assert.ThrowsAsync<QueryMemoryStorageException>(() => writer.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        Assert.Equal(QueryMemoryEnqueueResult.Stopped, writer.TryEnqueue(Capture(2), Policy));
        Assert.Equal(1, repository.CommitAttempts);
        Assert.Equal(0, calls);
        // 停止後等待閒置必須立即完成，手動整理不能卡在已經 fault 的 writer 上。
        await writer.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForIdleCoversInFlightWorkAndHonorsCancellation()
    {
        var entered = Signal();
        var release = Signal();
        var writer = CreateWriter(BlockFirstRead(entered, release));
        await writer.WaitForIdleAsync(TestContext.Current.CancellationToken);
        writer.TryEnqueue(Capture(), Policy);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var idle = writer.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.False(idle.IsCompleted);
        using (var cancellation = new CancellationTokenSource())
        {
            var canceled = writer.WaitForIdleAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        }
        release.TrySetResult(true);
        await idle.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static QueryMemoryStorageException Busy() =>
        new(QueryMemoryStorageErrorKind.Busy, "測試：資料庫忙碌", 5, 5, "SqliteException");

    private static QueryMemoryBackgroundWriter CreateWriter(RecordingQueryMemoryRepository repository, int count = 10, long bytes = 10000) =>
        new(new QueryMemoryProcessor(repository, new QueryRevisionEngine()), count, bytes);

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RecordingQueryMemoryRepository BlockFirstRead(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release)
    {
        var count = 0;
        return new RecordingQueryMemoryRepository
        {
            BeforeRead = async () =>
            {
                if (Interlocked.Increment(ref count) != 1) return;
                entered.TrySetResult(true);
                await release.Task;
            },
        };
    }

    private sealed class ThreadCheckingSnapshot(ThreadLocal<bool> enqueuing) : IQueryTextSnapshot
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
