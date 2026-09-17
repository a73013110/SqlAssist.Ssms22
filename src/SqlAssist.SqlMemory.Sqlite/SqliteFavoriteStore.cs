using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteContentRows;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>收藏：單一儲存入口、標註篩選的時間 keyset 分頁與版本時間軸。</summary>
internal sealed class SqliteFavoriteStore
{
    private const string FavoriteCursor = "favorite2";
    private const string RevisionCursor = "revision2";

    private const string FavoriteProjection = @"SELECT f.FavoriteId,f.Name,f.Description,f.CurrentRevisionId,
f.Server,f.DatabaseName,f.UpdatedAt,f.Version,r.ContentId,c.Preview";
    private const string FavoriteSource = @"
FROM Favorites f JOIN Revisions r ON r.RevisionId=f.CurrentRevisionId JOIN Contents c ON c.ContentId=r.ContentId";

    private readonly SqliteDatabase _database;
    private readonly SqliteSearchBudget _searchBudget;

    public SqliteFavoriteStore(SqliteDatabase database, SqliteSearchBudget? searchBudget = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _searchBudget = searchBudget ?? SqliteSearchBudget.Default;
    }

    public SqlFavoriteItem? ReadFavorite(Guid favoriteId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Connect();
        using var command = Command(connection, null, FavoriteProjection + FavoriteSource + " WHERE f.FavoriteId=$id;", ("$id", Id(favoriteId)));
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read() ? ReadFavoriteItem(reader) : null;
    }

    /// <remarks>
    /// 與擷取、清理共用寫鎖及交易邊界，不能先檢查版本再另開交易寫入。收藏自己的版本不屬於任何 Session，
    /// 也不動 head、序號或 Capture，更不寫 History 列——收藏與擷取是兩條獨立的路。ParentRevisionId 留空：
    /// 接成版本鏈會讓每個舊版本被子版本永久保護，配額就永遠回收不到。
    /// </remarks>
    public SqlFavoriteWriteResult SaveFavorite(SqlFavoriteSave save, CancellationToken cancellationToken)
    {
        if (save == null) throw new ArgumentNullException(nameof(save));
        var favorite = save.Favorite;
        // 雜湊在取得寫鎖之前算好，大型 SQL 不拉長別的程序等鎖的時間。
        var content = save.Sql == null ? null : SqlContent.Create(save.Sql);
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        // 新增時已存在是重送或撞號，更新時不符是別人改過或已移除；一個比較涵蓋兩者，都不覆寫。
        if (ReadFavoriteVersion(connection, transaction, favorite.FavoriteId) != save.ExpectedVersion)
            return SqlFavoriteWriteResult.Conflict;
        cancellationToken.ThrowIfCancellationRequested();
        if (content != null)
        {
            WriteContent(connection, transaction, content);
            Execute(connection, transaction, @"INSERT INTO Revisions
(RevisionId,ContentId,CreatedAt,Reason,IsExecutionSelection,FavoriteId) VALUES($id,$content,$time,$reason,0,$favorite);",
                ("$id", Id(favorite.CurrentRevisionId)), ("$content", content.ContentId), ("$time", Ticks(save.SavedAt)),
                ("$reason", (int)SqlRevisionReason.Favorite), ("$favorite", Id(favorite.FavoriteId)));
        }
        Execute(connection, transaction, @"INSERT INTO Favorites
(FavoriteId,Name,Description,CurrentRevisionId,Server,DatabaseName,UpdatedAt,Version)
VALUES($id,$name,$description,$revision,$server,$database,$time,$version)
ON CONFLICT(FavoriteId) DO UPDATE SET Name=excluded.Name,Description=excluded.Description,
CurrentRevisionId=excluded.CurrentRevisionId,Server=excluded.Server,DatabaseName=excluded.DatabaseName,
UpdatedAt=excluded.UpdatedAt,Version=excluded.Version;",
            ("$id", Id(favorite.FavoriteId)), ("$name", favorite.Name), ("$description", favorite.Description),
            ("$revision", Id(favorite.CurrentRevisionId)), ("$server", favorite.Server), ("$database", favorite.Database),
            ("$time", Ticks(save.SavedAt)), ("$version", Id(Guid.NewGuid())));
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return SqlFavoriteWriteResult.Committed;
    }

    public SqlFavoriteWriteResult DeleteFavorite(Guid favoriteId, Guid expectedVersion, CancellationToken cancellationToken)
    {
        if (expectedVersion == Guid.Empty) throw new ArgumentException("版本不可為空。", nameof(expectedVersion));
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (ReadFavoriteVersion(connection, transaction, favoriteId) != expectedVersion)
            return SqlFavoriteWriteResult.Conflict;
        // 移除收藏不等於刪除歷史；版本與內容留給具引用保護的維護流程。
        Execute(connection, transaction, "DELETE FROM Favorites WHERE FavoriteId=$id;", ("$id", Id(favoriteId)));
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return SqlFavoriteWriteResult.Committed;
    }

    private static Guid? ReadFavoriteVersion(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT Version FROM Favorites WHERE FavoriteId=$id;", ("$id", Id(id)));
        return command.ExecuteScalar() is string value ? Guid.ParseExact(value, "N") : (Guid?)null;
    }

    private static SqlFavoriteItem ReadFavoriteItem(SqliteDataReader reader) => new(
        new SqlFavorite(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), StringOrNull(reader, 2),
            Guid.ParseExact(reader.GetString(3), "N"), StringOrNull(reader, 4), StringOrNull(reader, 5)),
        Guid.ParseExact(reader.GetString(7), "N"), reader.GetString(8), reader.GetString(9), Time(reader.GetInt64(6)));

    public SqlMemoryPage<SqlFavoriteItem> ReadFavorites(SqlFavoriteRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var binding = SqliteTimeCursor.Fingerprint(request.Server, request.Database, request.Search);
        var cursor = SqliteTimeCursor.Decode(request.Cursor, FavoriteCursor, _database.StoreId, binding, SqliteTimeCursor.IsId);
        // 搜尋只是標註篩選之上的條件，同樣受單頁掃描預算限制。
        var search = SqliteSearchScan.Create(request.Search, _searchBudget, cancellationToken);
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)> { ("$limit", search?.CandidateLimit ?? request.PageSize + 1) };
        SqliteConnectionFilter.Append(conditions, parameters, "f", request.Server, request.Database);
        cursor?.AppendCondition(conditions, parameters, "f.UpdatedAt", "f.FavoriteId");
        using var connection = _database.Connect();
        using var command = Command(connection, null, FavoritePageSql(conditions, search != null), parameters.ToArray());
        using var reader = command.ExecuteReader();
        return SqliteKeysetPage.Read(reader, request.PageSize, search,
            row => (row.GetInt64(6), row.GetString(0)),
            (row, scan) => scan.Matches((byte[])row.GetValue(10), row.GetString(1), StringOrNull(row, 2)),
            ReadFavoriteItem,
            (ticks, key) => SqliteTimeCursor.Encode(FavoriteCursor, _database.StoreId, binding, ticks, key), cancellationToken);
    }

    /// <summary>必須沿標註時間索引串流而沒有暫存排序，搜尋預算才真的限制讀入的 BLOB；由 EXPLAIN 測試守住。</summary>
    internal static string FavoritePageSql(IReadOnlyCollection<string> conditions, bool includeSql) =>
        FavoriteProjection + (includeSql ? ",c.SqlBytes" : "") + FavoriteSource + SqliteConnectionFilter.Where(conditions) +
        " ORDER BY f.UpdatedAt DESC,f.FavoriteId DESC LIMIT $limit;";

    public SqlMemoryPage<SqlFavoriteRevisionItem> ReadFavoriteRevisions(SqlFavoriteRevisionRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var favoriteId = Id(request.FavoriteId);
        var after = SqliteTimeCursor.Decode(request.Cursor, RevisionCursor, _database.StoreId, favoriteId, SqliteTimeCursor.IsId);
        using var connection = _database.Connect();
        // 兩次查詢要看到同一份快照：否則回溯剛換掉目前版本時，時間軸會同時少一筆或多標一個目前版本。
        using var transaction = connection.BeginTransaction(deferred: true);

        SqlFavoriteRevisionItem? current;
        using (var command = Command(connection, transaction, @"SELECT r.RevisionId,r.ContentId,r.CreatedAt,r.Reason,c.Preview,c.Length,r.FavoriteId
FROM Favorites f JOIN Revisions r ON r.RevisionId=f.CurrentRevisionId JOIN Contents c ON c.ContentId=r.ContentId
WHERE f.FavoriteId=$id;", ("$id", favoriteId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return new SqlMemoryPage<SqlFavoriteRevisionItem>(Array.Empty<SqlFavoriteRevisionItem>(), null);
            current = ReadRevisionItem(reader, true);
            // 收藏自己的目前版本會在索引串流裡出現；只有引用自 History 的目前版本要另外併進時間序。
            if (StringOrNull(reader, 6) == favoriteId) current = null;
        }
        if (current != null && after != null && Compare(current, (after.Ticks, after.Key)) >= 0) current = null;

        cancellationToken.ThrowIfCancellationRequested();
        using var page = Command(connection, transaction, FavoriteRevisionPageSql(after != null),
            ("$id", favoriteId), ("$time", after?.Ticks), ("$revision", after?.Key), ("$limit", request.PageSize + 1));
        using var rows = page.ExecuteReader();
        var items = new List<SqlFavoriteRevisionItem>();
        bool Add(SqlFavoriteRevisionItem item)
        {
            if (items.Count == request.PageSize) return false;
            items.Add(item);
            return true;
        }
        string Cursor()
        {
            var last = items[items.Count - 1];
            return SqliteTimeCursor.Encode(RevisionCursor, _database.StoreId, favoriteId, Ticks(last.CreatedAt), Id(last.RevisionId));
        }

        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = ReadRevisionItem(rows, false);
            if (current != null && Compare(current, (Ticks(item.CreatedAt), Id(item.RevisionId))) > 0)
            {
                if (!Add(current)) return new SqlMemoryPage<SqlFavoriteRevisionItem>(items, Cursor());
                current = null;
            }
            if (!Add(item)) return new SqlMemoryPage<SqlFavoriteRevisionItem>(items, Cursor());
        }
        if (current != null && !Add(current)) return new SqlMemoryPage<SqlFavoriteRevisionItem>(items, Cursor());
        return new SqlMemoryPage<SqlFavoriteRevisionItem>(items, null);

        SqlFavoriteRevisionItem ReadRevisionItem(SqliteDataReader reader, bool isCurrent) => new(
            Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), Time(reader.GetInt64(2)),
            (SqlRevisionReason)reader.GetInt32(3), isCurrent || reader.GetInt32(6) != 0, reader.GetString(4), reader.GetInt32(5));
    }

    /// <summary>新到舊的時間序比較；與 keyset 的 (CreatedAt, RevisionId) DESC 同一個順序。</summary>
    private static int Compare(SqlFavoriteRevisionItem item, (long Ticks, string RevisionId) key)
    {
        var ticks = Ticks(item.CreatedAt).CompareTo(key.Ticks);
        return ticks != 0 ? ticks : string.CompareOrdinal(Id(item.RevisionId), key.RevisionId);
    }

    /// <summary>
    /// 沿部分索引 <c>IX_Revisions_Favorite</c> 反向串流，不做暫存排序；只讀這個收藏自己的版本，由 EXPLAIN 測試守住。
    /// </summary>
    /// <remarks>第七欄是「是否為目前版本」；讀取器依欄位位置解讀，與目前版本那條查詢共用。</remarks>
    internal static string FavoriteRevisionPageSql(bool after) => @"SELECT r.RevisionId,r.ContentId,r.CreatedAt,r.Reason,c.Preview,c.Length,
 EXISTS(SELECT 1 FROM Favorites f WHERE f.FavoriteId=$id AND f.CurrentRevisionId=r.RevisionId)
FROM Revisions r INDEXED BY IX_Revisions_Favorite JOIN Contents c ON c.ContentId=r.ContentId
WHERE r.FavoriteId=$id AND r.FavoriteId IS NOT NULL" +
        (after ? " AND (r.CreatedAt,r.RevisionId) < ($time,$revision)" : "") +
        " ORDER BY r.CreatedAt DESC,r.RevisionId DESC LIMIT $limit;";
}
