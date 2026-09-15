using System;

namespace SqlAssist.Core.SqlMemory;

public enum SqlRevisionReason
{
    AutoCheckpoint,
    EditorClosed,
    BeforeExecute,
    FavoriteEdit,
}

public enum SqlCaptureKind { DraftIdle, BeforeExecute, EditorClosed }
public enum SqlExecutionScope { Document, Selection }
public enum SqlFavoriteScope { Global, Server, Database }
public enum SqlHistoryFilter { All, Executions, Drafts }

// 文件身分不包含連線；同一檔案可以在不同頁籤與資料庫中工作。
[Serializable]
public sealed record SqlDocument(Guid DocumentId, string DisplayName, string? FilePath);
[Serializable]
public sealed record SqlSession(Guid SessionId, Guid DocumentId, DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt = null);

[Serializable]
public sealed record SqlConnectionLabel(string Server, string Database);

[Serializable]
public sealed record SqlRevision(Guid RevisionId, Guid? ParentRevisionId, string ContentId,
    Guid SessionId, DateTimeOffset CreatedAt, SqlRevisionReason Reason,
    SqlConnectionLabel? Connection, bool IsExecutionSelection = false);

[Serializable]
public sealed record SqlExecution(Guid ExecutionId, Guid RevisionId, DateTimeOffset ExecutedAt,
    SqlConnectionLabel? Connection, SqlExecutionScope Scope);

[Serializable]
public sealed record SqlRecovery(Guid SessionId, string ContentId, long Sequence,
    DateTimeOffset CapturedAt, SqlConnectionLabel? Connection);

[Serializable]
public sealed record SqlFavorite(Guid FavoriteId, string Name, string? Description,
    Guid CurrentRevisionId, SqlFavoriteScope Scope, SqlConnectionLabel? Connection);

/// <summary>儲存層讀出的 Session 投影；Version 是交易 CAS，不是 UI 的文字版本。</summary>
/// <remarks>
/// <paramref name="RecoveryContentId"/> 是目前 Recovery 指向的內容位址（沒有 Recovery 時為 null），
/// 讓引擎在準備下一筆擷取時不必先讀 Recovery 資料表即可判斷內容是否真的變了。
/// </remarks>
[Serializable]
public sealed record SqlSessionHead(SqlSession Session, long Version, long LastSequence,
    SqlRevision? LatestRevision, SqlRevision? LatestExecutionRevision = null, string? RecoveryContentId = null);
