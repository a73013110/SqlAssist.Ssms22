using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteContentRows;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>收藏：metadata CAS、SQL 編輯版本與 scope keyset 分頁。</summary>
internal sealed class SqliteFavoriteStore
{
    private const string FavoriteProjection = @"SELECT s.FavoriteId,s.Name,s.Description,s.CurrentRevisionId,
s.Scope,x.Server,x.DatabaseName,s.Version,r.ContentId,c.Preview";
    private const string FavoriteSource = @"
FROM Favorites s JOIN Revisions r ON r.RevisionId=s.CurrentRevisionId
JOIN Contents c ON c.ContentId=r.ContentId LEFT JOIN Contexts x ON x.ContextId=s.ContextId";

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
        using var command = Command(connection, null, FavoriteProjection + FavoriteSource + " WHERE s.FavoriteId=$id;", ("$id", Id(favoriteId)));
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read() ? ReadFavoriteItem(reader) : null;
    }

    public SqlFavoriteWriteResult WriteFavorite(SqlFavoriteWrite write, CancellationToken cancellationToken)
    {
        if (write == null) throw new ArgumentNullException(nameof(write));
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        var favorite = write.Favorite;
        // 與擷取、清理共用寫鎖及交易邊界，不能先檢查引用再另開交易刪除。
        if (ReadFavoriteVersion(connection, transaction, favorite.FavoriteId) != write.ExpectedVersion)
            return SqlFavoriteWriteResult.Conflict;
        cancellationToken.ThrowIfCancellationRequested();
        WriteFavoriteRow(connection, transaction, favorite);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return SqlFavoriteWriteResult.Committed;
    }

    public SqlFavoriteWriteResult CreateFavoriteFromSql(SqlFavoriteSqlCreate create, CancellationToken cancellationToken)
    {
        if (create == null) throw new ArgumentNullException(nameof(create));
        var content = SqlContent.Create(create.Sql);
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        // 已經有這個收藏就是重送或撞號；覆寫的話會把別人的名稱、scope 與版本一起換掉。
        if (ReadFavoriteVersion(connection, transaction, create.Favorite.FavoriteId) != null)
            return SqlFavoriteWriteResult.Conflict;
        cancellationToken.ThrowIfCancellationRequested();
        WriteContent(connection, transaction, content);
        WriteFavoriteRevision(connection, transaction, create.Favorite, content.ContentId, create.CreatedAt);
        WriteFavoriteRow(connection, transaction, create.Favorite);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return SqlFavoriteWriteResult.Committed;
    }

    public SqlFavoriteWriteResult EditFavoriteSql(SqlFavoriteSqlEdit edit, CancellationToken cancellationToken)
    {
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        var content = SqlContent.Create(edit.Sql);
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        string? contextId;
        using (var command = Command(connection, transaction,
            "SELECT Version,ContextId FROM Favorites WHERE FavoriteId=$id;", ("$id", Id(edit.FavoriteId))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || Guid.ParseExact(reader.GetString(0), "N") != edit.ExpectedVersion)
                return SqlFavoriteWriteResult.Conflict;
            contextId = StringOrNull(reader, 1);
        }
        cancellationToken.ThrowIfCancellationRequested();
        WriteContent(connection, transaction, content);
        WriteFavoriteRevision(connection, transaction, edit.FavoriteId, edit.RevisionId, content.ContentId,
            contextId, edit.EditedAt);
        Execute(connection, transaction, "UPDATE Favorites SET CurrentRevisionId=$revision,Version=$version WHERE FavoriteId=$id;",
            ("$revision", Id(edit.RevisionId)), ("$version", Id(Guid.NewGuid())), ("$id", Id(edit.FavoriteId)));
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

    /// <summary>收藏自己的版本：新增與改 SQL 共用同一個形狀。</summary>
    /// <remarks>
    /// 不屬於任何 Session，也不動 head、序號或 Capture，更不寫 History 列——收藏與擷取是兩條獨立的路，
    /// 擷取設定是關的也照樣收得起來。ParentRevisionId 留空：接成版本鏈會讓每個舊版本被子版本永久保護，
    /// 配額就永遠回收不到。連線沿用收藏自己的 scope，不是它被收藏當下的執行連線。
    /// </remarks>
    private static void WriteFavoriteRevision(SqliteConnection connection, SqliteTransaction transaction,
        Guid favoriteId, Guid revisionId, string contentId, string? contextId, DateTimeOffset createdAt) =>
        Execute(connection, transaction, @"INSERT INTO Revisions
(RevisionId,ParentRevisionId,ContentId,SessionId,CreatedAt,Reason,ContextId,IsExecutionSelection,FavoriteId)
VALUES($id,NULL,$content,NULL,$time,$reason,$context,0,$favorite);",
            ("$id", Id(revisionId)), ("$content", contentId), ("$time", Ticks(createdAt)),
            ("$reason", (int)SqlRevisionReason.Favorite), ("$context", contextId), ("$favorite", Id(favoriteId)));

    private static void WriteFavoriteRevision(SqliteConnection connection, SqliteTransaction transaction,
        SqlFavorite favorite, string contentId, DateTimeOffset createdAt) =>
        WriteFavoriteRevision(connection, transaction, favorite.FavoriteId, favorite.CurrentRevisionId, contentId,
            WriteContext(connection, transaction, favorite.Connection), createdAt);

    /// <summary>收藏列本身；新增與更新 metadata 共用，欄位組合由 schema 的 scope CHECK 守住。</summary>
    private static void WriteFavoriteRow(SqliteConnection connection, SqliteTransaction transaction, SqlFavorite favorite) =>
        Execute(connection, transaction, @"INSERT INTO Favorites
(FavoriteId,Name,Description,CurrentRevisionId,Scope,ContextId,Server,DatabaseName,Version)
VALUES($id,$name,$description,$revision,$scope,$context,$server,$database,$version)
ON CONFLICT(FavoriteId) DO UPDATE SET Name=excluded.Name,Description=excluded.Description,
CurrentRevisionId=excluded.CurrentRevisionId,Scope=excluded.Scope,ContextId=excluded.ContextId,
Server=excluded.Server,DatabaseName=excluded.DatabaseName,Version=excluded.Version;",
            ("$id", Id(favorite.FavoriteId)), ("$name", favorite.Name), ("$description", favorite.Description),
            ("$revision", Id(favorite.CurrentRevisionId)), ("$scope", (int)favorite.Scope),
            ("$context", WriteContext(connection, transaction, favorite.Connection)),
            ("$server", favorite.Connection?.Server),
            ("$database", favorite.Scope == SqlFavoriteScope.Database ? favorite.Connection?.Database : null),
            ("$version", Id(Guid.NewGuid())));

    private static Guid? ReadFavoriteVersion(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT Version FROM Favorites WHERE FavoriteId=$id;", ("$id", Id(id)));
        return command.ExecuteScalar() is string value ? Guid.ParseExact(value, "N") : (Guid?)null;
    }

    private static SqlFavoriteItem ReadFavoriteItem(SqliteDataReader reader) => new(
        new SqlFavorite(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), StringOrNull(reader, 2),
            Guid.ParseExact(reader.GetString(3), "N"), (SqlFavoriteScope)reader.GetInt32(4), ReadContext(reader, 5)),
        Guid.ParseExact(reader.GetString(7), "N"), reader.GetString(8), reader.GetString(9));

    public SqlMemoryPage<SqlFavoriteItem> ReadFavorites(SqlFavoriteRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        // scope 專用前綴防止與 History 游標混用；指紋重用內容雜湊與長度前綴編碼。
        var prefix = "favorite1|" + _database.StoreId + "|" + SqlContent.Create(
            ((int)request.Scope).ToString(CultureInfo.InvariantCulture) + ";" + SqliteFilterKey.Field(request.Server) +
            SqliteFilterKey.Field(request.Database) + SqliteFilterKey.Field(request.Search)).ContentHash + "|";
        var after = DecodeFavoriteCursor(request.Cursor, prefix);
        // 搜尋只是 scope keyset 之上的篩選，同樣受單頁掃描預算限制。
        var search = SqliteSearchScan.Create(request.Search, _searchBudget, cancellationToken);
        using var connection = _database.Connect();
        using var command = Command(connection, null, FavoritePageSql(after != null, search != null),
            ("$scope", (int)request.Scope), ("$server", request.Server), ("$database", request.Database),
            ("$after", after), ("$limit", search?.CandidateLimit ?? request.PageSize + 1));
        using var reader = command.ExecuteReader();
        var items = new List<SqlFavoriteItem>();
        string? lastId = null;
        string Cursor() => Convert.ToBase64String(Encoding.UTF8.GetBytes(prefix + lastId));
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Favorites 沒有時間序；部分搜尋只能說「還有沒檢查的收藏」。
            if (search?.IsExhausted == true) return new SqlMemoryPage<SqlFavoriteItem>(items, Cursor(), null);
            var item = ReadFavoriteItem(reader);
            var matched = search == null ||
                search.Matches((byte[])reader.GetValue(10), item.Favorite.Name, item.Favorite.Description);
            if (matched && items.Count == request.PageSize) return new SqlMemoryPage<SqlFavoriteItem>(items, Cursor());
            lastId = Id(item.Favorite.FavoriteId);
            if (matched) items.Add(item);
        }
        return new SqlMemoryPage<SqlFavoriteItem>(items, null);
    }

    public SqlMemoryPage<SqlFavoriteRevisionItem> ReadFavoriteRevisions(SqlFavoriteRevisionRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var favoriteId = Id(request.FavoriteId);
        var prefix = "revision1|" + _database.StoreId + "|" + favoriteId + "|";
        var after = DecodeRevisionCursor(request.Cursor, prefix);
        using var connection = _database.Connect();
        // 兩次查詢要看到同一份快照：否則回溯剛換掉目前版本時，時間軸會同時少一筆或多標一個目前版本。
        using var transaction = connection.BeginTransaction(deferred: true);

        SqlFavoriteRevisionItem? current;
        using (var command = Command(connection, transaction, @"SELECT r.RevisionId,r.ContentId,r.CreatedAt,r.Reason,c.Preview,c.Length,r.FavoriteId
FROM Favorites s JOIN Revisions r ON r.RevisionId=s.CurrentRevisionId JOIN Contents c ON c.ContentId=r.ContentId
WHERE s.FavoriteId=$id;", ("$id", favoriteId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return new SqlMemoryPage<SqlFavoriteRevisionItem>(Array.Empty<SqlFavoriteRevisionItem>(), null);
            current = ReadRevisionItem(reader, true);
            // 收藏自己的目前版本會在索引串流裡出現；只有引用自 History 的目前版本要另外併進時間序。
            if (StringOrNull(reader, 6) == favoriteId) current = null;
        }
        if (current != null && after is { } cursor && Compare(current, cursor) >= 0) current = null;

        cancellationToken.ThrowIfCancellationRequested();
        using var page = Command(connection, transaction, FavoriteRevisionPageSql(after != null),
            ("$id", favoriteId), ("$time", after?.Ticks), ("$revision", after?.RevisionId), ("$limit", request.PageSize + 1));
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
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(prefix + Ticks(last.CreatedAt).ToString(CultureInfo.InvariantCulture) +
                "|" + Id(last.RevisionId)));
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
 EXISTS(SELECT 1 FROM Favorites s WHERE s.FavoriteId=$id AND s.CurrentRevisionId=r.RevisionId)
FROM Revisions r INDEXED BY IX_Revisions_Favorite JOIN Contents c ON c.ContentId=r.ContentId
WHERE r.FavoriteId=$id AND r.FavoriteId IS NOT NULL" +
        (after ? " AND (r.CreatedAt,r.RevisionId) < ($time,$revision)" : "") +
        " ORDER BY r.CreatedAt DESC,r.RevisionId DESC LIMIT $limit;";

    private static (long Ticks, string RevisionId)? DecodeRevisionCursor(string? cursor, string prefix)
    {
        if (cursor == null) return null;
        if (cursor.Length > 512) throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "SQL Favorite 版本分頁游標無效。");
        string value;
        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException) { throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "SQL Favorite 版本分頁游標無效。"); }
        var parts = value.StartsWith(prefix, StringComparison.Ordinal) ? value.Substring(prefix.Length).Split('|') : Array.Empty<string>();
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "N", out var revision))
            throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "SQL Favorite 版本游標失效或不屬於這個收藏。");
        return (ticks, Id(revision));
    }

    /// <summary>必須沿 scope 索引串流而沒有暫存排序，搜尋預算才真的限制讀入的 BLOB；由 EXPLAIN 測試守住。</summary>
    internal static string FavoritePageSql(bool after, bool includeSql) =>
        FavoriteProjection + (includeSql ? ",c.SqlBytes" : "") + FavoriteSource + @"
 WHERE s.Scope=$scope AND s.Server IS $server AND s.DatabaseName IS $database" +
        (after ? " AND s.FavoriteId < $after" : "") + " ORDER BY s.FavoriteId DESC LIMIT $limit;";

    private static string? DecodeFavoriteCursor(string? cursor, string prefix)
    {
        if (cursor == null) return null;
        if (cursor.Length > 512) throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "SQL Favorite 分頁游標無效。");
        string value;
        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException) { throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "SQL Favorite 分頁游標無效。"); }
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.Substring(prefix.Length), "N", out var id))
            throw new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "SQL Favorite 游標失效或不屬於目前篩選條件。");
        return Id(id);
    }
}
