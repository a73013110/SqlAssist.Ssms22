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
        var contextId = WriteContext(connection, transaction, favorite.Connection);
        Execute(connection, transaction, @"INSERT INTO Favorites
(FavoriteId,Name,Description,CurrentRevisionId,Scope,ContextId,Server,DatabaseName,Version)
VALUES($id,$name,$description,$revision,$scope,$context,$server,$database,$version)
ON CONFLICT(FavoriteId) DO UPDATE SET Name=excluded.Name,Description=excluded.Description,
CurrentRevisionId=excluded.CurrentRevisionId,Scope=excluded.Scope,ContextId=excluded.ContextId,
Server=excluded.Server,DatabaseName=excluded.DatabaseName,Version=excluded.Version;",
            ("$id", Id(favorite.FavoriteId)), ("$name", favorite.Name), ("$description", favorite.Description),
            ("$revision", Id(favorite.CurrentRevisionId)), ("$scope", (int)favorite.Scope), ("$context", contextId),
            ("$server", favorite.Connection?.Server),
            ("$database", favorite.Scope == SqlFavoriteScope.Database ? favorite.Connection?.Database : null),
            ("$version", Id(Guid.NewGuid())));
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
        string sessionId;
        using (var command = Command(connection, transaction, @"SELECT s.Version,s.ContextId,r.SessionId FROM Favorites s
JOIN Revisions r ON r.RevisionId=s.CurrentRevisionId WHERE s.FavoriteId=$id;", ("$id", Id(edit.FavoriteId))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || Guid.ParseExact(reader.GetString(0), "N") != edit.ExpectedVersion)
                return SqlFavoriteWriteResult.Conflict;
            contextId = StringOrNull(reader, 1);
            sessionId = reader.GetString(2);
        }
        cancellationToken.ThrowIfCancellationRequested();
        WriteContent(connection, transaction, content);
        // 沿用被編輯版本的 Session，不建假 Session，也不動 head、序號或 Capture；不寫 History 列。
        // ParentRevisionId 留空：接成版本鏈會讓每個舊版本被子版本永久保護，配額就永遠回收不到。
        Execute(connection, transaction, @"INSERT INTO Revisions
(RevisionId,ParentRevisionId,ContentId,SessionId,CreatedAt,Reason,ContextId,IsExecutionSelection,FavoriteId)
VALUES($id,NULL,$content,$session,$time,$reason,$context,0,$favorite);",
            ("$id", Id(edit.RevisionId)), ("$content", content.ContentId), ("$session", sessionId),
            ("$time", Ticks(edit.EditedAt)), ("$reason", (int)SqlRevisionReason.FavoriteEdit),
            ("$context", contextId), ("$favorite", Id(edit.FavoriteId)));
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
