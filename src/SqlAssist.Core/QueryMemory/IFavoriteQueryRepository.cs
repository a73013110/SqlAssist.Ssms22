using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

public enum FavoriteQueryWriteResult { Committed, Conflict }

/// <summary>Favorite 生命週期獨立於擷取；只引用已存在的不可變 Revision，不建立假 Session。</summary>
public interface IFavoriteQueryRepository
{
    Task<FavoriteQueryEntry?> ReadFavoriteQueryAsync(Guid favoriteQueryId, CancellationToken cancellationToken);
    Task<QueryMemoryPage<FavoriteQueryEntry>> ReadFavoriteQueriesAsync(FavoriteQueryRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// null 版本僅允許新增；更新須符合讀取時的版本。成功產生新版本，衝突不寫入。
    /// 缺少 Revision 必須失敗；不刪除舊 Revision、Content 或 History。
    /// </summary>
    Task<FavoriteQueryWriteResult> WriteFavoriteQueryAsync(FavoriteQueryWrite write, CancellationToken cancellationToken);
    Task<FavoriteQueryWriteResult> DeleteFavoriteQueryAsync(Guid favoriteQueryId, Guid expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// 改 SQL 一律新增不可變版本，並在同一交易換 CurrentRevisionId。
    /// 新版本不進 History 投影、不建立 Session，也不改任何 Session 的 head 或序號。
    /// 版本不符或收藏不存在都回 Conflict，不留下部分寫入；重送不冪等，回應遺失後先重讀。
    /// </summary>
    Task<FavoriteQueryWriteResult> EditFavoriteQuerySqlAsync(FavoriteQueryEdit edit, CancellationToken cancellationToken);
}

// 版本使用不可重用的 token，避免刪除後以相同 Id 重建，讓舊編輯器誤覆寫新資料。
[Serializable]
public sealed record FavoriteQueryEntry(FavoriteQuery Query, Guid Version, string ContentId, string Preview);

[Serializable]
public sealed class FavoriteQueryWrite
{
    public FavoriteQueryWrite(FavoriteQuery query, Guid? expectedVersion = null)
    {
        Query = query ?? throw new ArgumentNullException(nameof(query));
        if (query.FavoriteQueryId == Guid.Empty || query.CurrentRevisionId == Guid.Empty)
            throw new ArgumentException("Favorite Query 與 Revision 必須有識別碼。", nameof(query));
        if (string.IsNullOrWhiteSpace(query.Name) || query.Name.Length > 200)
            throw new ArgumentException("Favorite Query 名稱必須為 1～200 字元。", nameof(query));
        if (query.Description?.Length > 2000)
            throw new ArgumentException("Favorite Query 說明不得超過 2000 字元。", nameof(query));
        FavoriteQueryRequest.ValidateScope(query.Scope, query.Connection?.Server, query.Connection?.Database);
        if (query.Scope == FavoriteQueryScope.Global && query.Connection != null)
            throw new ArgumentException("全域 Favorite Query 不綁連線。", nameof(query));
        if (expectedVersion == Guid.Empty) throw new ArgumentException("版本不可為空。", nameof(expectedVersion));
        ExpectedVersion = expectedVersion;
    }

    public FavoriteQuery Query { get; }
    public Guid? ExpectedVersion { get; }
}

/// <summary>只改 SQL；名稱、說明、scope 仍走 <see cref="FavoriteQueryWrite"/>。</summary>
[Serializable]
public sealed class FavoriteQueryEdit
{
    public FavoriteQueryEdit(Guid favoriteQueryId, Guid expectedVersion, Guid revisionId, string sql, DateTimeOffset editedAt)
    {
        if (favoriteQueryId == Guid.Empty) throw new ArgumentException("Favorite Query 必須有識別碼。", nameof(favoriteQueryId));
        if (expectedVersion == Guid.Empty) throw new ArgumentException("版本不可為空。", nameof(expectedVersion));
        if (revisionId == Guid.Empty) throw new ArgumentException("新版本必須有識別碼。", nameof(revisionId));
        FavoriteQueryId = favoriteQueryId;
        ExpectedVersion = expectedVersion;
        RevisionId = revisionId;
        // 只帶原文，內容位址留給儲存層在背景計算，不讓 UI 執行緒為大型 SQL 做雜湊。
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        EditedAt = editedAt.ToUniversalTime();
    }

    public Guid FavoriteQueryId { get; }
    public Guid ExpectedVersion { get; }
    public Guid RevisionId { get; }
    public string Sql { get; }
    public DateTimeOffset EditedAt { get; }
}

[Serializable]
public sealed class FavoriteQueryRequest
{
    /// <summary>scope 精確比對，不隱含合併全域或父層。以 FavoriteQueryId DESC 穩定分頁。</summary>
    public FavoriteQueryRequest(int pageSize, FavoriteQueryScope scope = FavoriteQueryScope.Global,
        string? server = null, string? database = null, string? search = null, string? cursor = null)
    {
        if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        ValidateScope(scope, server, database);
        PageSize = pageSize;
        Scope = scope;
        Server = server;
        Database = string.IsNullOrEmpty(database) ? null : database;
        Search = string.IsNullOrEmpty(search) ? null : search;
        Cursor = cursor;
    }

    public int PageSize { get; }
    public FavoriteQueryScope Scope { get; }
    public string? Server { get; }
    public string? Database { get; }

    /// <summary>
    /// 區分大小寫的字面子字串，與 History 搜尋同語意；命中名稱、說明或目前版本的 SQL 全文即納入。
    /// 它是 scope 之上的額外篩選，不放寬 scope，也不是 FTS 或萬用字元比對。
    /// </summary>
    public string? Search { get; }
    public string? Cursor { get; }

    internal static void ValidateScope(FavoriteQueryScope scope, string? server, string? database)
    {
        if (!Enum.IsDefined(typeof(FavoriteQueryScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scope == FavoriteQueryScope.Global ? server != null || database != null :
            string.IsNullOrWhiteSpace(server) || (scope == FavoriteQueryScope.Database
                ? string.IsNullOrWhiteSpace(database) : !string.IsNullOrEmpty(database)))
            throw new ArgumentException("scope 與伺服器／資料庫不一致。");
    }
}
