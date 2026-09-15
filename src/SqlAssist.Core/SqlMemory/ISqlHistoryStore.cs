using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

public enum SqlHistoryCommitResult { Committed, Conflict, AlreadyCommitted }

/// <summary>
/// 實作須跨程序安全；方法不得依賴 UI 執行緒。不可提供載入所有歷史的捷徑。
/// </summary>
public interface ISqlHistoryStore
{
    Task<SqlSessionHead?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// 單一交易：先檢查 CaptureId 冪等，再 CAS ExpectedVersion（null 代表尚無 Session）。
    /// ContentId 唯一鍵去重時必須核對原文，碰撞不可覆寫；Revision 只新增。
    /// 寫入 Document、Session、Content、Revision、Execution 與單份 Recovery 必須全成或全敗。
    /// Conflict／AlreadyCommitted 不得有部分寫入；成功後 Session.Version 遞增。
    /// Recovery 為 null 表示不改動；DeleteRecovery 優先表示刪除。
    /// leaseId 是呼叫端目前認定的程序租約；交易內租約列已不存在（被其他程序回收）時 Session 寫成無租約，
    /// 不得因外鍵讓整筆擷取失敗。失敗以 <see cref="SqlMemoryStorageException"/> 分類回報。
    /// </summary>
    Task<SqlHistoryCommitResult> CommitAsync(SqlCaptureCommit write, string? leaseId, CancellationToken cancellationToken);

    /// <summary>
    /// 以 CreatedAt DESC、唯一鍵 DESC 做 keyset paging。時間區間為 [Since, Until)。
    /// Search 為字面子字串（不是 SQL LIKE 語法）；失效／跨篩選游標須明確拒絕。
    /// 預覽至多 240 個 UTF-16 code units，不隨列表載入完整 SQL。
    /// </summary>
    Task<SqlMemoryPage<SqlHistoryItem>> ReadHistoryAsync(SqlHistoryRequest request, CancellationToken cancellationToken);

    Task<SqlContent?> ReadContentAsync(string contentId, CancellationToken cancellationToken);

    /// <summary>
    /// History 或 Favorites 出現過的伺服器／資料庫名稱，不讀 SQL。最多回傳
    /// <see cref="SqlConnectionFacetRequest.PageSize"/> + 1 筆：多出的一筆只表示還有下一頁，不顯示。
    /// </summary>
    Task<IReadOnlyList<string>> ReadConnectionFacetsAsync(SqlConnectionFacetRequest request, CancellationToken cancellationToken);
}
