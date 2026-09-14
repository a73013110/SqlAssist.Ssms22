using System;

namespace SqlAssist.Core.QueryMemory;

public enum QueryRevisionReason
{
    AutoCheckpoint,
    EditorClosed,
    BeforeExecute,
    FavoriteQueryEdit,
}

public enum QueryCaptureKind { DraftIdle, BeforeExecute, EditorClosed }
public enum QueryExecutionScope { Document, Selection }
public enum FavoriteQueryScope { Global, Server, Database }
public enum QueryHistoryKind { All, Executed, Drafts }

// 文件身分不包含連線；同一檔案可以在不同頁籤與資料庫中工作。
[Serializable]
public sealed record QueryDocument(Guid DocumentId, string DisplayName, string? FilePath);
[Serializable]
public sealed record QuerySession(Guid SessionId, Guid DocumentId, DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt = null);

[Serializable]
public sealed record QueryConnectionContext(string Server, string Database);

[Serializable]
public sealed record QueryRevision(Guid RevisionId, Guid? ParentRevisionId, string ContentId,
    Guid SessionId, DateTimeOffset CreatedAt, QueryRevisionReason Reason,
    QueryConnectionContext? Connection, bool IsExecutionSelection = false);

[Serializable]
public sealed record QueryExecutionEvent(Guid ExecutionId, Guid RevisionId, DateTimeOffset ExecutedAt,
    QueryConnectionContext? Connection, QueryExecutionScope Scope);

[Serializable]
public sealed record QueryRecoverySnapshot(Guid SessionId, string ContentId, long Sequence,
    DateTimeOffset CapturedAt, QueryConnectionContext? Connection);

[Serializable]
public sealed record FavoriteQuery(Guid FavoriteQueryId, string Name, string? Description,
    Guid CurrentRevisionId, FavoriteQueryScope Scope, QueryConnectionContext? Connection);

/// <summary>儲存層讀出的 Session 投影；Version 是交易 CAS，不是 UI 的文字版本。</summary>
/// <remarks>
/// <paramref name="RecoveryContentId"/> 是目前 Recovery 指向的內容位址（沒有 Recovery 時為 null），
/// 讓引擎在準備下一筆擷取時不必先讀 Recovery 資料表即可判斷內容是否真的變了。
/// </remarks>
[Serializable]
public sealed record QuerySessionState(QuerySession Session, long Version, long LastSequence,
    QueryRevision? LatestRevision, QueryRevision? LatestExecutionRevision = null, string? RecoveryContentId = null);
