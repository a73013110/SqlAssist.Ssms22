using System;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using Xunit;
using static SqlAssist.Core.Tests.QueryMemory.QueryMemoryTestData;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryMemoryProcessorTests
{
    [Fact]
    public async Task RetryingSameCaptureDoesNotDuplicateExecution()
    {
        var repository = new RecordingQueryMemoryRepository();
        var processor = new QueryMemoryProcessor(repository, new QueryRevisionEngine());
        var capture = Capture(kind: QueryCaptureKind.BeforeExecute);
        await processor.ProcessAsync(capture, Policy, CancellationToken.None);
        await processor.ProcessAsync(capture, Policy, CancellationToken.None);
        Assert.Single(repository.Writes);
        Assert.Single(repository.Contents);
    }

    [Fact]
    public async Task CompareAndSwapConflictRetriesWithoutPartialWrite()
    {
        var repository = new RecordingQueryMemoryRepository { ConflictsRemaining = 2 };
        var processor = new QueryMemoryProcessor(repository, new QueryRevisionEngine());
        await processor.ProcessAsync(Capture(), Policy, CancellationToken.None);
        Assert.Equal(3, repository.CommitAttempts);
        Assert.Single(repository.Writes);
    }

    [Fact]
    public async Task PersistentConflictIsBoundedAndVisible()
    {
        var repository = new RecordingQueryMemoryRepository { ConflictsRemaining = 10 };
        var processor = new QueryMemoryProcessor(repository, new QueryRevisionEngine(), 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Capture(), Policy, CancellationToken.None));
        Assert.Equal(2, repository.CommitAttempts);
        Assert.Empty(repository.Writes);
    }

    [Fact]
    public async Task StorageFailureAndCancellationAreNotSwallowed()
    {
        var failure = new InvalidOperationException("測試：磁碟不可用");
        var repository = new RecordingQueryMemoryRepository { CommitException = failure };
        var processor = new QueryMemoryProcessor(repository, new QueryRevisionEngine());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Capture(), Policy, CancellationToken.None)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(Capture(), Policy, cancellation.Token));
        Assert.Empty(repository.Writes);
    }
}
