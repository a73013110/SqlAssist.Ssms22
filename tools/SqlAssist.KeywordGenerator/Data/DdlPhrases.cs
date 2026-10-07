using System.Linq;

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

    // CLR 物件指向組件的寫法（組件.類別[.方法]）：模組、彙總與型別共用。
    private const string ExternalName = "EXTERNAL NAME {name}";

    // 靜態欄位依序初始化，要寫在用到它的 Tables 之前。
    private static readonly string[] InlineIndexKinds =
        ["", "CLUSTERED ", "NONCLUSTERED ", "UNIQUE ", "UNIQUE CLUSTERED ", "UNIQUE NONCLUSTERED ", "HASH ", "NONCLUSTERED HASH "];

    // 資料表定義清單的一項：CREATE TABLE 的定義與 ALTER TABLE ADD 之後。
    private static readonly string[] TableItem = ["ColumnDefinition", "AlterTableAdd"];

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
        // DELETE 的目標之後是 FROM（聯結來源）、WHERE、OUTPUT；寫完已是完整的一句，資料表提示的 WITH 被當成 CTE 的開頭扣掉，手寫補回。
        new("DELETE FROM {name}") { Values = ["WITH"] },
        new("MERGE") { Closed = false },
        // DML 的 TOP (n) 之後是 PERCENT 與動詞之後的那些字（INTO、FROM、目標）；位置分析回到動詞之後那一格，
        // 字由這裡給。目標後面還要再寫一段（SET、USING、VALUES），宣告不封閉。證據把 TOP 補進 INSERT 那一格時
        // 立起的片語探測判成封閉，會藏掉目標，INSERT 也宣告不封閉。
        new("INSERT") { Closed = false },
        .. new[] { "INSERT", "UPDATE", "DELETE", "MERGE" }.SelectMany(verb => new[]
        {
            new PhraseDeclaration($"{verb} TOP ()") { Group = "(10)", Closed = false },
            new PhraseDeclaration($"{verb} TOP () PERCENT") { Group = "(10)", Closed = false },
        }),
        // TRUNCATE 不在 Kinds 裡（後面只有 TABLE），名稱格要有片語，執行期才認得出「種類之後是既有的名稱」。
        new("TRUNCATE TABLE"),
        // WITH ( 之後剖析器什麼名稱都收，PARTITIONS 探不出來，手寫；PARTITIONS (1) 之後還要關上 WITH 那一組括號才寫得完。
        new("TRUNCATE TABLE {name} WITH (*") { Values = ["PARTITIONS"], Closed = true, Endings = [" (1))"] },
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
        // DROP 一次可以刪幾個 DDL 觸發程序：名稱清單寫完是 ON DATABASE 或 ALL SERVER，中段的 ,* 走過前面幾個名稱。
        // CREATE、ALTER 只有一個名稱，共用 DdlObject 那一條探不出清單。
        new("DROP TRIGGER ,* {name} ON") { Expand = 1 },
        new("DROP TRIGGER IF EXISTS ,* {name} ON") { Expand = 1 },

        // 模組的 WITH 選項：四種模組的選項不同，EXECUTE AS 之後的 CALLER、SELF、OWNER 除了檢視都共用；
        // 函式的兩個多字選項寫全，中間每一格由它們補出來。寫完那一項游標處已是 ModuleHeader，字由位置的各個樣板給齊；
        // 片語只拿純量函式的樣板探，確定比對的話內嵌資料表值函式的 RETURN 就不見了，所以寫全的那一格只加字。
        new("") { After = ModuleOptions },
        new("EXECUTE AS") { After = ["ProcedureOption", "FunctionOption", "TriggerOption"] },
        new("EXEC AS") { After = ["ProcedureOption", "FunctionOption", "TriggerOption"] },
        new("RETURNS NULL ON NULL INPUT") { After = ["FunctionOption"], Additive = true },
        new("CALLED ON NULL INPUT") { After = ["FunctionOption"], Additive = true },

        // 本體的 AS：一句的開頭，CLR 模組改寫 EXTERNAL NAME 組件.類別.方法。位置分析把開本體的 AS 前一格判成 ModuleHeader，
        // 拿程序的樣板探；空本體的程序也剖析得過，下一句的開頭被當成下一句扣掉，所以只加字，EXTERNAL 加在語句開頭旁邊。
        // EXTERNAL NAME 剖析器要看到整段才收，整段是證據。彙總沒有 AS，回傳型別之後直接接；CLR 型別接在名稱之後。
        new("AS") { After = ["ModuleHeader"], Template = 1, Additive = true },
        new("AS " + ExternalName) { After = ["ModuleHeader"], Template = 1 },
        new("CREATE AGGREGATE ... " + ExternalName) { Gap = "a (@x int) RETURNS int" },
        new("CREATE TYPE {name} " + ExternalName),
        // 內嵌資料表值函式的 RETURN 之後是一個查詢，可以從 CTE 開始；純量函式的 RETURN 接運算式，位置分析判不出，
        // 所以只寫 WITH 那一格，以 Lead 墊 RETURNS TABLE。與一句開頭的 WITH 同理：前置子句要寫完括號與查詢才驗，CTE 名稱什麼都收。
        new("RETURN ( WITH") { Lead = "CREATE FUNCTION f () RETURNS TABLE ", Endings = [" ('x' AS n) SELECT 1 AS a)"], Closed = false },
        new("RETURN WITH") { Lead = "CREATE FUNCTION f () RETURNS TABLE ", Endings = [" ('x' AS n) SELECT 1 AS a"], Closed = false },
        // 資料表值參數的型別之後是 READONLY：參數清單判不出位置，READONLY 不是關鍵字，整段是證據，進判不出位置的附加片語。
        // 證據寫到本體的 AS，與上面的 AS 同理只加字。
        new("READONLY AS") { Lead = "CREATE PROCEDURE p @p t ", Additive = true },
        new("DROP ASSEMBLY {name} WITH") { Expand = 1 },
        // ALTER ASSEMBLY 寫完名稱之後的 DROP FILE、ADD FILE FROM 剖析器要看到整段才收，逐字探只探得到 FROM：
        // 名稱那一格自己宣告，其餘的字由整段證據補進前面那段。
        new("ALTER ASSEMBLY {name}"),
        new("ALTER ASSEMBLY {name} WITH ,*"),
        // 換新版本的 FROM 之後同一份選項（VISIBILITY、PERMISSION_SET、UNCHECKED DATA）。
        new("ALTER ASSEMBLY {name} FROM {value} WITH ,*"),
        new("ALTER ASSEMBLY {name} DROP FILE ALL"),
        new("ALTER ASSEMBLY {name} ADD FILE FROM {value}"),
        // CREATE ASSEMBLY 的 WITH 只有 PERMISSION_SET 一項；FROM 可以寫幾個檔案、前面還可以有 AUTHORIZATION。
        new("CREATE ASSEMBLY {name} FROM {value} WITH") { Expand = 1 },
        new("CREATE ASSEMBLY ... WITH") { Gap = "t AUTHORIZATION o FROM 'x', 'y'" },
        new("CREATE ASSEMBLY ... WITH PERMISSION_SET =") { Gap = "t AUTHORIZATION o FROM 'x', 'y'" },

        // 原生編譯模組的 BEGIN ATOMIC WITH (…)：ATOMIC 不是關鍵字，由這裡的證據進區塊開頭的附加片語。
        // BEGIN x WITH ( 剖析器要一項寫完才在 x 報錯，續尾把那一項寫完，才分得出 ATOMIC 不是名稱。
        new("ATOMIC WITH (*") { After = ["BlockStart"], Endings = [" TRANSACTION ISOLATION LEVEL = SNAPSHOT)"] },
        new("ATOMIC WITH (* TRANSACTION ISOLATION LEVEL =") { After = ["BlockStart"] },
    ];

    internal static readonly PhraseDeclaration[] Tables =
    [
        // 外部索引鍵的參考動作。
        new("ON DELETE") { After = ["ReferencesTail"], Expand = 1 },
        new("ON UPDATE") { After = ["ReferencesTail"], Expand = 1 },

        // NOT FOR 之後一定是 REPLICATION：尾巴本身認得出來，寫在 IDENTITY、外部索引鍵、CHECK、觸發程序、安全性原則的述詞之後都一樣。
        new("NOT FOR") { Lead = "CREATE TABLE t (a int IDENTITY " },

        // 資料行定義的型別之後（CREATE TABLE、ALTER TABLE ADD、ALTER COLUMN、OPENJSON WITH 共用）：選項不以逗號分隔、
        // 順序不限，與序列選項同一種格子。HIDDEN、SPARSE、MASKED 這些字與下一層（NOT FOR、MASKED WITH）由展開探出；
        // 只配某些寫法的字各探自己的樣板，由證據補進同一格：PERSISTED、FILESTREAM、NOT FOR REPLICATION、AS JSON、
        // ALTER COLUMN 的線上選項、條件約束的 ON 檔案群組、字元型別的 VARYING（nchar varying）。
        // 寫完一個選項之後回到這一格，見 ReturnCompletedOptionsToPosition；資料行層級的索引名稱之後是 CLUSTERED、WHERE。
        // MASKED 要寫完 WITH (FUNCTION = …) 才驗，續尾把它寫完；只配 xml 的 COLUMN_SET 整段是證據（ALL_SPARSE_COLUMNS 在展開的第二層）。
        new("") { After = ["ColumnDefinitionTail"], Expand = 1, Endings = [" WITH (FUNCTION = 'default()'))"] },
        new("PERSISTED") { After = ["ColumnDefinitionTail"], Template = 1 },
        new("FILESTREAM") { After = ["ColumnDefinitionTail"], Template = 2 },
        new("NOT FOR REPLICATION") { After = ["ColumnDefinitionTail"], Template = 3 },
        new("AS JSON") { After = ["ColumnDefinitionTail"], Template = 4 },
        new("WITH (*") { After = ["ColumnDefinitionTail"], Template = 5 },
        new("ON {name}") { After = ["ColumnDefinitionTail"], Template = 6 },
        new("COLUMN_SET FOR ALL_SPARSE_COLUMNS") { After = ["ColumnDefinitionTail"], Template = 7 },
        new("VARYING") { After = ["ColumnDefinitionTail"], Template = 8 },
        // xml 型別的括號裡是 CONTENT、DOCUMENT 與結構描述集合；型別寫在宣告、參數與資料行定義都一樣，判不出位置，尾巴本身認得出來。
        new("XML (*") { Lead = "DECLARE @x " },
        // 資料行層級的索引名稱之後的種類：之後是 WITH (…)、ON；WITH (…) 之後的 FILESTREAM_ON 補進型別之後那一格。
        // 不從 INDEX {name} 展開：那一格接得上整份資料行選項，每個字都會另立一個片語。
        new("INDEX {name}") { After = ["ColumnDefinitionTail"] },
        .. new[] { "CLUSTERED", "NONCLUSTERED", "HASH", "NONCLUSTERED HASH" }.Select(kind =>
            new PhraseDeclaration($"INDEX {{name}} {kind}") { After = ["ColumnDefinitionTail"] }),
        new("FILESTREAM_ON {name}") { After = ["ColumnDefinitionTail"], Template = 9 },
        new("MASKED WITH (*") { After = ["ColumnDefinitionTail"] },

        // 時態表：期間資料行（GENERATED ALWAYS AS ROW START）寫在型別之後；PERIOD FOR SYSTEM_TIME
        // 是資料表層級的一項。資料表選項 WITH (…)、ALTER TABLE SET (…) 與 SYSTEM_VERSIONING = ON (…) 是括號清單。
        // AS 之後的 ROW、SUSER_SID、TRANSACTION_ID 這些字剖析器要看到 START／END 才收，逐字探不出來，整段是證據。
        new("GENERATED ALWAYS AS ROW START HIDDEN") { After = ["ColumnDefinitionTail"] },
        new("GENERATED ALWAYS AS ROW END HIDDEN") { After = ["ColumnDefinitionTail"] },
        new("GENERATED ALWAYS AS SUSER_SID START HIDDEN") { After = ["ColumnDefinitionTail"] },
        new("GENERATED ALWAYS AS SUSER_SNAME END HIDDEN") { After = ["ColumnDefinitionTail"] },
        new("GENERATED ALWAYS AS TRANSACTION_ID START HIDDEN") { After = ["ColumnDefinitionTail"] },
        new("GENERATED ALWAYS AS SEQUENCE_NUMBER END HIDDEN") { After = ["ColumnDefinitionTail"] },
        new("PERIOD FOR SYSTEM_TIME ()") { After = TableItem, Group = "(a, b)" },
        // 資料表選項的 WITH (…) 寫在定義、AS FILETABLE 或儲存段之後，前面那段由 ... 走過；FILETABLE 的選項也在這一份。
        new("CREATE TABLE ... WITH (*") { Gap = "t (a int)" },
        new("CREATE TYPE {name} AS TABLE () WITH (*") { Group = "(a int)" },
        new("ALTER TABLE {name} SET (*"),
        new("SYSTEM_VERSIONING = ON (*") { Lead = "CREATE TABLE t (a int) WITH (" },
        // 大量載入：BULK INSERT 與 COPY INTO（Synapse／Fabric）的 WITH (…)，選項名稱剖析器逐一驗，各探各的。
        // COPY 不是關鍵字，語句開頭的 COPY 由證據進附加片語。目標之後可以夾資料行清單、FROM 之後可以有幾個來源，以 ... 跨過。
        // FILE_TYPE 的值剖析器只收 'CSV' 這類字串，CREDENTIAL 的 IDENTITY 只收 'Managed Identity' 這類字串，續尾寫進去。
        new("BULK INSERT {name} FROM {value} WITH (*"),
        new("COPY INTO {name}"),
        new("COPY INTO {name} ()") { Group = "(a)" },
        new("COPY INTO {name} FROM {value}"),
        new("COPY INTO ... WITH (*") { Gap = "t FROM 'x'", Endings = [" = 'CSV'"] },
        new("COPY INTO ... WITH (* CREDENTIAL = (*") { Gap = "t FROM 'x'", Endings = [" = 'Shared Access Signature'"] },
        new("COPY INTO ... WITH (* ERRORFILE_CREDENTIAL = (*") { Gap = "t FROM 'x'", Endings = [" = 'Shared Access Signature'"] },

        // ALTER TABLE 的變更追蹤：ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON)。
        new("ENABLE CHANGE_TRACKING WITH (*") { After = ["AlterTableAction"], Expand = 1 },
        // 外部資料表的 REJECT_TYPE = 之後是 VALUE、PERCENTAGE：展開一層到等號之後。
        new("CREATE EXTERNAL TABLE {name} () WITH (*") { Group = "(a int)", Expand = 1 },
        new("CREATE EXTERNAL TABLE {name} WITH (*") { Expand = 1 },

        // 資料表定義與 ALTER TABLE ADD 裡的索引（INDEX i CLUSTERED COLUMNSTORE WITH (…)），前一格是定義清單的一項。
        // 索引鍵之後與 CREATE INDEX 一樣接 INCLUDE、WHERE、WITH、ON；名稱與索引鍵之間的種類寫法有限，逐一寫出，
        // 也是名稱之後那一格的證據（HASH、UNIQUE）。ALTER TABLE ADD 寫完索引鍵就是完整的語句，WITH 被當成下一句扣掉了，手寫補回。
        // 資料行層級的索引（a int INDEX i UNIQUE (a)）剖析器也收索引鍵與 UNIQUE，帶索引鍵的這幾條也寫在型別之後那一格。
        new("INDEX {name}") { After = TableItem },
        new("INDEX {name} CLUSTERED") { After = TableItem },
        .. InlineIndexKinds.Select(kind =>
            new PhraseDeclaration($"INDEX {{name}} {kind}()") { After = [.. TableItem, "ColumnDefinitionTail"], Values = ["WITH"] }),
        new("INDEX {name} CLUSTERED COLUMNSTORE WITH (*") { After = TableItem },
        new("INDEX {name} NONCLUSTERED COLUMNSTORE () WITH (*") { After = TableItem },

        // 圖形資料表：名稱或定義之後的 AS NODE、AS EDGE；邊緣條件約束 CONNECTION (a TO b, …) ON DELETE CASCADE
        // 是定義裡的一項，括號裡是逗號清單、每一項是 名稱 TO 名稱。TO 要看到另一個名稱與兩層右括號才驗。
        new("CREATE TABLE {name} AS"),
        new("CREATE TABLE {name} () AS") { Group = "(a int)" },
        // 定義或 AS FILETABLE 之後依序是 ON 檔案群組、TEXTIMAGE_ON、FILESTREAM_ON 與 WITH (…)，每一段之後都是完整的語句，
        // WITH 也是 CTE 的開頭，被當成下一句扣掉了，手寫補回。不展開：FILESTREAM_ON 收值，剖析器把值之後的字都當成運算式往下讀。
        // 前面寫了哪幾段由 ... 走過，FILETABLE、圖形資料表與一般定義共用。
        new("CREATE TABLE {name} ()") { Group = "(a int)", Values = ["WITH"] },
        new("CREATE TABLE {name} AS FILETABLE") { Values = ["WITH"] },
        .. new[] { "ON", "TEXTIMAGE_ON", "FILESTREAM_ON" }.Select(storage =>
            new PhraseDeclaration($"CREATE TABLE ... {storage} {{name}}") { Gap = "t (a int)", Values = ["WITH"] }),
        new("CONNECTION (* {name}") { After = TableItem, Endings = [" x))"] },
        new("CONNECTION ()") { After = TableItem, Group = "(a TO b)", Expand = 2 },

        // Always Encrypted 的資料行：ENCRYPTION_TYPE 的值剖析器要看到下一項才驗，續尾把清單寫完。
        new("ENCRYPTED WITH (*") { After = ["ColumnDefinitionTail"] },
        new("ENCRYPTED WITH (* ENCRYPTION_TYPE =") { After = ["ColumnDefinitionTail"], Endings = [", ALGORITHM = 'x', COLUMN_ENCRYPTION_KEY = k))"] },

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
        // ALTER 多了 RESTART [WITH]，拿 ALTER 的樣板探：RESTART 自己就寫完一項，之後是其餘選項；
        // WITH 是 CTE 的開頭、被當成下一句扣掉，整段是證據。RESTART 也由證據補進那一格。
        new("") { After = ["SequenceOption"], Expand = 1 },
        new("START WITH {value}") { After = ["SequenceOption"] },
        new("RESTART") { After = ["SequenceOption"], Template = 2 },
        new("RESTART WITH {value}") { After = ["SequenceOption"], Template = 2 },
    ];

    internal static readonly PhraseDeclaration[] Constraint =
    [
        // 條件約束：CONSTRAINT 名稱之後是 PRIMARY KEY、UNIQUE、CHECK、FOREIGN KEY；寫在型別之後的是資料行層級，多一個 DEFAULT。
        new("CONSTRAINT {name}") { After = ["ColumnDefinition", "AlterTableAdd", "ColumnDefinitionTail"] },
    ];
}
