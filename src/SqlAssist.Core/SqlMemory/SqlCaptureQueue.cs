using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

public enum SqlCaptureEnqueueResult { Accepted, Coalesced, QueueFull, SnapshotTooLarge, Stopped, Stale }

/// <summary>
/// 單一背景 consumer；熱路徑不展開 SQL、不雜湊、不做 I/O。
/// 宿主必須觀察 Completion、enqueue 回傳值與暫時失敗回呼，並在卸載時 await CompleteAsync。
/// </summary>
/// <remarks>
/// 儲存忙碌由 processor 有界重試；重試用盡只放棄那一筆並回呼，writer 繼續處理後續擷取。
/// 其他失敗（損毀、不相容、未知）才讓 Completion fault 並停止接受。
/// </remarks>
public sealed class SqlCaptureQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<PendingCapture> _queue = new();
    private readonly Dictionary<Guid, LinkedListNode<PendingCapture>> _drafts = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SqlCaptureCommitter _processor;
    private readonly Action<SqlCapture, SqlMemoryStorageException>? _transientFailure;
    private readonly int _maximumPendingCount;
    private readonly long _maximumPendingTextBytes;
    private TaskCompletionSource<bool> _idle = Completed();
    private bool _accepting = true;
    private int _pendingCount;
    private long _pendingTextBytes;
    private int _droppedCount;

    /// <param name="transientFailure">
    /// 忙碌重試用盡、該筆未保存時在 consumer 執行緒呼叫；不得擲出，否則 writer 會視為致命失敗。
    /// </param>
    public SqlCaptureQueue(SqlCaptureCommitter processor, int maximumPendingCount, long maximumPendingTextBytes,
        Action<SqlCapture, SqlMemoryStorageException>? transientFailure = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        if (maximumPendingCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingCount));
        if (maximumPendingTextBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingTextBytes));
        _maximumPendingCount = maximumPendingCount;
        _maximumPendingTextBytes = maximumPendingTextBytes;
        _transientFailure = transientFailure;
        Completion = Task.Run(ConsumeAsync);
    }

    public Task Completion { get; }
    public int PendingCount { get { lock (_gate) return _pendingCount; } }
    public long PendingTextBytes { get { lock (_gate) return _pendingTextBytes; } }

    /// <summary>因儲存持續忙碌而放棄的擷取筆數。</summary>
    public int DroppedCount { get { lock (_gate) return _droppedCount; } }

    public SqlCaptureEnqueueResult TryEnqueue(SqlCapture capture, SqlCapturePolicy policy)
    {
        if (capture == null) throw new ArgumentNullException(nameof(capture));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        lock (_gate)
        {
            if (!_accepting) return SqlCaptureEnqueueResult.Stopped;
            if (capture.EstimatedTextBytes > _maximumPendingTextBytes) return SqlCaptureEnqueueResult.SnapshotTooLarge;
            LinkedListNode<PendingCapture>? replaced = null;
            if (capture.Kind == SqlCaptureKind.DraftIdle && _drafts.TryGetValue(capture.Session.SessionId, out replaced))
            {
                if (capture.Sequence <= replaced.Value.Capture.Sequence) return SqlCaptureEnqueueResult.Stale;
            }
            var oldBytes = replaced?.Value.Capture.EstimatedTextBytes ?? 0;
            if ((replaced == null && _pendingCount >= _maximumPendingCount) ||
                capture.EstimatedTextBytes > _maximumPendingTextBytes - (_pendingTextBytes - oldBytes))
                return SqlCaptureEnqueueResult.QueueFull;

            if (replaced != null) _queue.Remove(replaced);
            else if (_pendingCount++ == 0) _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTextBytes += capture.EstimatedTextBytes - oldBytes;
            var node = _queue.AddLast(new PendingCapture(capture, policy));
            if (capture.Kind == SqlCaptureKind.DraftIdle) _drafts[capture.Session.SessionId] = node;
            else _drafts.Remove(capture.Session.SessionId);
            // Execute／Close 是合併屏障，不能拿後來的 draft 替換它們之前的快照。
            if (replaced == null) _signal.Release();
            return replaced == null ? SqlCaptureEnqueueResult.Accepted : SqlCaptureEnqueueResult.Coalesced;
        }
    }

    /// <summary>
    /// 等到目前沒有待處理或處理中的擷取；writer 停止時也完成。不阻止之後的新擷取，
    /// 只讓手動整理這類長操作不必把已接受的內容擱在記憶體裡。
    /// </summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        if (idle.IsCompleted || !cancellationToken.CanBeCanceled) return idle;
        return WaitAsync(idle, cancellationToken);

        static async Task WaitAsync(Task idle, CancellationToken cancellationToken)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken)))
                await (await Task.WhenAny(idle, canceled.Task).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    /// <summary>停止接收並排空已接受的工作。失敗時 Task 保留原例外，未完成項目不宣稱已保存。</summary>
    public Task CompleteAsync()
    {
        lock (_gate)
        {
            if (_accepting)
            {
                _accepting = false;
                _signal.Release();
            }
            return Completion;
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync().ConfigureAwait(false);
                PendingCapture pending;
                lock (_gate)
                {
                    var first = _queue.First;
                    if (first == null)
                    {
                        if (!_accepting) return;
                        continue;
                    }
                    pending = first.Value;
                    _queue.RemoveFirst();
                    if (_drafts.TryGetValue(pending.Capture.Session.SessionId, out var draft) && ReferenceEquals(draft, first))
                        _drafts.Remove(pending.Capture.Session.SessionId);
                }
                SqlMemoryStorageException? dropped = null;
                try
                {
                    await _processor.ProcessAsync(pending.Capture, pending.Policy, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SqlMemoryStorageException error) when (error.IsTransient)
                {
                    // processor 已退避重試；再等下去會讓整個佇列卡在一筆上，下一次擷取仍會帶著完整內容重來。
                    dropped = error;
                }
                if (dropped != null)
                {
                    lock (_gate) _droppedCount++;
                    _transientFailure?.Invoke(pending.Capture, dropped);
                }
                lock (_gate)
                {
                    // 執行中的快照也計入上限，避免 consumer 取走巨量 SQL 後又收滿一整批。
                    _pendingCount--;
                    _pendingTextBytes -= pending.Capture.EstimatedTextBytes;
                    if (_pendingCount == 0) _idle.TrySetResult(true);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _accepting = false;
                _queue.Clear();
                _drafts.Clear();
                _pendingCount = 0;
                _pendingTextBytes = 0;
                _idle.TrySetResult(true);
                _signal.Dispose();
            }
        }
    }

    private static TaskCompletionSource<bool> Completed()
    {
        var completed = new TaskCompletionSource<bool>();
        completed.SetResult(true);
        return completed;
    }

    private sealed record PendingCapture(SqlCapture Capture, SqlCapturePolicy Policy);
}
