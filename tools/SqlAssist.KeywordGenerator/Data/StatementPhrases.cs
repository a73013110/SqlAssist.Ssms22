using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>一般敘述：SET、BACKUP／RESTORE、統計資料、交易、EXECUTE AS、WAITFOR、DBCC、EXEC 的選項清單。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class StatementPhrases
{
    private static readonly string[] DateFormats = ["mdy", "dmy", "ymd", "ydm", "myd", "dym"];

    private static readonly string[] DeadlockPriorities = ["LOW", "NORMAL", "HIGH"];

    internal static readonly PhraseDeclaration[] Set =
    [
        new("SET") { Expand = 4 },
        new("SET IDENTITY_INSERT {name}"),
        new("SET DATEFORMAT") { Values = DateFormats, Closed = true },
        new("SET DEADLOCK_PRIORITY") { Values = DeadlockPriorities, Closed = true },

        // 一次可以設幾個選項（SET LANGUAGE 'x', DATEFORMAT dmy），值的那一格也可能在逗號之後：中段的 ,* 走過前面幾項。
        // 逗號之後以尾巴認，不寫成清單片語：那樣 SET 之後就成了 OptionItem，SetTarget 收的變數列不出來。
        new("SET ,* ,"),
        new("SET ,* , DATEFORMAT") { Gap = "LANGUAGE 'x'", Values = DateFormats, Closed = true },
        new("SET ,* , DEADLOCK_PRIORITY") { Gap = "LANGUAGE 'x'", Values = DeadlockPriorities, Closed = true },
    ];

    internal static readonly PhraseDeclaration[] BackupHeaders =
    [
        // BACKUP／RESTORE 的標頭：名稱之後是 TO／FROM 與檔案、檔案群組，TO／FROM 之後是裝置種類
        // （DISK、URL、TAPE）。HEADERONLY 這一族也走到 FROM 之後；DATABASE、LOG 之後的名稱是展開的一步。
        new("BACKUP") { Expand = 2 },
        new("RESTORE") { Expand = 2 },
        // SERVICE MASTER KEY 的 MASTER 剖析器當名稱讀，手寫。
        new("BACKUP SERVICE") { Values = ["MASTER"] },
        new("RESTORE SERVICE") { Values = ["MASTER"] },

        // 備份對象：資料庫名稱之後、TO／FROM 之前可以列幾個檔案或檔案群組（FILE = 'a', FILEGROUP = 'b'），也是逗號清單。
        // 標頭寫到名稱為止，TO、FROM 開的是裝置清單，不是這份清單的項。
        new("BACKUP DATABASE {name} ,*"),
        new("RESTORE DATABASE {name} ,*"),
        new("RESTORE LOG {name} ,*"),

        // 裝置清單：TO／FROM 之後一項一個裝置，每一項都能換種類（DISK、TAPE、URL、邏輯裝置名稱或變數）。一個裝置寫完
        // （值、名稱或變數）之後是逗號、BACKUP 的 MIRROR TO 或 WITH；MIRROR TO 之後是同一種清單，中段的 ,* 往回走過前面的裝置
        // 與 MIRROR TO 找到標頭。名稱與 TO 之間夾著備份對象時以 ... 跨過，探測墊一個檔案：只墊名稱的話與展開出來的
        // BACKUP DATABASE {name} TO 是同一格，兩個片語互搶比對。BACKUP LOG 沒有備份對象。
        // 裝置寫成 {value} 不寫 {name}：DISK 是關鍵字、不是運算元，TO DISK 之後不會比對成寫完一個裝置。
        // 寫完一個裝置整句已經完整，WITH 也是 CTE 的開頭被扣掉了，手寫補回；續尾寫一個兩邊都收的選項驗它。
        .. Devices("BACKUP DATABASE ... TO", BackupTarget),
        .. Devices("BACKUP LOG {name} TO", null),
        .. Devices("RESTORE DATABASE ... FROM", BackupTarget),
        .. Devices("RESTORE LOG ... FROM", BackupTarget),
        new("BACKUP DATABASE ... TO ,* {value} MIRROR TO") { Gap = BackupTarget },
        new("BACKUP LOG {name} TO ,* {value} MIRROR TO"),
    ];

    private const string BackupTarget = "t FILE = 'x'";

    private static PhraseDeclaration[] Devices(string head, string? gap) =>
    [
        new(head + " ,*") { Gap = gap },
        new(head + " ,* {value}") { Gap = gap, Values = ["WITH"], Endings = [" STATS"] },
    ];

    internal static readonly PhraseDeclaration[] BackupOptions =
    [
        // BACKUP／RESTORE 的 WITH 選項清單：兩者的選項不同，由標頭分開。BACKUP CERTIFICATE 接的是別的選項，不在這裡。
        // RESTORE 的 MOVE 要寫完 'a' TO 'b' 才是一項，續尾把它寫完。
        new("BACKUP DATABASE ... WITH ,*") { Gap = "d TO DISK = 'x'" },
        new("BACKUP LOG ... WITH ,*") { Gap = "d TO DISK = 'x'" },
        new("RESTORE DATABASE ... WITH ,*") { Gap = "d FROM DISK = 'x'", Endings = [" 'a' TO 'b'"] },
        new("RESTORE LOG ... WITH ,*") { Gap = "d FROM DISK = 'x'", Endings = [" 'a' TO 'b'"] },

        // 備份加密 ENCRYPTION (ALGORITHM = …, SERVER CERTIFICATE = …) 是 BACKUP 選項清單裡的一項，從 OptionItem 寫起（BACKUP 的樣板）。
        new("ENCRYPTION (*") { After = ["OptionItem"], Template = 8 },
        new("ENCRYPTION (* ALGORITHM =") { After = ["OptionItem"], Template = 8 },
        new("ENCRYPTION (* SERVER") { After = ["OptionItem"], Template = 8, Items = "ALGORITHM = AES_256, ", Expand = 1 },
    ];

    internal static readonly PhraseDeclaration[] Statistics =
    [
        // CREATE／UPDATE STATISTICS 的 WITH 選項清單：標頭夾著資料行清單與篩選 WHERE，以 ... 跨過。
        // SAMPLE 要寫完 n PERCENT|ROWS 才是一項，數值之後的單位從 OptionItem 寫起（CREATE STATISTICS 的樣板）。
        // OptionItem 不放 UPDATE STATISTICS 的樣板：WITH ALL、WITH INDEX 寫完就是一句，ALL、INDEX 會被判成寫完一項的字。
        new("CREATE STATISTICS ... WITH ,*") { Gap = "s ON t (a)" },
        new("UPDATE STATISTICS ... WITH ,*") { Gap = "t" },
        new("SAMPLE {value}") { After = ["OptionItem"], Template = 14 },
        // RESAMPLE ON PARTITIONS (…) 只有 UPDATE STATISTICS 收，OptionItem 沒有它的樣板（理由同上），以尾巴認。
        new("RESAMPLE ON") { Lead = "UPDATE STATISTICS t WITH " },
    ];

    internal static readonly PhraseDeclaration[] Transactions =
    [
        // 交易的 COMMIT [TRAN | TRANSACTION [名稱]] WITH (DELAYED_DURABILITY = ON)。交易名稱以 ... 跨過、不寫 {name}：
        // 證據會立 COMMIT TRAN {name}，名稱格也比對得上保留字，IF … COMMIT TRAN ELSE 的 ELSE 會被當成交易名稱、只列 WITH。
        .. new[] { "", " TRAN", " TRANSACTION" }.Select(middle => new PhraseDeclaration($"COMMIT{middle} WITH (*") { Expand = 1 }),
        new("COMMIT TRAN ... WITH (*") { Gap = "t" },
        new("COMMIT TRANSACTION ... WITH (*") { Gap = "t" },
    ];

    internal static readonly PhraseDeclaration[] ExecuteAs =
    [
        new("EXECUTE AS"),
        new("EXEC AS"),
        new("ENABLE TRIGGER") { Closed = false },
        new("DISABLE TRIGGER") { Closed = false },
    ];

    internal static readonly PhraseDeclaration[] Waitfor =
    [
        new("WAITFOR"),
        // Service Broker 的 WAITFOR (RECEIVE …)、(GET CONVERSATION GROUP …)，關上之後的逗號接 TIMEOUT。
        // 括號裡是一句敘述，逗號屬於 RECEIVE 的選取清單：寫成單獨的 (。
        // 不展開：RECEIVE 之後是選取清單，展開會立成只列幾個字的封閉片語。GET CONVERSATION GROUP 那一段是證據。
        new("WAITFOR ("),
        new("WAITFOR ( " + ServiceBrokerPhrases.GetConversationGroup),
        new("WAITFOR ( " + ServiceBrokerPhrases.GetConversationGroup + " {name} FROM"),
        new("WAITFOR () ,") { Group = "(RECEIVE * FROM q)" },

        // KILL 之後是工作階段、工作單位或這兩種寫法：QUERY、STATS 剖析器要看到整段才收，整段是證據。
        new("KILL QUERY NOTIFICATION SUBSCRIPTION ALL"),
        new("KILL STATS JOB {value}"),
    ];

    internal static readonly PhraseDeclaration[] Dbcc =
    [
        // DBCC 之後的命令剖析器什麼名稱都收（未公開的命令、DBCC dllname (FREE)），探不出字；
        // 字來自語句說明登錄的命令（見探測之後那一段），由人宣告封閉：那一格不是任何物件的名稱。
        // 命令寫完已經是完整的一句，WITH 同時是 CTE 的開頭被扣掉了，由下面的清單片語補回。
        // 選項不分命令：剖析器對任何命令都收同一份，命令名稱探測時是 t。
        // 命令括號裡的關鍵字（CHECKIDENT 的 RESEED）同樣探不出來，由語句說明的語法給（見探測之後那一段）。
        new("DBCC") { Closed = true },
        new("DBCC {name}"),
        new("DBCC {name} ()"),
        new("DBCC {name} WITH ,*"),
        new("DBCC {name} () WITH ,*"),
        new("RAISERROR () WITH ,*") { Group = "('x', 16, 1)" },
    ];

    internal static readonly PhraseDeclaration[] ExecOptions =
    [
        // EXEC 的 WITH 選項清單：程序與 WITH 之間夾著長度不定的參數清單。
        new("EXEC ... WITH ,*") { Gap = "p" },
        new("EXECUTE ... WITH ,*") { Gap = "p" },

        // RESULT SETS 是 EXEC 選項清單裡的一項，之後是 NONE、UNDEFINED 或結果集清單，RESULT 之後的 SETS 由這一條補出來；
        // 清單裡 AS 之後是 OBJECT、TYPE、FOR（XML），資料行型別之後的 NOT 只接 NULL。
        new("RESULT SETS") { After = ["OptionItem"], Template = 2 },
        new("AS") { After = ["ResultSetList"], Expand = 1 },
        new("NOT") { After = ["ResultSetColumnTail"] },
    ];
}
