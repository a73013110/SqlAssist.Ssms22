using System;

namespace SqlAssist.Core.QueryMemory;

public enum QueryRevisionReason
{
    AutoCheckpoint,
    EditorClosed,
    BeforeExecute,
    ManualSnapshot,
    SavedQueryEdit,
    Recovery,
}

public enum QueryCaptureKind { DraftIdle, BeforeExecute, EditorClosed, ManualSnapshot, Recovery }
public enum QueryExecutionScope { Document, Selection }
public enum QueryExecutionStatus { Unknown, Submitted }
public enum SavedQueryScope { Global, Server, Database }
public enum QueryHistoryKind { All, Executed, Drafts, Pinned }

// 文件身分不包含連線；同一檔案可以在不同頁籤與資料庫中工作。
public sealed record QueryDocument(Guid DocumentId, string DisplayName, string? FilePath);
public sealed record QuerySession(Guid SessionId, Guid DocumentId, DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt = null);

/// <summary>Identity 只放不含密碼／Token 的識別值，禁止傳入完整連線字串。</summary>
public sealed record QueryConnectionContext(string Server, string Database, string? ConnectionIdentity = null);

public sealed record QueryRevision(Guid RevisionId, Guid? ParentRevisionId, string ContentId,
    Guid SessionId, DateTimeOffset CreatedAt, QueryRevisionReason Reason,
    QueryConnectionContext? Connection, bool IsExecutionSelection = false);

public sealed record QueryExecutionEvent(Guid ExecutionId, Guid RevisionId, DateTimeOffset ExecutedAt,
    QueryConnectionContext? Connection, QueryExecutionScope Scope,
    QueryExecutionStatus Status = QueryExecutionStatus.Submitted, TimeSpan? Duration = null);

public sealed record QueryRecoverySnapshot(Guid SessionId, string ContentId, long Sequence,
    DateTimeOffset CapturedAt, QueryConnectionContext? Connection);

public sealed record SavedQuery(Guid SavedQueryId, string Name, string? Description,
    Guid CurrentRevisionId, SavedQueryScope Scope, QueryConnectionContext? Connection, bool Pinned);

/// <summary>儲存層讀出的 Session 投影；Version 是交易 CAS，不是 UI 的文字版本。</summary>
public sealed record QuerySessionState(QuerySession Session, long Version, long LastSequence,
    QueryRevision? LatestRevision, QueryRevision? LatestExecutionRevision = null);
