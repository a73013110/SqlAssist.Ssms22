namespace SqlAssist.KeywordGenerator.Data;

/// <summary>接在洞後面的續尾，以及判定時拿來對照的普通名稱與變數。</summary>
internal static class Continuations
{
    // 洞後面接的東西。單一續尾會誤判，取聯集。
    internal static readonly string[] Keywords =
    [
        "", " x", " x FROM y", " * FROM y", " TABLE x", " TABLE x (a int)",
        " x = 1", " 1", " 1 END", " (1)", " x.y", " PROC p AS SELECT 1",
        " x AS SELECT 1", " DATABASE x", " VIEW v AS SELECT 1", " BY x",
        " JOIN y ON x.a = y.a", " NULL", " KEY", " ON x TO y", " OFF", ")",

        // ALTER TABLE t ALTER 在剖析器眼中直接是語法錯誤——它要看到 COLUMN 才收。
        // 少了這一條，ALTER 就不會分到 AlterTableAction，而「猜錯位置的代價是使用者
        // 永遠打不出來」。續尾取聯集，多一條只會讓分類更寬鬆。
        " COLUMN x int",

        // BEGIN DISTRIBUTED 同理：後面不是 TRAN／TRANSACTION 就是語法錯誤。
        " TRANSACTION",

        // NEXT 是非保留字，要有 NEXT VALUE FOR 才分得出它不是欄位名稱。
        " VALUE FOR s",

        // 權限 ON 之後的類別（OBJECT、TYPE）是非保留字，要有 :: 才分得出它不是物件名稱。
        "::x TO y",
    ];

    // 片語的續尾在第三階段那一組之外多幾條：SET 選項值、選項清單的 = ON、字串與括號的結尾，
    // 以及幾個要多看一個詞元才分得出來的地方（AFTER 後面沒有 INSERT 就是語法錯誤）。
    // 模組選項的名稱要看到本體才驗（寫到檔案結尾為止任何名稱都過），所以函式的兩種本體也在。
    // CREATE LOGIN 的 WITH PASSWORD 只收字串，DBCC 的 WITH 只收它自己的選項。
    // DECRYPTION BY 之後的 ASYMMETRIC、SYMMETRIC 要看到金鑰名稱才驗。
    // 登入與使用者的選項收名稱值（DEFAULT_DATABASE = x）與二進位值（SID = 0x01），而 WITH 之後寫不完時剖析器回頭在 WITH 報錯，要整句寫得完才算。
    internal static readonly string[] Phrases =
    [
        .. Keywords,
        " ON", " 'x'", " = ON", " = ON)", " = 1", " ON)", " ROWS ONLY", " NO_INFOMSGS",
        " PRECEDING)", " ZONE 'UTC'", " IN (1)", " FOR SELECT 1", " ACTION)",
        " (a)", " TIES a FROM t ORDER BY a", " FROM x", " INSERT AS SELECT 1", " OF INSERT AS SELECT 1",
        " LEVEL READ COMMITTED", " READ COMMITTED", " COMMITTED", " READ", " TRIGGER ALL",
        " AS BEGIN RETURN 1 END", " AS RETURN SELECT 1 AS a", " = 'x'", " KEY x", " = x", " = 0x01",
    ];

    // 值的代表寫法，依序試：數值、字串。手寫的值另外還要開得了一組清單（索引鍵之後的 WITH 只接 `(`）。
    internal static readonly string[] ValueSamples = ["1", "'x'"];

    internal static readonly string[] ValueEndings = [.. Phrases, " ("];

    // 非保留字的對照名稱：不是任何關鍵字的普通識別字。
    internal const string PlainName = "Lib_Reader";

    // 封閉那一格收不收變數的對照名稱：SET 之後除了選項還接 @ReaderId = 1。
    internal const string PlainVariable = "@ReaderId";
}
