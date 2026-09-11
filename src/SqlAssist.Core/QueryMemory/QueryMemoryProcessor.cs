using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

/// <summary>交易衝突只重讀並重建計畫；I/O 失敗交給宿主呈現，不假裝已保存。</summary>
public sealed class QueryMemoryProcessor
{
    private readonly IQueryMemoryRepository _repository;
    private readonly QueryRevisionEngine _engine;
    private readonly int _maximumAttempts;

    public QueryMemoryProcessor(IQueryMemoryRepository repository, QueryRevisionEngine engine, int maximumAttempts = 3)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        _maximumAttempts = maximumAttempts;
    }

    public async Task ProcessAsync(QueryMemoryCapture capture, QueryMemoryPolicy policy, CancellationToken cancellationToken)
    {
        if (capture == null) throw new ArgumentNullException(nameof(capture));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        for (var attempt = 0; attempt < _maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = await _repository.ReadSessionAsync(capture.Session.SessionId, cancellationToken).ConfigureAwait(false);
            var write = _engine.Prepare(capture, previous, policy);
            if (write == null) return;
            var result = await _repository.CommitAsync(write, cancellationToken).ConfigureAwait(false);
            if (result == QueryMemoryCommitResult.Committed || result == QueryMemoryCommitResult.AlreadyCommitted) return;
            if (result != QueryMemoryCommitResult.Conflict) throw new InvalidOperationException("未知的 Query Memory 交易結果。");
        }
        throw new InvalidOperationException("Query Memory 交易持續衝突，尚未保存擷取內容。");
    }
}
