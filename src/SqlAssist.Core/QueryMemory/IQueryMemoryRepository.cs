using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

public enum QueryMemoryCommitResult { Committed, Conflict, AlreadyCommitted }

/// <summary>
/// 實作須跨程序安全；方法不得依賴 UI 執行緒。不可提供載入所有歷史的捷徑。
/// </summary>
public interface IQueryMemoryRepository
{
    Task<QuerySessionState?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// 單一交易：先檢查 CaptureId 冪等，再 CAS ExpectedVersion（null 代表尚無 Session）。
    /// ContentId 唯一鍵去重時必須核對原文，碰撞不可覆寫；Revision 只新增。
    /// 寫入 Document、Session、Content、Revision、Execution 與單份 Recovery 必須全成或全敗。
    /// Conflict／AlreadyCommitted 不得有部分寫入；成功後 Session.Version 遞增。
    /// Recovery 為 null 表示不改動；DeleteRecovery 優先表示刪除。
    /// </summary>
    Task<QueryMemoryCommitResult> CommitAsync(QueryMemoryWrite write, CancellationToken cancellationToken);

    /// <summary>
    /// 以 CreatedAt DESC、唯一鍵 DESC 做 keyset paging。時間區間為 [Since, Until)。
    /// Search 為字面子字串（不是 SQL LIKE 語法）；失效／跨篩選游標須明確拒絕。
    /// 預覽至多 240 個 UTF-16 code units，不隨列表載入完整 SQL。
    /// </summary>
    Task<QueryMemoryPage<QueryHistoryItem>> ReadHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken);

    Task<QueryContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken);
}
