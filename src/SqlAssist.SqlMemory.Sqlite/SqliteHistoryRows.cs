using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>
/// 使用者刪除一列 History 的交易內步驟；逐筆刪除與手動清理共用，兩條路徑的保護根不會分岔。
/// </summary>
internal static class SqliteHistoryRows
{
    /// <summary>空白字元集合：ASCII 那幾個，加上不分行空白與全形空白。</summary>
    private const string BlankCharacters =
        "char(32)||char(9)||char(10)||char(13)||char(11)||char(12)||char(160)||char(12288)";

    /// <summary>
    /// 內容是空白的：空的，或整份都是空白字元。
    /// </summary>
    /// <remarks>
    /// 與擷取端的 <c>SqlContent.IsBlank</c> 是同一條規則的兩種寫法——一邊在 C# 裡問，
    /// 一邊必須是 SQL 才篩得動整張表，兩者不可能共用同一段程式碼。
    ///
    /// 只認長度在預覽之內的那些：本體是 UTF-16LE 的 BLOB，在 SQL 裡拆不開，而
    /// <c>Preview</c> 只留前 <see cref="SqliteText.PreviewLength"/> 個字元。比那還長、
    /// 卻整份都是空白的內容因此漏掉——那是刪太少，不是刪錯，而新的擷取從一開始就不會產生。
    /// </remarks>
    /// <param name="alias">有 <c>ContentId</c> 欄位的資料表別名；只來自呼叫端常數。</param>
    public static string Blank(string alias) =>
        "EXISTS(SELECT 1 FROM Contents c WHERE c.ContentId=" + alias + ".ContentId AND (c.Length=0 OR (c.Length<=" +
        SqliteText.PreviewLength + " AND trim(c.Preview," + BlankCharacters + ")='')))";

    /// <summary>投影鍵：一碼種類前綴（e 執行／r 草稿版本／s 回復內容）加上 32 位十六進位識別碼。</summary>
    public static bool IsKey(string key) =>
        key.Length == 33 && "ers".IndexOf(key[0]) >= 0 && SqliteTimeCursor.IsId(key.Substring(1));

    /// <summary>
    /// 刪投影與它自己的本體：執行刪 Executions、回復內容刪 Recovery；版本只在沒有保護根與其他引用時刪除。
    /// 被釋出的 ContentId 加進 <paramref name="contents"/>，由呼叫端在同一交易結束前 <see cref="CollectContents"/>。
    /// </summary>
    /// <returns>刪除的列數；投影不存在或不屬於 <paramref name="sessionId"/> 時為 0。</returns>
    public static int Delete(SqliteConnection connection, SqliteTransaction transaction, string key, string sessionId,
        ISet<string> contents, CancellationToken cancellationToken)
    {
        var revisions = new HashSet<string>(System.StringComparer.Ordinal);
        using (var read = Command(connection, transaction,
            "SELECT RevisionId,ContentId FROM History WHERE EntryKey=$key AND SessionId=$session;",
            ("$key", key), ("$session", sessionId)))
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) return 0;
            if (StringOrNull(reader, 0) is { } revision) revisions.Add(revision);
            contents.Add(reader.GetString(1));
        }
        var deleted = 0;
        if (key[0] == 'e')
        {
            // 合併列底下的執行可能引用不同版本（同內容的選取版本與完整版本），每一份都要重查引用。
            using (var read = Command(connection, transaction, "SELECT RevisionId FROM Executions WHERE EntryKey=$key;", ("$key", key)))
            using (var reader = read.ExecuteReader())
                while (reader.Read()) revisions.Add(reader.GetString(0));
            // 執行事件只引用版本，自己沒有內容；版本另由下方的引用清單決定能不能刪。
            Execute(connection, transaction, "DELETE FROM Executions WHERE EntryKey=$key;", ("$key", key));
            deleted += Changes(connection, transaction);
        }
        Execute(connection, transaction, "DELETE FROM History WHERE EntryKey=$key;", ("$key", key));
        deleted += Changes(connection, transaction);
        if (key[0] == 's')
        {
            // 仍開著的視窗下一次擷取會重寫 Recovery；這裡只移除使用者看到的那一份。
            deleted += DeleteReleasing(connection, transaction, contents, "Recovery", "SessionId=$id", ("$id", sessionId));
        }
        foreach (var revision in revisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deleted += DeleteReleasing(connection, transaction, contents, "Revisions",
                "RevisionId=$revision" + SqliteContentRows.UnreferencedRevision, ("$revision", revision));
        }
        return deleted;
    }

    /// <summary>重查被釋出的內容，沒有任何引用才刪；回傳刪除的內容列數。</summary>
    public static int CollectContents(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> contents,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var content in contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(connection, transaction, "DELETE FROM Contents WHERE ContentId=$id" + SqliteContentRows.Unreferenced + ";",
                ("$id", content));
            deleted += Changes(connection, transaction);
        }
        return deleted;
    }

    /// <summary>先記下列引用的內容再刪；條件不成立（仍被引用）時什麼都不釋出。</summary>
    private static int DeleteReleasing(SqliteConnection connection, SqliteTransaction transaction, ISet<string> contents,
        string table, string condition, params (string Name, object? Value)[] parameters)
    {
        string? content;
        using (var read = Command(connection, transaction, "SELECT ContentId FROM " + table + " WHERE " + condition + ";", parameters))
            content = read.ExecuteScalar() as string;
        if (content == null) return 0;
        Execute(connection, transaction, "DELETE FROM " + table + " WHERE " + condition + ";", parameters);
        contents.Add(content);
        return Changes(connection, transaction);
    }
}
