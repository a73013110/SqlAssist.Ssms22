using System.IO;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>擷取、收藏與維護共用的 Contents／Contexts 列；只在呼叫端的交易內動作。</summary>
internal static class SqliteContentRows
{
    /// <summary>接在 <c>DELETE FROM Contents WHERE ContentId=$id</c> 之後：沒有任何版本、Recovery 或歷程還引用它。</summary>
    public const string Unreferenced = @"
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ContentId=$id)
 AND NOT EXISTS(SELECT 1 FROM Recovery WHERE ContentId=$id)
 AND NOT EXISTS(SELECT 1 FROM History WHERE ContentId=$id)";

    /// <summary>收藏的目前版本是保護根；接在以 <c>$revision</c> 指定版本的條件之後。</summary>
    public const string UnprotectedRevision =
        " AND NOT EXISTS(SELECT 1 FROM Favorites WHERE CurrentRevisionId=$revision)";

    /// <summary>
    /// 接在 <c>DELETE FROM Revisions WHERE RevisionId=$revision</c> 之後：不是保護根，也沒有 head、子版本、執行或歷程引用。
    /// 使用者刪除與維護共用同一份引用清單，任一邊新增引用時不會漏掉另一邊。
    /// </summary>
    public const string UnreferencedRevision = UnprotectedRevision + @"
 AND NOT EXISTS(SELECT 1 FROM Sessions WHERE LatestRevisionId=$revision)
 AND NOT EXISTS(SELECT 1 FROM Sessions WHERE LatestExecutionRevisionId=$revision)
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ParentRevisionId=$revision)
 AND NOT EXISTS(SELECT 1 FROM Executions WHERE RevisionId=$revision)
 AND NOT EXISTS(SELECT 1 FROM History WHERE RevisionId=$revision)";

    /// <summary>完整的連線列刪除；所有引用 Contexts 的表都在這裡。</summary>
    public const string DeleteUnreferencedContext = @"DELETE FROM Contexts WHERE ContextId=$id
 AND NOT EXISTS(SELECT 1 FROM Revisions WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM Executions WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM Recovery WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM History WHERE ContextId=$id)
 AND NOT EXISTS(SELECT 1 FROM Favorites WHERE ContextId=$id);";

    // 交易一開始就取得 IMMEDIATE 寫鎖，同一筆 ContentId 不會有並行寫入；先查再寫可以讓已存在的
    // 內容（去重命中）完全不必重新編碼 UTF-16LE 或讀回整份 BLOB，只比對 Hash 與 Length。
    // SHA-256 的碰撞機率遠低於磁碟或傳輸層本身出錯的機率，全位元組比對留給
    // SqlMemoryStorageSelfTest／ReadContent 的完整讀取驗證；取捨見 docs/sql-memory-storage.md。
    public static void WriteContent(SqliteConnection connection, SqliteTransaction transaction, SqlContent content)
    {
        using (var existing = Command(connection, transaction, "SELECT ContentHash, Length FROM Contents WHERE ContentId=$id;", ("$id", content.ContentId)))
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != content.ContentHash || reader.GetInt64(1) != content.Length)
                    throw new InvalidDataException("SQL 內容位址碰撞或資料已損壞；不會覆寫舊內容。");
                return;
            }
        }
        var bytes = SqliteText.Encode(content.SqlText);
        Execute(connection, transaction, @"INSERT INTO Contents VALUES($id,$hash,$sql,$length,$preview)
ON CONFLICT(ContentId) DO NOTHING;", ("$id", content.ContentId), ("$hash", content.ContentHash), ("$sql", bytes),
            ("$length", content.Length), ("$preview", SqliteText.Preview(content.SqlText)));
    }

    public static string? WriteContext(SqliteConnection connection, SqliteTransaction transaction, SqlConnectionLabel? context)
    {
        if (context == null) return null;
        var key = SqlContent.Create(SqliteFilterKey.Field(context.Server) + SqliteFilterKey.Field(context.Database)).ContentHash;
        Execute(connection, transaction, "INSERT INTO Contexts VALUES($id,$server,$database) ON CONFLICT(ContextId) DO NOTHING;",
            ("$id", key), ("$server", context.Server), ("$database", context.Database));
        using var command = Command(connection, transaction, "SELECT Server, DatabaseName FROM Contexts WHERE ContextId=$id;", ("$id", key));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || ReadContext(reader, 0) != context) throw new InvalidDataException("連線識別碼碰撞。");
        return key;
    }

    public static SqlConnectionLabel? ReadContext(SqliteDataReader reader, int start) => reader.IsDBNull(start) ? null :
        new SqlConnectionLabel(reader.GetString(start), reader.GetString(start + 1));
}
