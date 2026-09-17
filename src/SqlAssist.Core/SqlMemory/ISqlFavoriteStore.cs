using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

public enum SqlFavoriteWriteResult { Committed, Conflict }

/// <summary>
/// Favorite 生命週期獨立於擷取：引用已存在的不可變 Revision，或自己建立一份不屬於任何 Session 的版本。
/// </summary>
public interface ISqlFavoriteStore
{
    Task<SqlFavoriteItem?> ReadFavoriteAsync(Guid favoriteId, CancellationToken cancellationToken);
    Task<SqlMemoryPage<SqlFavoriteItem>> ReadFavoritesAsync(SqlFavoriteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// null 版本僅允許新增；更新須符合讀取時的版本。成功產生新版本，衝突不寫入。
    /// 缺少 Revision 必須失敗；不刪除舊 Revision、Content 或 History。
    /// </summary>
    Task<SqlFavoriteWriteResult> WriteFavoriteAsync(SqlFavoriteWrite write, CancellationToken cancellationToken);

    /// <summary>
    /// 從任意 SQL 文字新增收藏：內容、版本與收藏在同一交易寫入。
    /// </summary>
    /// <remarks>
    /// 給沒有既有 Revision 可引用的入口用：查詢視窗當下的 SQL，以及還沒有版本的未存檔草稿。
    /// 版本與 <see cref="EditFavoriteSqlAsync"/> 同一形狀——不屬於任何 Session、不進 History 投影、
    /// 不建 Capture，也不動任何 Session 的 head 或序號；擷取設定是關的也照樣收得起來。
    /// 收藏已存在回 Conflict，不覆寫；重送不冪等，回應遺失後先重讀。
    /// </remarks>
    Task<SqlFavoriteWriteResult> CreateFavoriteFromSqlAsync(SqlFavoriteSqlCreate create, CancellationToken cancellationToken);

    Task<SqlFavoriteWriteResult> DeleteFavoriteAsync(Guid favoriteId, Guid expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// 改 SQL 一律新增不可變版本，並在同一交易換 CurrentRevisionId。
    /// 新版本不進 History 投影、不屬於任何 Session，也不改任何 Session 的 head 或序號。
    /// 版本不符或收藏不存在都回 Conflict，不留下部分寫入；重送不冪等，回應遺失後先重讀。
    /// </summary>
    Task<SqlFavoriteWriteResult> EditFavoriteSqlAsync(SqlFavoriteSqlEdit edit, CancellationToken cancellationToken);

    /// <summary>
    /// 收藏的版本時間軸，新到舊以 (CreatedAt, RevisionId) keyset 分頁；只帶列表投影，全文另以 ContentId 讀取。
    /// </summary>
    /// <remarks>
    /// 包含收藏自己建立的版本，以及目前版本——即使它是引用自 History、不屬於這個收藏的擷取版本。
    /// 不走 ParentRevisionId：收藏版本刻意不串版本鏈。清單只剩維護配額還保留的版本，
    /// 不代表完整編輯史；收藏不存在回空頁。回溯不另設寫入路徑，一律以舊版本全文走
    /// <see cref="EditFavoriteSqlAsync"/> 產生新版本。
    /// </remarks>
    Task<SqlMemoryPage<SqlFavoriteRevisionItem>> ReadFavoriteRevisionsAsync(SqlFavoriteRevisionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>版本時間軸的一列；不讀全文就能畫出清單。</summary>
/// <param name="Reason"><see cref="SqlRevisionReason.Favorite"/> 以外表示引用自 History 的擷取版本。</param>
/// <param name="Preview">與 <see cref="SqlFavoriteItem.Preview"/> 同一份有界單行投影。</param>
/// <param name="Length">全文的 UTF-16 code unit 數；讓介面在讀全文前就知道要不要降級比對。</param>
[Serializable]
public sealed record SqlFavoriteRevisionItem(Guid RevisionId, string ContentId, DateTimeOffset CreatedAt,
    SqlRevisionReason Reason, bool IsCurrent, string Preview, int Length);

[Serializable]
public sealed class SqlFavoriteRevisionRequest
{
    /// <param name="cursor">上一頁的 NextCursor；綁定儲存與收藏，換收藏沿用會被拒絕。</param>
    public SqlFavoriteRevisionRequest(Guid favoriteId, int pageSize, string? cursor = null)
    {
        if (favoriteId == Guid.Empty) throw new ArgumentException("SQL Favorite 必須有識別碼。", nameof(favoriteId));
        if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        FavoriteId = favoriteId;
        PageSize = pageSize;
        Cursor = cursor;
    }

    public Guid FavoriteId { get; }
    public int PageSize { get; }
    public string? Cursor { get; }
}

// 版本使用不可重用的 token，避免刪除後以相同 Id 重建，讓舊編輯器誤覆寫新資料。
[Serializable]
public sealed record SqlFavoriteItem(SqlFavorite Favorite, Guid Version, string ContentId, string Preview);

[Serializable]
public sealed class SqlFavoriteWrite
{
    public SqlFavoriteWrite(SqlFavorite favorite, Guid? expectedVersion = null)
    {
        Favorite = Validate(favorite);
        if (expectedVersion == Guid.Empty) throw new ArgumentException("版本不可為空。", nameof(expectedVersion));
        ExpectedVersion = expectedVersion;
    }

    public SqlFavorite Favorite { get; }
    public Guid? ExpectedVersion { get; }

    /// <summary>收藏 metadata 的共同規則；每個寫入入口都要過同一份，否則限制會隨入口分岔。</summary>
    internal static SqlFavorite Validate(SqlFavorite favorite)
    {
        if (favorite == null) throw new ArgumentNullException(nameof(favorite));
        if (favorite.FavoriteId == Guid.Empty || favorite.CurrentRevisionId == Guid.Empty)
            throw new ArgumentException("SQL Favorite 與 Revision 必須有識別碼。", nameof(favorite));
        if (string.IsNullOrWhiteSpace(favorite.Name) || favorite.Name.Length > 200)
            throw new ArgumentException("SQL Favorite 名稱必須為 1～200 字元。", nameof(favorite));
        if (favorite.Description?.Length > 2000)
            throw new ArgumentException("SQL Favorite 說明不得超過 2000 字元。", nameof(favorite));
        SqlFavoriteRequest.ValidateScope(favorite.Scope, favorite.Connection?.Server, favorite.Connection?.Database);
        if (favorite.Scope == SqlFavoriteScope.Global && favorite.Connection != null)
            throw new ArgumentException("全域 SQL Favorite 不綁連線。", nameof(favorite));
        return favorite;
    }
}

/// <summary>新增收藏並同時建立它自己的第一份版本；<see cref="SqlFavorite.CurrentRevisionId"/> 就是那份版本。</summary>
[Serializable]
public sealed class SqlFavoriteSqlCreate
{
    public SqlFavoriteSqlCreate(SqlFavorite favorite, string sql, DateTimeOffset createdAt)
    {
        Favorite = SqlFavoriteWrite.Validate(favorite);
        // 只帶原文，內容位址留給儲存層在背景計算，不讓 UI 執行緒為大型 SQL 做雜湊。
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        if (sql.Length == 0) throw new ArgumentException("收藏的 SQL 不可為空。", nameof(sql));
        CreatedAt = createdAt.ToUniversalTime();
    }

    public SqlFavorite Favorite { get; }
    public string Sql { get; }
    public DateTimeOffset CreatedAt { get; }
}

/// <summary>只改 SQL；名稱、說明、scope 仍走 <see cref="SqlFavoriteWrite"/>。</summary>
[Serializable]
public sealed class SqlFavoriteSqlEdit
{
    public SqlFavoriteSqlEdit(Guid favoriteId, Guid expectedVersion, Guid revisionId, string sql, DateTimeOffset editedAt)
    {
        if (favoriteId == Guid.Empty) throw new ArgumentException("SQL Favorite 必須有識別碼。", nameof(favoriteId));
        if (expectedVersion == Guid.Empty) throw new ArgumentException("版本不可為空。", nameof(expectedVersion));
        if (revisionId == Guid.Empty) throw new ArgumentException("新版本必須有識別碼。", nameof(revisionId));
        FavoriteId = favoriteId;
        ExpectedVersion = expectedVersion;
        RevisionId = revisionId;
        // 只帶原文，內容位址留給儲存層在背景計算，不讓 UI 執行緒為大型 SQL 做雜湊。
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        EditedAt = editedAt.ToUniversalTime();
    }

    public Guid FavoriteId { get; }
    public Guid ExpectedVersion { get; }
    public Guid RevisionId { get; }
    public string Sql { get; }
    public DateTimeOffset EditedAt { get; }
}

[Serializable]
public sealed class SqlFavoriteRequest
{
    /// <summary>scope 精確比對，不隱含合併全域或父層。以 FavoriteId DESC 穩定分頁。</summary>
    public SqlFavoriteRequest(int pageSize, SqlFavoriteScope scope = SqlFavoriteScope.Global,
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
    public SqlFavoriteScope Scope { get; }
    public string? Server { get; }
    public string? Database { get; }

    /// <summary>
    /// 區分大小寫的字面子字串，與 History 搜尋同語意；命中名稱、說明或目前版本的 SQL 全文即納入。
    /// 它是 scope 之上的額外篩選，不放寬 scope，也不是 FTS 或萬用字元比對。
    /// </summary>
    public string? Search { get; }
    public string? Cursor { get; }

    internal static void ValidateScope(SqlFavoriteScope scope, string? server, string? database)
    {
        if (!Enum.IsDefined(typeof(SqlFavoriteScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scope == SqlFavoriteScope.Global ? server != null || database != null :
            string.IsNullOrWhiteSpace(server) || (scope == SqlFavoriteScope.Database
                ? string.IsNullOrWhiteSpace(database) : !string.IsNullOrEmpty(database)))
            throw new ArgumentException("scope 與伺服器／資料庫不一致。");
    }
}
