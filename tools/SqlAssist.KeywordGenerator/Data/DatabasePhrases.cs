namespace SqlAssist.KeywordGenerator.Data;

/// <summary>資料庫與伺服器：檔案規格、ALTER DATABASE SET 的括號選項、ALTER SERVER CONFIGURATION、資源集區。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class DatabasePhrases
{
    // 檔案規格的探測文字：剖析器要 FILEGROUP、LOG ON 前面先寫過一組。
    private const string FileSpec = "(NAME = a, FILENAME = 'x')";

    internal static readonly PhraseDeclaration[] Files =
    [
        // 檔案規格一組一組寫下去（ON (…), (…), FILEGROUP g (…) LOG ON (…)），中段的 ,* 走過前面幾組，第幾組的括號裡都是同一份項。
        // 剖析器收任何順序的項，SIZE、MAXSIZE、FILEGROWTH 也寫得成第一項，所以單位（MB、UNLIMITED）由展開兩層探出來。
        // 一組寫完之後是 LOG、COLLATE、FOR ATTACH。整句已經完整，WITH 也是 CTE 的開頭被扣掉了；手寫的值要接得上一組續尾，
        // 而 WITH 之後的資料庫選項（TRUSTWORTHY ON）共用續尾寫不出來，沒有補。
        // SSMS 產生的指令碼在名稱與 ON 之間寫 CONTAINMENT = NONE：標頭有兩種寫法，各宣告一份。
        new("CREATE DATABASE {name} CONTAINMENT ="),
        .. CreateDatabase("CREATE DATABASE {name} ON"),
        .. CreateDatabase("CREATE DATABASE {name} CONTAINMENT = {name} ON"),
        new("ALTER DATABASE {name} ADD FILE ,* (*") { Expand = 2 },
        new("ALTER DATABASE {name} ADD FILE ,* ()") { Group = FileSpec, Expand = 1 },
        new("ALTER DATABASE {name} ADD LOG FILE ,* (*") { Expand = 2 },
        new("ALTER DATABASE {name} MODIFY FILE (*") { Expand = 2 },

        // Azure SQL Database 的服務層級：EDITION、SERVICE_OBJECTIVE、MAXSIZE 寫在資料庫名稱之後的括號裡。
        new("CREATE DATABASE {name} (*") { Expand = 2 },
        new("ALTER DATABASE {name} MODIFY (*") { Expand = 2 },
    ];

    private static PhraseDeclaration[] CreateDatabase(string head) =>
    [
        new(head),
        new(head + " ,*") { Endings = [" " + FileSpec] },
        new(head + " ,* (*") { Expand = 2 },
        new(head + " ,* ()") { Group = FileSpec, Expand = 1 },
        new(head + " ,* FILEGROUP {name}") { Gap = FileSpec + ",", Expand = 2 },
    ];

    internal static readonly PhraseDeclaration[] SetOptions =
    [
        // ALTER DATABASE SET 的括號選項：值是識別字（AUTO、READ_WRITE）或數值帶單位（30 MINUTES），展開一兩層探。
        // CHANGE_RETENTION 少了單位就剖析不過，續尾由 Endings 補。選項裡再開的括號（CLEANUP_POLICY = (…)）另寫；
        // QUERY_CAPTURE_MODE = CUSTOM 與它的 QUERY_CAPTURE_POLICY 剖析器還不認得。
        new("ALTER DATABASE {name} SET TARGET_RECOVERY_TIME = {value}"),
        new("ALTER DATABASE {name} SET CHANGE_TRACKING (*") { Expand = 2, Endings = [" = 2 DAYS"] },
        new("ALTER DATABASE {name} SET CHANGE_TRACKING = ON (*") { Expand = 2, Endings = [" = 2 DAYS"] },
        new("ALTER DATABASE {name} SET QUERY_STORE (*") { Expand = 1 },
        new("ALTER DATABASE {name} SET QUERY_STORE = ON (*") { Expand = 1 },
        new("ALTER DATABASE {name} SET QUERY_STORE (* CLEANUP_POLICY = (*"),
        new("ALTER DATABASE {name} SET QUERY_STORE = ON (* CLEANUP_POLICY = (*"),
    ];

    internal static readonly PhraseDeclaration[] Server =
    [
        // ALTER SERVER CONFIGURATION SET 之後的分支：PROCESS AFFINITY、DIAGNOSTICS LOG、FAILOVER CLUSTER PROPERTY、
        // HADR CLUSTER CONTEXT、BUFFER POOL EXTENSION、SOFTNUMA。CONFIGURATION 後面非接 SET 不可，逐字探不出來，整段是證據。
        // MEMORY_OPTIMIZED、SUSPEND_FOR_SNAPSHOT_BACKUP 剖析器還不認得，探不出來。緩衝集區的 SIZE 少了單位剖析不過，續尾由 Endings 補。
        new("ALTER SERVER CONFIGURATION SET") { Expand = 4 },
        new("ALTER SERVER CONFIGURATION SET BUFFER POOL EXTENSION ON (*") { Expand = 2, Endings = [" = 1 GB"] },

        // 資源集區的 WITH (…)：AFFINITY 之後是 SCHEDULER、NUMANODE（外部集區是 CPU），再之後的等號接 AUTO。
        new("CREATE RESOURCE POOL {name} WITH (*") { Expand = 2 },
        new("ALTER RESOURCE POOL {name} WITH (*") { Expand = 2 },
        new("CREATE EXTERNAL RESOURCE POOL {name} WITH (*") { Expand = 2 },
        new("ALTER EXTERNAL RESOURCE POOL {name} WITH (*") { Expand = 2 },

        // 工作負載群組的 WITH (…)：IMPORTANCE 之後是 LOW、MEDIUM、HIGH，括號之後是 USING 資源集區。
        // 群組名稱之後的語句已經完整，WITH 被當成 CTE 的開頭扣掉，由整段的證據補回。
        new("CREATE WORKLOAD GROUP {name} WITH (*") { Expand = 2 },
        new("ALTER WORKLOAD GROUP {name} WITH (*") { Expand = 2 },
        new("CREATE WORKLOAD GROUP {name} WITH ()") { Group = "(IMPORTANCE = HIGH)" },
        new("ALTER WORKLOAD GROUP {name} WITH ()") { Group = "(IMPORTANCE = HIGH)" },
    ];
}
