using System;
using System.Collections.Generic;
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

    /// <summary>以 (UpdatedAt, FavoriteId) 新到舊 keyset 分頁；伺服器與資料庫標註各自精確比對，未指定表示不限。</summary>
    Task<SqlMemoryPage<SqlFavoriteItem>> ReadFavoritesAsync(SqlFavoriteRequest request, CancellationToken cancellationToken);

    /// <summary>新增或更新收藏的唯一寫入入口：資料、選用的新版本與 UpdatedAt 在同一交易完成。</summary>
    /// <remarks>
    /// <see cref="SqlFavoriteSave.ExpectedVersion"/> 為 null 只允許新增，收藏已存在回 Conflict；否則須符合讀取時的版本，
    /// 不符或收藏已不存在都回 Conflict，不留下部分寫入。<see cref="SqlFavoriteSave.Sql"/> 為 null 時引用
    /// <see cref="SqlFavorite.CurrentRevisionId"/> 指定的既有版本（不存在由外鍵拒絕）；有 SQL 時以該識別碼建立收藏自己的版本——
    /// 不屬於任何 Session、不進 History、不建 Capture，也不動任何 head 或序號，擷取設定關著也照樣收得起來。
    /// 不刪除舊 Revision、Content 或 History；重送不冪等，回應遺失後先重讀。
    /// </remarks>
    Task<SqlFavoriteWriteResult> SaveFavoriteAsync(SqlFavoriteSave save, CancellationToken cancellationToken);

    Task<SqlFavoriteWriteResult> DeleteFavoriteAsync(Guid favoriteId, Guid expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// 收藏的版本時間軸，新到舊以 (CreatedAt, RevisionId) keyset 分頁；只帶列表投影，全文另以 ContentId 讀取。
    /// </summary>
    /// <remarks>
    /// 包含收藏自己建立的版本，以及目前版本——即使它是引用自 History、不屬於這個收藏的擷取版本。
    /// 不走 ParentRevisionId：收藏版本刻意不串版本鏈。清單只剩維護配額還保留的版本，
    /// 不代表完整編輯史；收藏不存在回空頁。回溯不另設寫入路徑，一律以舊版本全文走
    /// <see cref="SaveFavoriteAsync"/> 產生新版本。
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
/// <param name="UpdatedAt">最後一次儲存的時間；清單依它排序。</param>
[Serializable]
public sealed record SqlFavoriteItem(SqlFavorite Favorite, Guid Version, string ContentId, string Preview,
    DateTimeOffset UpdatedAt);

/// <summary>一次收藏儲存；新增、改資料、改 SQL 與回溯都是它。</summary>
[Serializable]
public sealed class SqlFavoriteSave
{
    /// <summary>伺服器與資料庫標註的長度上限；對齊 SQL Server 名稱，擋下誤貼的整段文字。</summary>
    public const int MaxTagLength = 256;

    /// <param name="expectedVersion">null 表示新增。</param>
    /// <param name="sql">null 表示引用 <see cref="SqlFavorite.CurrentRevisionId"/> 指定的既有版本，不改 SQL。</param>
    public SqlFavoriteSave(SqlFavorite favorite, Guid? expectedVersion, DateTimeOffset savedAt, string? sql = null)
    {
        if (favorite == null) throw new ArgumentNullException(nameof(favorite));
        if (favorite.FavoriteId == Guid.Empty || favorite.CurrentRevisionId == Guid.Empty)
            throw new ArgumentException("SQL Favorite 與 Revision 必須有識別碼。", nameof(favorite));
        if (string.IsNullOrWhiteSpace(favorite.Name) || favorite.Name.Length > 200)
            throw new ArgumentException("SQL Favorite 名稱必須為 1～200 字元。", nameof(favorite));
        if (favorite.Description?.Length > 2000)
            throw new ArgumentException("SQL Favorite 說明不得超過 2000 字元。", nameof(favorite));
        if (expectedVersion == Guid.Empty) throw new ArgumentException("版本不可為空。", nameof(expectedVersion));
        if (sql?.Length == 0) throw new ArgumentException("收藏的 SQL 不可為空。", nameof(sql));
        // 只帶原文，內容位址留給儲存層在背景計算，不讓 UI 執行緒為大型 SQL 做雜湊。
        Favorite = favorite with
        {
            Description = string.IsNullOrEmpty(favorite.Description) ? null : favorite.Description,
            Server = Tag(favorite.Server, nameof(favorite)),
            Database = Tag(favorite.Database, nameof(favorite)),
        };
        ExpectedVersion = expectedVersion;
        SavedAt = savedAt.ToUniversalTime();
        Sql = sql;
    }

    public SqlFavorite Favorite { get; }
    public Guid? ExpectedVersion { get; }
    public DateTimeOffset SavedAt { get; }
    public string? Sql { get; }

    /// <summary>標註與篩選共用的正規化：空白視為不限，前後空白不算名稱的一部分。</summary>
    internal static string? Tag(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var tag = value!.Trim();
        if (tag.Length > MaxTagLength)
            throw new ArgumentException($"伺服器與資料庫標註不得超過 {MaxTagLength} 字元。", parameter);
        return tag;
    }
}

[Serializable]
public sealed class SqlFavoriteRequest
{
    /// <param name="servers">伺服器標註；空名單表示不限，不隱含「未標註」。</param>
    /// <param name="databases">資料庫標註；不需要先指定伺服器。</param>
    public SqlFavoriteRequest(int pageSize, IEnumerable<string>? servers = null, IEnumerable<string>? databases = null,
        string? search = null, string? cursor = null)
    {
        if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        PageSize = pageSize;
        // 標註的長度上限與儲存時同一份；篩選放行更長的字只會永遠篩不到，而畫面上看不出是為什麼。
        Servers = SqlConnectionNames.Normalize(servers, name => SqlFavoriteSave.Tag(name, nameof(servers)));
        Databases = SqlConnectionNames.Normalize(databases, name => SqlFavoriteSave.Tag(name, nameof(databases)));
        Search = string.IsNullOrEmpty(search) ? null : search;
        Cursor = cursor;
    }

    public int PageSize { get; }

    /// <summary>伺服器標註；空名單表示不限。</summary>
    public IReadOnlyList<string> Servers { get; }

    /// <summary>資料庫標註；空名單表示不限。</summary>
    public IReadOnlyList<string> Databases { get; }

    /// <summary>
    /// 區分大小寫的字面子字串，與 History 搜尋同語意；命中名稱、說明或目前版本的 SQL 全文即納入。
    /// 它是標註篩選之上的額外條件，不是 FTS 或萬用字元比對。
    /// </summary>
    public string? Search { get; }
    public string? Cursor { get; }
}
