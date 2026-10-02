namespace SqlAssist.KeywordGenerator.Data;

/// <summary>一般敘述：SET、BACKUP／RESTORE、EXECUTE AS、WAITFOR、DBCC、EXEC 的選項清單。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class StatementPhrases
{
    internal static readonly PhraseDeclaration[] Set =
    [
        new("SET") { Expand = 4 },
        new("SET IDENTITY_INSERT {name}"),
        new("SET DATEFORMAT") { Values = ["mdy", "dmy", "ymd", "ydm", "myd", "dym"], Closed = true },
        new("SET DEADLOCK_PRIORITY") { Values = ["LOW", "NORMAL", "HIGH"], Closed = true },
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
    ];

    internal static readonly PhraseDeclaration[] BackupOptions =
    [
        // BACKUP／RESTORE 的 WITH 選項清單：兩者的選項不同，由標頭分開。BACKUP CERTIFICATE 接的是別的選項，不在這裡。
        new("BACKUP DATABASE ... WITH ,*") { Gap = "d TO DISK = 'x'" },
        new("BACKUP LOG ... WITH ,*") { Gap = "d TO DISK = 'x'" },
        new("RESTORE DATABASE ... WITH ,*") { Gap = "d FROM DISK = 'x'" },
        new("RESTORE LOG ... WITH ,*") { Gap = "d FROM DISK = 'x'" },
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
