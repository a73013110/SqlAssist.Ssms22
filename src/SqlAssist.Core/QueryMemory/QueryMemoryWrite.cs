using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.QueryMemory;

/// <summary>由版本引擎產生的不可變交易單位，不把儲存層的 SQL 或 provider 暴露給核心。</summary>
public sealed class QueryMemoryWrite
{
    internal QueryMemoryWrite(QueryMemoryCapture capture, long? expectedVersion,
        QuerySessionState state, IEnumerable<QueryContent> contents, IEnumerable<QueryRevision> revisions,
        QueryRecoverySnapshot? recovery, bool deleteRecovery, QueryExecutionEvent? execution)
    {
        CaptureId = capture.CaptureId;
        Document = capture.Document;
        ExpectedVersion = expectedVersion;
        State = state;
        Contents = Array.AsReadOnly(contents.ToArray());
        Revisions = Array.AsReadOnly(revisions.ToArray());
        Recovery = recovery;
        DeleteRecovery = deleteRecovery;
        Execution = execution;
    }

    public Guid CaptureId { get; }
    public QueryDocument Document { get; }
    public long? ExpectedVersion { get; }
    public QuerySessionState State { get; }
    public IReadOnlyList<QueryContent> Contents { get; }
    public IReadOnlyList<QueryRevision> Revisions { get; }
    public QueryRecoverySnapshot? Recovery { get; }
    public bool DeleteRecovery { get; }
    public QueryExecutionEvent? Execution { get; }
}
