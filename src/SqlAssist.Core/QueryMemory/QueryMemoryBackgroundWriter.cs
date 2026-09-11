using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

public enum QueryMemoryEnqueueResult { Accepted, Coalesced, QueueFull, SnapshotTooLarge, Stopped, Stale }

/// <summary>
/// 單一背景 consumer；熱路徑不展開 SQL、不雜湊、不做 I/O。
/// 宿主必須觀察 Completion 與 enqueue 回傳值，並在卸載時 await CompleteAsync。
/// </summary>
public sealed class QueryMemoryBackgroundWriter
{
    private readonly object _gate = new();
    private readonly LinkedList<PendingCapture> _queue = new();
    private readonly Dictionary<Guid, LinkedListNode<PendingCapture>> _drafts = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly QueryMemoryProcessor _processor;
    private readonly int _maximumPendingCount;
    private readonly long _maximumPendingTextBytes;
    private bool _accepting = true;
    private int _pendingCount;
    private long _pendingTextBytes;

    public QueryMemoryBackgroundWriter(QueryMemoryProcessor processor, int maximumPendingCount, long maximumPendingTextBytes)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        if (maximumPendingCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingCount));
        if (maximumPendingTextBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingTextBytes));
        _maximumPendingCount = maximumPendingCount;
        _maximumPendingTextBytes = maximumPendingTextBytes;
        Completion = Task.Run(ConsumeAsync);
    }

    public Task Completion { get; }
    public int PendingCount { get { lock (_gate) return _pendingCount; } }
    public long PendingTextBytes { get { lock (_gate) return _pendingTextBytes; } }

    public QueryMemoryEnqueueResult TryEnqueue(QueryMemoryCapture capture, QueryMemoryPolicy policy)
    {
        if (capture == null) throw new ArgumentNullException(nameof(capture));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        lock (_gate)
        {
            if (!_accepting) return QueryMemoryEnqueueResult.Stopped;
            if (capture.EstimatedTextBytes > _maximumPendingTextBytes) return QueryMemoryEnqueueResult.SnapshotTooLarge;
            LinkedListNode<PendingCapture>? replaced = null;
            if (capture.Kind == QueryCaptureKind.DraftIdle && _drafts.TryGetValue(capture.Session.SessionId, out replaced))
            {
                if (capture.Sequence <= replaced.Value.Capture.Sequence) return QueryMemoryEnqueueResult.Stale;
            }
            var oldBytes = replaced?.Value.Capture.EstimatedTextBytes ?? 0;
            if ((replaced == null && _pendingCount >= _maximumPendingCount) ||
                capture.EstimatedTextBytes > _maximumPendingTextBytes - (_pendingTextBytes - oldBytes))
                return QueryMemoryEnqueueResult.QueueFull;

            if (replaced != null) _queue.Remove(replaced);
            else _pendingCount++;
            _pendingTextBytes += capture.EstimatedTextBytes - oldBytes;
            var node = _queue.AddLast(new PendingCapture(capture, policy));
            if (capture.Kind == QueryCaptureKind.DraftIdle) _drafts[capture.Session.SessionId] = node;
            else _drafts.Remove(capture.Session.SessionId);
            // Execute／Close／Manual 是合併屏障，不能拿後來的 draft 替換它們之前的快照。
            if (replaced == null) _signal.Release();
            return replaced == null ? QueryMemoryEnqueueResult.Accepted : QueryMemoryEnqueueResult.Coalesced;
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
                await _processor.ProcessAsync(pending.Capture, pending.Policy, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    // 執行中的快照也計入上限，避免 consumer 取走巨量 SQL 後又收滿一整批。
                    _pendingCount--;
                    _pendingTextBytes -= pending.Capture.EstimatedTextBytes;
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
                _signal.Dispose();
            }
        }
    }

    private sealed record PendingCapture(QueryMemoryCapture Capture, QueryMemoryPolicy Policy);
}
