namespace SqlAssist.KeywordGenerator.Data;

/// <summary>資料指標：OPEN、CLOSE（含對稱金鑰）、DEALLOCATE、FETCH。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class CursorPhrases
{
    internal static readonly PhraseDeclaration[] OpenClose =
    [
        // 資料指標語句：名稱前可以夾 GLOBAL，FETCH 的方向之後是 FROM。OPEN、CLOSE 另接對稱金鑰與
        // 資料庫主要金鑰，金鑰名稱之後的 DECRYPTION BY 接憑證、密碼或另一把金鑰，金鑰之後還可以 WITH PASSWORD。
        // OPEN SYMMETRIC KEY 的名稱後面還要寫 DECRYPTION 才完整，探測判成封閉會把名稱藏起來，由人宣告不封閉。
        new("OPEN") { Expand = 4 },
        new("OPEN SYMMETRIC KEY") { Closed = false },
        new("OPEN SYMMETRIC KEY {name}") { Expand = 6 },
        new("CLOSE") { Expand = 2 },
    ];

    internal static readonly PhraseDeclaration[] Fetch =
    [
        // ISO 寫法的游標宣告：名稱與 CURSOR 之間是 INSENSITIVE、SCROLL。DECLARE @a 是變數的宣告，名稱格不收它。
        new("DECLARE {name}") { Expand = 1 },
        new("DEALLOCATE"),
        new("FETCH") { Expand = 2 },
    ];
}
