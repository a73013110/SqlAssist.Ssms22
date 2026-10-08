using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>資料庫與伺服器：檔案規格、ALTER DATABASE SET 的括號選項、ALTER SERVER CONFIGURATION、資源集區。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class DatabasePhrases
{
    // 檔案規格的探測文字：剖析器要 FILEGROUP、LOG ON 前面先寫過一組。
    private const string FileSpec = "(NAME = a, FILENAME = 'x')";

    // 靜態欄位依宣告順序初始化，要寫在用到它的清單前面。
    private static readonly string[] CreateDatabaseHeads = ["CREATE DATABASE {name}", "CREATE DATABASE {name} CONTAINMENT = {name}"];

    internal static readonly PhraseDeclaration[] Files =
    [
        // 檔案規格一組一組寫下去（ON (…), (…), FILEGROUP g (…) LOG ON (…)），中段的 ,* 走過前面幾組，第幾組的括號裡都是同一份項。
        // 剖析器收任何順序的項，SIZE、MAXSIZE、FILEGROWTH 也寫得成第一項，所以單位（MB、UNLIMITED）由展開兩層探出來。
        // 一組寫完之後是 LOG、COLLATE、FOR ATTACH 與 WITH。整句已經完整，WITH 也是 CTE 的開頭被扣掉了，手寫補回。
        // SSMS 產生的指令碼在名稱與 ON 之間寫 CONTAINMENT = NONE：標頭有兩種寫法，各宣告一份。
        new("CREATE DATABASE {name} CONTAINMENT ="),
        .. CreateDatabaseHeads.SelectMany(head => CreateDatabase(head + " ON")),
        // 資料庫快照集：檔案規格之後是 AS SNAPSHOT OF 來源資料庫。SNAPSHOT 剖析器要讀到 OF 才收，寫到 SNAPSHOT 的整段是證據。
        new("CREATE DATABASE {name} ON ,* () AS SNAPSHOT") { Group = FileSpec },

        // WITH 之後的資料庫選項（LEDGER = ON、TRUSTWORTHY ON、FILESTREAM (…)）是逗號清單。WITH 寫在名稱、COLLATE、檔案規格
        // 或 FOR ATTACH 之後，前面那段由 ... 走過（拿 COLLATE 探：檔案規格之後的 WITH 那一格已由上面的 ,* () 立起）。
        // 名稱寫完整句已經完整，WITH 也是 CTE 的開頭被扣掉了，手寫補回，續尾寫一個選項驗它。
        // FILESTREAM 之後的括號與 NON_TRANSACTED_ACCESS 的值共用續尾寫不完，由 Endings 補。
        // PERSISTENT_LOG_BUFFER = ON (DIRECTORY_NAME = …) 剖析器還不認得。
        .. CreateDatabaseHeads.Select(head => new PhraseDeclaration(head) { Values = ["WITH"], Endings = [" TRUSTWORTHY ON"] }),
        new("CREATE DATABASE ... WITH ,*") { Gap = "t COLLATE Latin1_General_CI_AS", Endings = [FileStreamOptions] },
        FileStream("CREATE DATABASE ... WITH ,*", "t COLLATE Latin1_General_CI_AS"),

        new("ALTER DATABASE {name} ADD FILE ,* (*") { Expand = 2 },
        new("ALTER DATABASE {name} ADD FILE ,* ()") { Group = FileSpec, Expand = 1 },
        new("ALTER DATABASE {name} ADD LOG FILE ,* (*") { Expand = 2 },
        new("ALTER DATABASE {name} MODIFY FILE (*") { Expand = 2 },

        // 檔案群組：ADD FILEGROUP g 之後是 CONTAINS FILESTREAM／MEMORY_OPTIMIZED_DATA，MODIFY FILEGROUP g 之後是
        // READ_ONLY、READ_WRITE、DEFAULT、AUTOGROW_SINGLE_FILE、AUTOGROW_ALL_FILES 與 NAME =。
        new("ALTER DATABASE {name} ADD FILEGROUP {name}") { Expand = 2 },
        new("ALTER DATABASE {name} MODIFY FILEGROUP {name}") { Expand = 1 },

        // Azure SQL Database 的服務層級：EDITION、SERVICE_OBJECTIVE、MAXSIZE 寫在資料庫名稱之後的括號裡，複製資料庫也一樣。
        // SERVICE_OBJECTIVE = 之後是值，展開不往下走；彈性集區那一組括號手寫（值之後要關兩層括號），ELASTIC_POOL 由整段的證據補回等號之後。
        new("CREATE DATABASE {name} (*") { Expand = 2 },
        new("CREATE DATABASE {name} COLLATE {name} (*") { Expand = 2 },
        new("CREATE DATABASE {name} (* SERVICE_OBJECTIVE = ELASTIC_POOL (*") { Endings = [" = p))"] },
        new("CREATE DATABASE {name} AS COPY OF {name} (*") { Expand = 2 },
        new("CREATE DATABASE {name} AS COPY OF {name} (* SERVICE_OBJECTIVE = ELASTIC_POOL (*") { Endings = [" = p))"] },
        new("ALTER DATABASE {name} MODIFY (*") { Expand = 2 },
        new("ALTER DATABASE {name} MODIFY (* SERVICE_OBJECTIVE = ELASTIC_POOL (*") { Endings = [" = p))"] },
    ];

    // WITH 之後的資料庫選項：共用續尾（ON、= ON）寫得完 TRUSTWORTHY、LEDGER，FILESTREAM 要一組括號。
    private const string DatabaseOption = " TRUSTWORTHY ON";
    private const string FileStreamOptions = " (NON_TRANSACTED_ACCESS = OFF)";

    // FILESTREAM (NON_TRANSACTED_ACCESS = …, DIRECTORY_NAME = …)：CREATE DATABASE 的 WITH 與 ALTER DATABASE 的 SET 同一份。
    private static PhraseDeclaration FileStream(string head, string? gap = null) =>
        new(head + " FILESTREAM (*") { Gap = gap, Endings = [" = OFF)"] };

    private static PhraseDeclaration[] CreateDatabase(string head) =>
    [
        new(head),
        new(head + " ,*") { Endings = [" " + FileSpec] },
        new(head + " ,* (*") { Expand = 2 },
        new(head + " ,* ()") { Group = FileSpec, Expand = 1, Values = ["WITH"], Endings = [DatabaseOption, FileStreamOptions] },
        new(head + " ,* FILEGROUP {name}") { Gap = FileSpec + ",", Expand = 2 },
    ];

    // 值寫成 {name}：探測代入那一格列得出的第一個字，執行期 ON 與 OFF 都比對得上。
    private static readonly string[] TerminableOptions =
    [
        "ONLINE", "OFFLINE", "EMERGENCY", "SINGLE_USER", "RESTRICTED_USER", "MULTI_USER", "READ_ONLY", "READ_WRITE",
        "DELAYED_DURABILITY = {name}", "TARGET_RECOVERY_TIME = {value} {name}", "READ_COMMITTED_SNAPSHOT {name}",
        "MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT = {name}", "DATE_CORRELATION_OPTIMIZATION {name}", "PARAMETERIZATION {name}",
        "CHANGE_TRACKING = {name}", "ACCELERATED_DATABASE_RECOVERY = {name}", "OPTIMIZED_LOCKING = {name}",
    ];

    internal static readonly PhraseDeclaration[] SetOptions =
    [
        // ALTER DATABASE SET 的括號選項：值是識別字（AUTO、READ_WRITE）或數值帶單位（30 MINUTES），展開一兩層探。
        // CHANGE_RETENTION 少了單位就剖析不過，續尾由 Endings 補。選項裡再開的括號（CLEANUP_POLICY = (…)）另寫；
        // QUERY_CAPTURE_MODE = CUSTOM 與它的 QUERY_CAPTURE_POLICY 剖析器還不認得。
        // SET 一次寫得了幾個選項（SET AUTO_UPDATE_STATISTICS ON, AUTO_CREATE_STATISTICS ON (INCREMENTAL = ON)），中段的 ,* 走過前面幾個。
        // 逗號之後的下一個選項：少了這一條只比對得到工作階段 SET 的 SET ,* ,，列的是 NOCOUNT 那一類。
        new("ALTER DATABASE {name} SET ,* ,"),
        new("ALTER DATABASE {name} SET ,* TARGET_RECOVERY_TIME = {value}"),
        new("ALTER DATABASE {name} SET ,* AUTO_CREATE_STATISTICS ON (*"),
        new("ALTER DATABASE {name} SET ,* CHANGE_TRACKING (*") { Expand = 2, Endings = [" = 2 DAYS"] },
        new("ALTER DATABASE {name} SET ,* CHANGE_TRACKING = ON (*") { Expand = 2, Endings = [" = 2 DAYS"] },
        new("ALTER DATABASE {name} SET ,* QUERY_STORE (*") { Expand = 1 },
        new("ALTER DATABASE {name} SET ,* QUERY_STORE = ON (*") { Expand = 1 },
        new("ALTER DATABASE {name} SET ,* QUERY_STORE (* CLEANUP_POLICY = (*"),
        new("ALTER DATABASE {name} SET ,* QUERY_STORE = ON (* CLEANUP_POLICY = (*"),
        new("ALTER DATABASE {name} SET ,* AUTOMATIC_TUNING (*"),
        FileStream("ALTER DATABASE {name} SET ,*"),

        // 終止子句 WITH ROLLBACK AFTER n SECONDS、ROLLBACK IMMEDIATE、NO_WAIT。選項寫完這一句已經完整，WITH 也是 CTE 的開頭
        // 被扣掉了：寫到 WITH 的整段是證據，WITH 補進選項那一格（那一格還不是片語就另立）。剖析器在任何選項之後都收，
        // 名單照文件「可以用 WITH <termination>」那一欄。WITH ROLLBACK 之後的尾巴與選項無關，T-SQL 也只有這裡寫得出 WITH ROLLBACK，
        // 判不出前一格照樣認得出意思，從那裡寫起。
        .. TerminableOptions.Select(option => new PhraseDeclaration($"ALTER DATABASE {{name}} SET ,* {option} WITH")),
        new("WITH ROLLBACK") { Lead = "ALTER DATABASE t SET SINGLE_USER " },
        new("WITH ROLLBACK AFTER {value}") { Lead = "ALTER DATABASE t SET SINGLE_USER " },
    ];

    internal static readonly PhraseDeclaration[] AvailabilityGroups =
    [
        // 資料庫加入可用性群組：SET HADR 之後的 AVAILABILITY GROUP = 剖析器要看到整段才收，整段是證據。
        new("ALTER DATABASE {name} SET HADR AVAILABILITY GROUP ="),

        // 可用性複本：ADD、MODIFY 的 ON 之後是一或多個 '伺服器' WITH (…)，中段的 ,* 走過前面幾個複本。括號裡的選項等號之後是值
        // （FAILOVER_MODE = MANUAL），角色選項再開一組括號。SEEDING_MODE、AUTOMATED_BACKUP_PREFERENCE 這幾個剖析器還不認得。
        // CREATE 的複本寫在 FOR DATABASE 清單之後，前面那段由 ... 走過。
        new("ALTER AVAILABILITY GROUP {name} ADD REPLICA ON ,* {value} WITH (*"),
        new("ALTER AVAILABILITY GROUP {name} ADD REPLICA ON ,* {value} WITH (* SECONDARY_ROLE (*"),
        new("ALTER AVAILABILITY GROUP {name} ADD REPLICA ON ,* {value} WITH (* PRIMARY_ROLE (*"),
        new("ALTER AVAILABILITY GROUP {name} MODIFY REPLICA ON ,* {value} WITH (*"),
        new("ALTER AVAILABILITY GROUP {name} MODIFY REPLICA ON ,* {value} WITH (* SECONDARY_ROLE (*"),
        new("ALTER AVAILABILITY GROUP {name} MODIFY REPLICA ON ,* {value} WITH (* PRIMARY_ROLE (*"),
        // FOR DATABASE 的資料庫清單寫完接 REPLICA；中段的 ,* 走過前面幾個資料庫。REPLICA 要寫完一個複本才驗，續尾補上。
        new("CREATE AVAILABILITY GROUP ... DATABASE ,* {name}")
        {
            Gap = "t FOR",
            Endings = [" ON 'x' WITH (ENDPOINT_URL = 'TCP://x:5022', AVAILABILITY_MODE = SYNCHRONOUS_COMMIT, FAILOVER_MODE = AUTOMATIC)"],
        },
        new("CREATE AVAILABILITY GROUP ... REPLICA ON ,* {value} WITH (*") { Gap = "t FOR DATABASE d" },
        new("CREATE AVAILABILITY GROUP ... REPLICA ON ,* {value} WITH (* SECONDARY_ROLE (*") { Gap = "t FOR DATABASE d" },
        new("CREATE AVAILABILITY GROUP ... REPLICA ON ,* {value} WITH (* PRIMARY_ROLE (*") { Gap = "t FOR DATABASE d" },
    ];

    internal static readonly PhraseDeclaration[] Server =
    [
        // ALTER SERVER CONFIGURATION SET 之後的分支：PROCESS AFFINITY、DIAGNOSTICS LOG、FAILOVER CLUSTER PROPERTY、
        // HADR CLUSTER CONTEXT、BUFFER POOL EXTENSION、SOFTNUMA。CONFIGURATION 後面非接 SET 不可，逐字探不出來，整段是證據。
        // MEMORY_OPTIMIZED、SUSPEND_FOR_SNAPSHOT_BACKUP 剖析器還不認得，探不出來。緩衝集區的 SIZE 少了單位剖析不過，續尾由 Endings 補。
        new("ALTER SERVER CONFIGURATION SET") { Expand = 4 },
        new("ALTER SERVER CONFIGURATION SET BUFFER POOL EXTENSION ON (*") { Expand = 2, Endings = [" = 1 GB"] },
        // FILENAME 要寫在第一項，SIZE 只出現在逗號之後，展開走不到它的單位（KB、MB、GB）。
        new("ALTER SERVER CONFIGURATION SET BUFFER POOL EXTENSION ON (* SIZE =") { Items = "FILENAME = 'x', ", Expand = 1 },

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
        // USING 之後是資源集區，外部資源集區以逗號與 EXTERNAL 接在後面，也可以只寫外部那一個。
        // WITH (…) 可省：每一種標頭各宣告一份，不以 ... 跨過。
        .. new[] { "CREATE", "ALTER" }.SelectMany(verb => new[] { "", " WITH ()" }
            .SelectMany(options => new[] { " USING", " USING {name} ," }
                .Select(tail => new PhraseDeclaration($"{verb} WORKLOAD GROUP {{name}}{options}{tail}")
                {
                    Group = options.Length == 0 ? null : "(IMPORTANCE = HIGH)",
                }))),
    ];
}
