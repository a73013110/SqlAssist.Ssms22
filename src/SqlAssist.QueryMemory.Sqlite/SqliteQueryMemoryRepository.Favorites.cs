using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

public sealed partial class SqliteQueryMemoryRepository
{
    private const string FavoriteProjection = @"SELECT s.FavoriteQueryId,s.Name,s.Description,s.CurrentRevisionId,
s.Scope,x.Server,x.DatabaseName,x.IdentityName,s.Version,r.ContentId,c.Preview
FROM FavoriteQueries s JOIN Revisions r ON r.RevisionId=s.CurrentRevisionId
JOIN Contents c ON c.ContentId=r.ContentId LEFT JOIN Contexts x ON x.ContextId=s.ContextId";

    public Task<FavoriteQueryEntry?> ReadFavoriteQueryAsync(Guid favoriteQueryId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var connection = Connect();
        using var command = Command(connection, null, FavoriteProjection + " WHERE s.FavoriteQueryId=$id;", ("$id", Id(favoriteQueryId)));
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read() ? ReadFavoriteEntry(reader) : null;
    }, cancellationToken);

    public Task<FavoriteQueryWriteResult> WriteFavoriteQueryAsync(FavoriteQueryWrite write, CancellationToken cancellationToken)
    {
        if (write == null) throw new ArgumentNullException(nameof(write));
        return Task.Run(() =>
        {
            using var connection = Connect();
            using var transaction = connection.BeginTransaction(deferred: false);
            var query = write.Query;
            // 與擷取、清理共用寫鎖及交易邊界，不能先檢查引用再另開交易刪除。
            if (ReadFavoriteVersion(connection, transaction, query.FavoriteQueryId) != write.ExpectedVersion)
                return FavoriteQueryWriteResult.Conflict;
            cancellationToken.ThrowIfCancellationRequested();
            var contextId = WriteContext(connection, transaction, query.Connection);
            Execute(connection, transaction, @"INSERT INTO FavoriteQueries
(FavoriteQueryId,Name,Description,CurrentRevisionId,Scope,ContextId,Server,DatabaseName,Version)
VALUES($id,$name,$description,$revision,$scope,$context,$server,$database,$version)
ON CONFLICT(FavoriteQueryId) DO UPDATE SET Name=excluded.Name,Description=excluded.Description,
CurrentRevisionId=excluded.CurrentRevisionId,Scope=excluded.Scope,ContextId=excluded.ContextId,
Server=excluded.Server,DatabaseName=excluded.DatabaseName,Version=excluded.Version;",
                ("$id", Id(query.FavoriteQueryId)), ("$name", query.Name), ("$description", query.Description),
                ("$revision", Id(query.CurrentRevisionId)), ("$scope", (int)query.Scope), ("$context", contextId),
                ("$server", query.Connection?.Server),
                ("$database", query.Scope == FavoriteQueryScope.Database ? query.Connection?.Database : null),
                ("$version", Id(Guid.NewGuid())));
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return FavoriteQueryWriteResult.Committed;
        }, cancellationToken);
    }

    public Task<FavoriteQueryWriteResult> EditFavoriteQuerySqlAsync(FavoriteQueryEdit edit, CancellationToken cancellationToken)
    {
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        return Task.Run(() =>
        {
            var content = QueryContent.Create(edit.Sql);
            using var connection = Connect();
            using var transaction = connection.BeginTransaction(deferred: false);
            string? contextId;
            string sessionId;
            using (var command = Command(connection, transaction, @"SELECT s.Version,s.ContextId,r.SessionId FROM FavoriteQueries s
JOIN Revisions r ON r.RevisionId=s.CurrentRevisionId WHERE s.FavoriteQueryId=$id;", ("$id", Id(edit.FavoriteQueryId))))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || Guid.ParseExact(reader.GetString(0), "N") != edit.ExpectedVersion)
                    return FavoriteQueryWriteResult.Conflict;
                contextId = StringOrNull(reader, 1);
                sessionId = reader.GetString(2);
            }
            cancellationToken.ThrowIfCancellationRequested();
            WriteContent(connection, transaction, content);
            // 沿用被編輯版本的 Session，不建假 Session，也不動 head、序號或 Capture；不寫 History 列。
            // ParentRevisionId 留空：接成版本鏈會讓每個舊版本被子版本永久保護，配額就永遠回收不到。
            Execute(connection, transaction, @"INSERT INTO Revisions
(RevisionId,ParentRevisionId,ContentId,SessionId,CreatedAt,Reason,ContextId,IsExecutionSelection,FavoriteQueryId)
VALUES($id,NULL,$content,$session,$time,$reason,$context,0,$favorite);",
                ("$id", Id(edit.RevisionId)), ("$content", content.ContentId), ("$session", sessionId),
                ("$time", Ticks(edit.EditedAt)), ("$reason", (int)QueryRevisionReason.FavoriteQueryEdit),
                ("$context", contextId), ("$favorite", Id(edit.FavoriteQueryId)));
            Execute(connection, transaction, "UPDATE FavoriteQueries SET CurrentRevisionId=$revision,Version=$version WHERE FavoriteQueryId=$id;",
                ("$revision", Id(edit.RevisionId)), ("$version", Id(Guid.NewGuid())), ("$id", Id(edit.FavoriteQueryId)));
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return FavoriteQueryWriteResult.Committed;
        }, cancellationToken);
    }

    public Task<FavoriteQueryWriteResult> DeleteFavoriteQueryAsync(Guid favoriteQueryId, Guid expectedVersion, CancellationToken cancellationToken)
    {
        if (expectedVersion == Guid.Empty) throw new ArgumentException("版本不可為空。", nameof(expectedVersion));
        return Task.Run(() =>
        {
            using var connection = Connect();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (ReadFavoriteVersion(connection, transaction, favoriteQueryId) != expectedVersion)
                return FavoriteQueryWriteResult.Conflict;
            // 移除收藏不等於刪除歷史；版本與內容留給具引用保護的維護流程。
            Execute(connection, transaction, "DELETE FROM FavoriteQueries WHERE FavoriteQueryId=$id;", ("$id", Id(favoriteQueryId)));
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return FavoriteQueryWriteResult.Committed;
        }, cancellationToken);
    }

    private static Guid? ReadFavoriteVersion(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT Version FROM FavoriteQueries WHERE FavoriteQueryId=$id;", ("$id", Id(id)));
        return command.ExecuteScalar() is string value ? Guid.ParseExact(value, "N") : (Guid?)null;
    }

    private static FavoriteQueryEntry ReadFavoriteEntry(SqliteDataReader reader) => new(
        new FavoriteQuery(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), StringOrNull(reader, 2),
            Guid.ParseExact(reader.GetString(3), "N"), (FavoriteQueryScope)reader.GetInt32(4), ReadContext(reader, 5)),
        Guid.ParseExact(reader.GetString(8), "N"), reader.GetString(9), reader.GetString(10));

    public Task<QueryMemoryPage<FavoriteQueryEntry>> ReadFavoriteQueriesAsync(FavoriteQueryRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return Task.Run(() =>
        {
            try { return ReadFavoriteQueries(request, cancellationToken); }
            catch (SqliteException) when (cancellationToken.IsCancellationRequested)
            {
                // SQLite 會包裝 scalar function 的取消例外；對呼叫端仍保留取消語意。
                throw new OperationCanceledException(cancellationToken);
            }
        }, cancellationToken);
    }

    private QueryMemoryPage<FavoriteQueryEntry> ReadFavoriteQueries(FavoriteQueryRequest request, CancellationToken cancellationToken)
    {
        // scope 專用前綴防止與 History 游標混用；指紋重用內容雜湊與長度前綴編碼。
        var prefix = "favorite1|" + _storeId + "|" + QueryContent.Create(
            ((int)request.Scope).ToString(CultureInfo.InvariantCulture) + ";" + Field(request.Server) +
            Field(request.Database) + Field(request.Search)).ContentHash + "|";
        var after = DecodeFavoriteCursor(request.Cursor, prefix);
        using var connection = Connect();
        var parameters = new List<(string, object?)>
        {
            ("$scope", (int)request.Scope), ("$server", request.Server), ("$database", request.Database),
            ("$after", after), ("$limit", request.PageSize + 1),
        };
        // 搜尋只是 scope keyset 之上的篩選；名稱與說明先比對，命中才需要解出目前版本的 SQL。
        var search = SqliteSearchFilter.Create(request.Search);
        using var command = Command(connection, null, FavoriteProjection + @"
 WHERE s.Scope=$scope AND s.Server IS $server AND s.DatabaseName IS $database" +
            (after == null ? "" : " AND s.FavoriteQueryId < $after") +
            (search == null ? "" : " AND " + search.Apply(connection, parameters, "c.SqlBytes", cancellationToken, "s.Name", "s.Description")) +
            " ORDER BY s.FavoriteQueryId DESC LIMIT $limit;", parameters.ToArray());
        using var reader = command.ExecuteReader();
        var items = new List<FavoriteQueryEntry>();
        string? nextCursor = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == request.PageSize)
            {
                nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(prefix + Id(items[items.Count - 1].Query.FavoriteQueryId)));
                break;
            }
            items.Add(ReadFavoriteEntry(reader));
        }
        return new QueryMemoryPage<FavoriteQueryEntry>(items, nextCursor);
    }

    private static string? DecodeFavoriteCursor(string? cursor, string prefix)
    {
        if (cursor == null) return null;
        if (cursor.Length > 512) throw new ArgumentException("Favorite Query 分頁游標無效。", nameof(cursor));
        string value;
        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException error) { throw new ArgumentException("Favorite Query 分頁游標無效。", nameof(cursor), error); }
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.Substring(prefix.Length), "N", out var id))
            throw new ArgumentException("Favorite Query 游標失效或不屬於目前篩選條件。", nameof(cursor));
        return Id(id);
    }
}
