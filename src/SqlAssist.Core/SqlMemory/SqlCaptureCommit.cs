using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.SqlMemory;

/// <summary>由版本引擎產生的不可變交易單位，不把儲存層的 SQL 或 provider 暴露給核心。</summary>
[Serializable]
public sealed class SqlCaptureCommit
{
    internal SqlCaptureCommit(SqlCapture capture, long? expectedVersion,
        SqlSessionHead state, IEnumerable<SqlContent> contents, IEnumerable<SqlRevision> revisions,
        SqlRecovery? recovery, bool deleteRecovery, SqlExecution? execution)
    {
        CaptureId = capture.CaptureId;
        Document = capture.Document;
        Connection = capture.Connection;
        ExpectedVersion = expectedVersion;
        State = state;
        Contents = Array.AsReadOnly(contents.ToArray());
        Revisions = Array.AsReadOnly(revisions.ToArray());
        Recovery = recovery;
        DeleteRecovery = deleteRecovery;
        Execution = execution;
    }

    public Guid CaptureId { get; }
    public SqlDocument Document { get; }

    /// <summary>擷取當下的連線；只寫進 History 投影，版本、執行與 Recovery 本身不保存連線。</summary>
    public SqlConnectionLabel? Connection { get; }
    public long? ExpectedVersion { get; }
    public SqlSessionHead State { get; }
    public IReadOnlyList<SqlContent> Contents { get; }
    public IReadOnlyList<SqlRevision> Revisions { get; }
    public SqlRecovery? Recovery { get; }
    public bool DeleteRecovery { get; }
    public SqlExecution? Execution { get; }
}
