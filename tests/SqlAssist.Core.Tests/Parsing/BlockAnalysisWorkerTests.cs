using System;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class BlockAnalysisWorkerTests
{
    [Fact]
    public async Task Debounce期間取消不會取得全文()
    {
        using var cancellation = new CancellationTokenSource();
        var read = false;
        var worker = new BlockAnalysisWorker();
        var task = worker.AnalyzeAsync(() => { read = true; return "BEGIN END"; }, 2000, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(read);
    }

    [Fact]
    public async Task 同份文件只允許一次分析且等待者可取消()
    {
        var worker = new BlockAnalysisWorker();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = worker.AnalyzeAsync(() =>
        {
            entered.SetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return "BEGIN END";
        }, 0, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var read = false;
            var second = worker.AnalyzeAsync(() => { read = true; return "()"; }, 0, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.False(read);
        }
        finally { release.Set(); }
        Assert.Single((await first).Pairs);
        Assert.Equal(BlockKind.Parenthesis, Assert.Single((await worker.AnalyzeAsync(() => "()", 0, CancellationToken.None)).Pairs).Kind);
    }

    [Fact]
    public async Task 已開始的過期解析不得回傳有效結果()
    {
        var worker = new BlockAnalysisWorker();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = worker.AnalyzeAsync(() =>
        {
            entered.SetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return "BEGIN END";
        }, 0, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancellation.Cancel();
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task 失敗後仍能繼續下一份快照()
    {
        var worker = new BlockAnalysisWorker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.AnalyzeAsync(
            () => throw new InvalidOperationException("測試失敗路徑"), 0, CancellationToken.None));
        Assert.Single((await worker.AnalyzeAsync(() => "BEGIN END", 0, CancellationToken.None)).Pairs);
    }
}
