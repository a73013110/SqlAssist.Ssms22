using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Core.Tests.SqlMemory;

// 僅作核心交易測試替身，不作正式 History storage 或 SQL 分頁驗證。
internal sealed class RecordingSqlHistoryStore : ISqlHistoryStore
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _captures = new();
    private readonly Dictionary<Guid, SqlSessionHead> _sessions = new();
    public List<SqlCaptureCommit> Writes { get; } = new();
    public Dictionary<string, SqlContent> Contents { get; } = new();
    public Func<Task>? BeforeRead { get; set; }
    public Exception? CommitException { get; set; }
    /// <summary>依序各擲出一次，用完後恢復正常提交。</summary>
    public Queue<Exception> CommitFailures { get; } = new();
    public List<string?> LeaseIds { get; } = new();
    public int ConflictsRemaining { get; set; }
    public int CommitAttempts { get; private set; }

    public async Task<SqlSessionHead?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeRead != null) await BeforeRead();
        lock (_gate) return _sessions.TryGetValue(sessionId, out var state) ? state : null;
    }

    public Task<SqlHistoryCommitResult> CommitAsync(SqlCaptureCommit write, string? leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            CommitAttempts++;
            if (CommitException != null) throw CommitException;
            if (CommitFailures.Count > 0) throw CommitFailures.Dequeue();
            if (_captures.Contains(write.CaptureId)) return Task.FromResult(SqlHistoryCommitResult.AlreadyCommitted);
            if (ConflictsRemaining > 0)
            {
                ConflictsRemaining--;
                return Task.FromResult(SqlHistoryCommitResult.Conflict);
            }
            _sessions.TryGetValue(write.State.Session.SessionId, out var previous);
            if (previous?.Version != write.ExpectedVersion) return Task.FromResult(SqlHistoryCommitResult.Conflict);
            foreach (var content in write.Contents) Contents[content.ContentId] = content;
            _sessions[write.State.Session.SessionId] = write.State;
            _captures.Add(write.CaptureId);
            Writes.Add(write);
            LeaseIds.Add(leaseId);
            return Task.FromResult(SqlHistoryCommitResult.Committed);
        }
    }

    public Task<SqlMemoryPage<SqlHistoryItem>> ReadHistoryAsync(SqlHistoryRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("測試替身不實作正式儲存層的查詢。");

    public Task<SqlHistoryDeleteResult> DeleteHistoryAsync(SqlHistoryItem item, CancellationToken cancellationToken) =>
        throw new NotSupportedException("測試替身不實作正式儲存層的刪除。");

    public Task<IReadOnlyList<string>> ReadConnectionFacetsAsync(SqlConnectionFacetRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("測試替身不實作正式儲存層的查詢。");

    public Task<SqlContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(Contents.TryGetValue(contentId, out var content) ? content : null);
    }
}
