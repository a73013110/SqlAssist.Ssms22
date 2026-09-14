using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 交易衝突重讀並重建計畫；儲存忙碌以有界退避重試。其餘 I/O 失敗交給呼叫端，不假裝已保存。
/// </summary>
/// <remarks>
/// 退避在 repository 呼叫之間等待，不持有任何宿主或隔離層閘門：手動整理、UI 讀取與維護
/// 在這段時間仍可執行。CaptureId 冪等，忙碌後重送不會重複寫入。
/// </remarks>
public sealed class SqlCaptureCommitter
{
    /// <summary>每次嘗試本身已含儲存層的 busy timeout；退避總和約 15 秒，涵蓋其他程序的整理或長交易。</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultBusyDelays = Array.AsReadOnly(new[]
    {
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    });

    private readonly ISqlHistoryStore _repository;
    private readonly SqlCapturePlanner _engine;
    private readonly int _maximumAttempts;
    private readonly Func<string?> _leaseId;
    private readonly IReadOnlyList<TimeSpan> _busyDelays;

    /// <param name="maximumAttempts">CAS 衝突的嘗試上限。</param>
    /// <param name="leaseId">每次提交當下讀取的程序租約；租約可能被回收或重開，不在建構時固定。</param>
    /// <param name="busyDelays">每次忙碌後的等待；筆數就是忙碌重試上限。</param>
    public SqlCaptureCommitter(ISqlHistoryStore repository, SqlCapturePlanner engine, int maximumAttempts = 3,
        Func<string?>? leaseId = null, IReadOnlyList<TimeSpan>? busyDelays = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        _maximumAttempts = maximumAttempts;
        _leaseId = leaseId ?? (() => null);
        _busyDelays = busyDelays ?? DefaultBusyDelays;
        foreach (var delay in _busyDelays)
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(busyDelays));
    }

    public async Task ProcessAsync(SqlCapture capture, SqlCapturePolicy policy, CancellationToken cancellationToken)
    {
        if (capture == null) throw new ArgumentNullException(nameof(capture));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        var conflicts = 0;
        var busy = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            try
            {
                var previous = await _repository.ReadSessionAsync(capture.Session.SessionId, cancellationToken).ConfigureAwait(false);
                var write = _engine.Prepare(capture, previous, policy);
                if (write == null) return;
                var result = await _repository.CommitAsync(write, _leaseId(), cancellationToken).ConfigureAwait(false);
                if (result == SqlHistoryCommitResult.Committed || result == SqlHistoryCommitResult.AlreadyCommitted) return;
                if (result != SqlHistoryCommitResult.Conflict) throw new InvalidOperationException("未知的 SQL Memory 交易結果。");
                if (++conflicts >= _maximumAttempts)
                    throw new InvalidOperationException("SQL Memory 交易持續衝突，尚未保存擷取內容。");
                continue;
            }
            catch (SqlMemoryStorageException error) when (error.IsTransient && busy < _busyDelays.Count)
            {
                delay = _busyDelays[busy++];
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
