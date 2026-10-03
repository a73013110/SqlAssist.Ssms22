namespace SqlAssist.KeywordGenerator.Data;

/// <summary>DDL：物件種類、資料庫範圍設定、觸發程序與模組選項、資料表、序列、條件約束。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class DdlPhrases
{
    internal static readonly string[] ModuleOptions = ["ProcedureOption", "FunctionOption", "ViewOption", "TriggerOption"];

    internal static readonly string[] ScopedConfigurations =
    [
        "ACCELERATED_PLAN_FORCING", "ASYNC_STATS_UPDATE_WAIT_AT_LOW_PRIORITY", "BATCH_MODE_ADAPTIVE_JOINS",
        "BATCH_MODE_MEMORY_GRANT_FEEDBACK", "BATCH_MODE_ON_ROWSTORE", "CE_FEEDBACK", "DEFERRED_COMPILATION_TV",
        "DOP_FEEDBACK", "ELEVATE_ONLINE", "ELEVATE_RESUMABLE", "EXEC_QUERY_STATS_FOR_SCALAR_FUNCTIONS",
        "GLOBAL_TEMPORARY_TABLE_AUTO_DROP", "IDENTITY_CACHE", "INTERLEAVED_EXECUTION_TVF", "ISOLATE_SECURITY_POLICY_CARDINALITY",
        "LAST_QUERY_PLAN_STATS", "LEDGER_DIGEST_STORAGE_ENDPOINT", "LEGACY_CARDINALITY_ESTIMATION", "LIGHTWEIGHT_QUERY_PROFILING",
        "MAXDOP", "MEMORY_GRANT_FEEDBACK_PERCENTILE_GRANT", "MEMORY_GRANT_FEEDBACK_PERSISTENCE", "OPTIMIZE_FOR_AD_HOC_WORKLOADS",
        "OPTIMIZED_PLAN_FORCING", "OPTIMIZED_SP_EXECUTESQL", "PARAMETER_SENSITIVE_PLAN_OPTIMIZATION", "PARAMETER_SNIFFING",
        "PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES", "QUERY_OPTIMIZER_HOTFIXES", "ROW_MODE_MEMORY_GRANT_FEEDBACK",
        "TSQL_SCALAR_UDF_INLINING", "VERBOSE_TRUNCATION_WARNINGS", "XTP_PROCEDURE_EXECUTION_STATISTICS", "XTP_QUERY_EXECUTION_STATISTICS",
    ];

    internal static readonly PhraseDeclaration[] Objects =
    [
        // CREATE、ALTER、DROP 之後是物件種類（Kinds）：展開到名稱為止，名稱之後只列一層（CREATE TABLE t 之後的 AS），
        // 更深的標頭由下面各敘述自己宣告。CREATE 的名稱是新名字，那一格封閉，種類輸出給執行期判新名字；
        // CREATE OR 探不出 ALTER（剖析器要看到整段才收），CREATE OR ALTER 那一條是證據。三層寫得到 UNIQUE CLUSTERED INDEX、XML SCHEMA COLLECTION。
        // DROP、UPDATE、DELETE、MERGE 之後是名稱的位置，目標過濾把關鍵字全擋掉；
        // IF EXISTS、TOP、INTO 這些字只能由片語給。名稱後面要再寫一段才完整的（UPDATE t SET、
        // MERGE t USING、DROP INDEX i ON t、DROP STATISTICS t.s）探測判成封閉，由人宣告不封閉。
        new("CREATE") { Expand = 3, Kinds = ObjectKinds.New },
        new("CREATE OR ALTER") { Expand = 1, Kinds = ObjectKinds.New },
        new("ALTER") { Expand = 3, Kinds = ObjectKinds.Existing },
        new("DROP") { Expand = 3, Kinds = ObjectKinds.Existing },
        new("DROP INDEX") { Closed = false },
        new("DROP STATISTICS") { Closed = false },
        new("UPDATE") { Closed = false },
        new("DELETE"),
        new("MERGE") { Closed = false },
        // TRUNCATE 不在 Kinds 裡（後面只有 TABLE），名稱格要有片語，執行期才認得出「種類之後是既有的名稱」。
        new("TRUNCATE TABLE"),
        new("DROP") { After = ["AlterTableAction"], Expand = 2 },
        // 資料表上的觸發程序：TRIGGER 之後是既有的名稱，同樣要有名稱格片語。
        new("ENABLE TRIGGER") { After = ["AlterTableAction"] },
        new("DISABLE TRIGGER") { After = ["AlterTableAction"] },
        // XML 結構描述集合與可用性群組：XML、AVAILABILITY 剖析器要看到整段才收，CREATE、ALTER、DROP 的展開探不出來，整段是證據。
        // 從 CREATE 寫到名稱的宣告也把種類交給 CreatedKinds，權限 ON 之後的類別（XML SCHEMA COLLECTION::）由它探。
        new("CREATE XML SCHEMA COLLECTION {name} AS"),
        new("ALTER XML SCHEMA COLLECTION {name}"),
        new("DROP XML SCHEMA COLLECTION {name}"),
        new("CREATE AVAILABILITY GROUP {name}"),
        new("ALTER AVAILABILITY GROUP {name}"),
        new("DROP AVAILABILITY GROUP {name}"),
        new("ALTER DATABASE {name}"),
        new("ALTER DATABASE {name} SET") { Expand = 1 },

        // 資料庫範圍設定：SCOPED 在剖析器眼中也可以是資料庫名稱，逐字探不出來，整段是證據。
        // SET 之後的設定名稱剖析器什麼都收（認得的幾個另外剖析），只能手寫；名單取 SQL Server 的文件，不含 Synapse 的 DW_COMPATIBILITY_LEVEL。
        new("ALTER DATABASE SCOPED CONFIGURATION") { Expand = 3 },
        new("ALTER DATABASE SCOPED CONFIGURATION SET") { Values = ScopedConfigurations },
        new("ALTER DATABASE SCOPED CONFIGURATION FOR SECONDARY SET") { Values = ScopedConfigurations },
    ];

    internal static readonly PhraseDeclaration[] TriggerPositions =
    [
        // 只認位置的格子。觸發程序標頭之後是 AFTER、FOR、INSTEAD、WITH，再下一層是 OF 與 EXECUTE；
        // 事件清單與游標選項每一格都是同一個位置，第二項之後也一樣。
        new("") { After = ["TriggerHeader"], Expand = 1 },
        new("") { After = ["TriggerEvent", "CursorOption"] },
    ];

    internal static readonly PhraseDeclaration[] TriggersAndModules =
    [
        // DDL 與登入觸發程序：ON 之後是資料表、DATABASE 或 ALL SERVER。資料表之後還要寫事件才完整，
        // 探測判成封閉會把資料表名稱藏起來，由人宣告不封閉。DDL 事件（CREATE_TABLE、LOGON）依標頭而不同，
        // 寫成清單片語；資料表的 INSERT、UPDATE、DELETE 是位置 TriggerEvent。
        new("TRIGGER {name} ON") { After = ["DdlObject"], Closed = false },
        new("TRIGGER {name} ON ALL") { After = ["DdlObject"] },
        new("TRIGGER {name} ON DATABASE FOR ,*") { After = ["DdlObject"] },
        new("TRIGGER {name} ON DATABASE AFTER ,*") { After = ["DdlObject"] },
        new("TRIGGER {name} ON ALL SERVER FOR ,*") { After = ["DdlObject"] },
        new("TRIGGER {name} ON ALL SERVER AFTER ,*") { After = ["DdlObject"] },

        // 模組的 WITH 選項：四種模組的選項不同，EXECUTE AS 之後的 CALLER、SELF、OWNER 除了檢視都共用；
        // 函式的兩個多字選項寫全，中間每一格由它們補出來。
        new("") { After = ModuleOptions },
        new("EXECUTE AS") { After = ["ProcedureOption", "FunctionOption", "TriggerOption"] },
        new("EXEC AS") { After = ["ProcedureOption", "FunctionOption", "TriggerOption"] },
        new("RETURNS NULL ON NULL INPUT") { After = ["FunctionOption"] },
        new("CALLED ON NULL INPUT") { After = ["FunctionOption"] },

        // 原生編譯模組的 BEGIN ATOMIC WITH (…)：ATOMIC 不是關鍵字，由這裡的證據進區塊開頭的附加片語。
        // BEGIN x WITH ( 剖析器要一項寫完才在 x 報錯，續尾把那一項寫完，才分得出 ATOMIC 不是名稱。
        new("ATOMIC WITH (*") { After = ["BlockStart"], Endings = [" TRANSACTION ISOLATION LEVEL = SNAPSHOT)"] },
        new("ATOMIC WITH (* TRANSACTION ISOLATION LEVEL =") { After = ["BlockStart"] },
    ];

    internal static readonly PhraseDeclaration[] Tables =
    [
        // 外部索引鍵的參考動作；資料行型別之後判不出位置（CREATE TABLE t (a int IDENTITY |）。
        new("ON DELETE") { After = ["ReferencesTail"], Expand = 1 },
        new("ON UPDATE") { After = ["ReferencesTail"], Expand = 1 },
        new("NOT FOR") { Lead = "CREATE TABLE t (a int IDENTITY " },

        // 時態表：期間資料行（GENERATED ALWAYS AS ROW START）寫在型別之後，同樣判不出位置；PERIOD FOR SYSTEM_TIME
        // 是資料表層級的一項。資料表選項 WITH (…)、ALTER TABLE SET (…) 與 SYSTEM_VERSIONING = ON (…) 是括號清單。
        new("GENERATED ALWAYS AS ROW START HIDDEN") { Lead = "CREATE TABLE t (a datetime2 " },
        new("GENERATED ALWAYS AS ROW END HIDDEN") { Lead = "CREATE TABLE t (a datetime2 " },
        // AS 之後的 SUSER_SID、TRANSACTION_ID 這些字剖析器要看到 START／END 才收，逐字探不出來，整段是證據；
        // 墊的文字要與上面相同，才補得進同一個 GENERATED ALWAYS AS。
        new("GENERATED ALWAYS AS SUSER_SID START HIDDEN") { Lead = "CREATE TABLE t (a datetime2 " },
        new("GENERATED ALWAYS AS SUSER_SNAME END HIDDEN") { Lead = "CREATE TABLE t (a datetime2 " },
        new("GENERATED ALWAYS AS TRANSACTION_ID START HIDDEN") { Lead = "CREATE TABLE t (a datetime2 " },
        new("GENERATED ALWAYS AS SEQUENCE_NUMBER END HIDDEN") { Lead = "CREATE TABLE t (a datetime2 " },
        new("PERIOD FOR SYSTEM_TIME ()") { After = ["ColumnDefinition", "AlterTableAdd"], Group = "(a, b)" },
        new("CREATE TABLE {name} () WITH (*") { Group = "(a int)" },
        new("ALTER TABLE {name} SET (*"),
        new("SYSTEM_VERSIONING = ON (*") { Lead = "CREATE TABLE t (a int) WITH (" },
        new("BULK INSERT {name} FROM {value} WITH (*"),
        new("CREATE EXTERNAL TABLE {name} () WITH (*") { Group = "(a int)" },
        new("CREATE EXTERNAL TABLE {name} WITH (*"),

        // 資料表定義裡的索引（INDEX i CLUSTERED COLUMNSTORE WITH (…)）：前一格判不出位置，尾巴本身認得出來。
        new("INDEX {name} CLUSTERED") { Lead = "CREATE TABLE t (a int, " },
        new("INDEX {name} CLUSTERED COLUMNSTORE WITH (*") { Lead = "CREATE TABLE t (a int, " },
        new("INDEX {name} NONCLUSTERED COLUMNSTORE () WITH (*") { Lead = "CREATE TABLE t (a int, " },

        // 圖形資料表：名稱或定義之後的 AS NODE、AS EDGE；邊緣條件約束 CONNECTION (a TO b, …) ON DELETE CASCADE
        // 是定義裡的一項，括號裡是逗號清單、每一項是 名稱 TO 名稱。TO 要看到另一個名稱與兩層右括號才驗。
        new("CREATE TABLE {name} AS"),
        new("CREATE TABLE {name} () AS") { Group = "(a int)" },
        new("CONNECTION (* {name}") { After = ["ColumnDefinition", "AlterTableAdd"], Endings = [" x))"] },
        new("CONNECTION ()") { After = ["ColumnDefinition", "AlterTableAdd"], Group = "(a TO b)", Expand = 2 },

        // Always Encrypted 的資料行：型別之後判不出位置，ENCRYPTED WITH ( 這條尾巴本身認得出來。ENCRYPTION_TYPE 的值
        // 剖析器要看到下一項才驗，續尾把清單寫完。
        new("ENCRYPTED WITH (*") { Lead = "CREATE TABLE t (a int " },
        new("ENCRYPTED WITH (* ENCRYPTION_TYPE =") { Lead = "CREATE TABLE t (a int ", Endings = [", ALGORITHM = 'x', COLUMN_ENCRYPTION_KEY = k))"] },
        // ALTER COLUMN 的 … WITH (*（線上選項）項數比上面多，比對取它；寫到 ENCRYPTED 的這一條更長。
        new("ALTER COLUMN ... ENCRYPTED WITH (*") { Lead = "ALTER TABLE t ", Gap = "a int" },

        // 資料分割函式：參數只寫型別，之後是 AS RANGE LEFT／RIGHT FOR VALUES；ALTER 的 SPLIT、MERGE 之後接 RANGE。
        // AS 之後剖析器什麼名稱都先收，逐字探不出 RANGE，整段是證據；之後的字要看到 FOR VALUES 才驗，續尾把整句寫完。
        new("CREATE PARTITION FUNCTION {name} ()") { Group = "(int)", Expand = 1 },
        new("CREATE PARTITION FUNCTION {name} () AS RANGE") { Group = "(int)", Expand = 2, Endings = [" FOR VALUES (1)"] },
        new("ALTER PARTITION FUNCTION {name} ()") { Group = "()", Expand = 1 },
    ];

    internal static readonly PhraseDeclaration[] Sequence =
    [
        // 序列的選項不以逗號分隔、順序不限，會重複的格子寫成位置；NO 之後的 CYCLE 往下一層。
        // START 後面非接 WITH 值不可，逐字探測接不上續尾，整段是證據。
        new("") { After = ["SequenceOption"], Expand = 1 },
        new("START WITH {value}") { After = ["SequenceOption"] },
    ];

    internal static readonly PhraseDeclaration[] Constraint =
    [
        // 資料表層級的條件約束：CONSTRAINT 名稱之後是 PRIMARY KEY、UNIQUE、CHECK、FOREIGN KEY。
        new("CONSTRAINT {name}") { After = ["ColumnDefinition", "AlterTableAdd"] },
    ];
}
