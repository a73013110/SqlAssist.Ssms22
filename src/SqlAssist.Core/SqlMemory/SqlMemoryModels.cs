using System;

namespace SqlAssist.Core.SqlMemory;

public enum SqlRevisionReason
{
    AutoCheckpoint,
    EditorClosed,
    BeforeExecute,

    /// <summary>屬於某個收藏自己的版本；新增收藏與改收藏的 SQL 都用它。</summary>
    Favorite,
}

public enum SqlCaptureKind { DraftIdle, BeforeExecute, EditorClosed }
public enum SqlHistoryFilter { All, Executions, Drafts }

// 文件身分不包含連線；同一檔案可以在不同頁籤與資料庫中工作。
[Serializable]
public sealed record SqlDocument(Guid DocumentId, string DisplayName, string? FilePath);
[Serializable]
public sealed record SqlSession(Guid SessionId, Guid DocumentId, DateTimeOffset? ClosedAt = null);

[Serializable]
public sealed record SqlConnectionLabel(string Server, string Database);

/// <remarks>
/// 版本不帶連線：連線只屬於使用者看得到的 History 投影，收藏另有自己的標註。
/// <paramref name="SessionId"/> 只有擷取產生的版本才有。收藏自己建立的版本不屬於任何一次編輯器
/// 生命週期，也不該讓某個 Session 的配額決定它的去留，因此留空；儲存層以
/// 「有 Session 或有 Favorite」的檢查擋住兩者皆空的列。
/// </remarks>
[Serializable]
public sealed record SqlRevision(Guid RevisionId, Guid? ParentRevisionId, string ContentId,
    Guid? SessionId, DateTimeOffset CreatedAt, SqlRevisionReason Reason, bool IsExecutionSelection = false);

[Serializable]
public sealed record SqlExecution(Guid ExecutionId, Guid RevisionId, DateTimeOffset ExecutedAt);

[Serializable]
public sealed record SqlRecovery(Guid SessionId, string ContentId, DateTimeOffset CapturedAt);

/// <summary>收藏的使用者資料與目前版本引用。</summary>
/// <param name="Server">伺服器標註；null 表示不限。只供篩選，不是開啟 SQL 時的連線。</param>
/// <param name="Database">資料庫標註；與 <paramref name="Server"/> 各自獨立，同名資料庫可以散在多台伺服器。</param>
[Serializable]
public sealed record SqlFavorite(Guid FavoriteId, string Name, string? Description,
    Guid CurrentRevisionId, string? Server, string? Database);

/// <summary>儲存層讀出的 Session 投影；Version 是交易 CAS，不是 UI 的文字版本。</summary>
/// <remarks>
/// <paramref name="RecoveryContentId"/> 是目前 Recovery 指向的內容位址（沒有 Recovery 時為 null），
/// 讓引擎在準備下一筆擷取時不必先讀 Recovery 資料表即可判斷內容是否真的變了。
/// </remarks>
[Serializable]
public sealed record SqlSessionHead(SqlSession Session, long Version, long LastSequence,
    SqlRevision? LatestRevision, SqlRevision? LatestExecutionRevision = null, string? RecoveryContentId = null);
