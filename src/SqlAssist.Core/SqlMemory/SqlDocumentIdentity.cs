using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SqlAssist.Core.SqlMemory;

/// <summary>身分改變時要先結束的舊 Session；呼叫端以它送出一筆關閉擷取，再開始新的 Session。</summary>
public sealed class SqlSessionHandover
{
    internal SqlSessionHandover(SqlDocument document, SqlSession session, long sequence)
    {
        Document = document;
        Session = session;
        Sequence = sequence;
    }

    public SqlDocument Document { get; }
    public SqlSession Session { get; }

    /// <summary>關閉擷取要用的序號；接在舊 Session 最後一筆之後。</summary>
    public long Sequence { get; }
}

/// <summary>
/// 一個查詢視窗目前的文件與 Session 身分。檔案路徑或顯示名稱可能在視窗開著時改變
/// （未存檔查詢第一次存檔、另存新檔），每次擷取前以 <see cref="Observe"/> 更新。
/// </summary>
/// <remarks>
/// 只有 UI 執行緒呼叫 <see cref="Observe"/>；<see cref="NextSequence"/> 可跨執行緒。
///
/// 路徑改變就換 DocumentId 與 Session：同一個 Session 不能跨兩份文件（儲存層以此檢查寫入計畫），
/// 而歷程要串在新路徑那份文件上。舊 Session 交由呼叫端正式關閉，它的未存檔草稿才不會掛在
/// 一個再也不會有擷取的 Session 上，靠租約保護到程序結束。只改顯示名稱則沿用同一份文件。
/// </remarks>
public sealed class SqlDocumentIdentity
{
    private long _sequence;

    public SqlDocumentIdentity(string displayName, string? filePath)
    {
        var path = SavedPath(filePath);
        Document = new SqlDocument(DocumentId(path), Name(displayName), path);
        Session = new SqlSession(Guid.NewGuid(), Document.DocumentId);
    }

    public SqlDocument Document { get; private set; }

    public SqlSession Session { get; private set; }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>目前 Session 是否已經送出過擷取；沒有的話儲存層不會有它的任何列。</summary>
    public bool HasCaptures => Interlocked.Read(ref _sequence) > 0;

    /// <summary>以目前的名稱與路徑判斷身分是否過期；只讀、不換 Session。</summary>
    /// <remarks>存檔、另存或改名的事件用它決定要不要立刻擷取，不必等下一次打字才交接。</remarks>
    public bool IsStale(string displayName, string? filePath) =>
        !SamePath(SavedPath(filePath), Document.FilePath) ||
        !string.Equals(Name(displayName), Document.DisplayName, StringComparison.Ordinal);

    /// <summary>以目前的名稱與路徑更新身分。</summary>
    /// <returns>
    /// 路徑改變且舊 Session 已有擷取時，回傳要關閉的舊 Session；其他情形為 null。
    /// 舊 Session 從沒送出擷取就沒有要關的列，送一筆關閉反而會替暫存標題憑空建立版本。
    /// </returns>
    public SqlSessionHandover? Observe(string displayName, string? filePath)
    {
        var path = SavedPath(filePath);
        var name = Name(displayName);
        if (!SamePath(path, Document.FilePath))
        {
            var previous = HasCaptures ? new SqlSessionHandover(Document, Session, NextSequence()) : null;
            Document = new SqlDocument(DocumentId(path), name, path);
            Session = new SqlSession(Guid.NewGuid(), Document.DocumentId);
            Interlocked.Exchange(ref _sequence, 0);
            return previous;
        }

        // 同一路徑的大小寫或顯示名稱變了：文件身分不變，下一筆擷取更新文件列即可。
        if (!string.Equals(name, Document.DisplayName, StringComparison.Ordinal) ||
            !string.Equals(path, Document.FilePath, StringComparison.Ordinal))
            Document = Document with { DisplayName = name, FilePath = path };
        return null;
    }

    /// <summary>
    /// 同一個檔案跨視窗、跨重啟都是同一份文件。
    /// </summary>
    /// <remarks>
    /// 由完整路徑推出來而不是存一張對照表：對照表活不過重新啟動，而歷程要串得起來的
    /// 正是「上週那個檔案」。還沒存檔的查詢沒有路徑可推，每個視窗各自成一份文件——
    /// 拿標題（SQLQuery1.sql）當身分的話，不同時候的兩個新查詢會被併成同一份。
    /// </remarks>
    public static Guid DocumentId(string? filePath)
    {
        var path = SavedPath(filePath);
        if (path == null) return Guid.NewGuid();

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.Unicode.GetBytes(NormalizedPath(path)));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    /// <summary>
    /// 只有本機絕對路徑才算存過檔。未存檔的查詢視窗只帶「SQLQuery1.sql」這類標題，
    /// 把它當路徑的話所有新查詢都會變成同一份文件。
    /// </summary>
    private static string? SavedPath(string? filePath) =>
        string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath) ? null : filePath;

    private static string NormalizedPath(string path) => Path.GetFullPath(path).ToLowerInvariant();

    // 每次擷取前都會走到；比對 DocumentId 的雜湊來源字串即可，不必各算一次 SHA-256。
    private static bool SamePath(string? left, string? right) =>
        left == null || right == null
            ? left == right
            : string.Equals(NormalizedPath(left), NormalizedPath(right), StringComparison.Ordinal);

    private static string Name(string displayName) => string.IsNullOrWhiteSpace(displayName) ? "SQL 查詢" : displayName;
}
