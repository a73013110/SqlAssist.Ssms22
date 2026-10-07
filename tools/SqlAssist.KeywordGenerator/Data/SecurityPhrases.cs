using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>安全性：稽核、安全性原則、金鑰與憑證、權限、登入與使用者。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class SecurityPhrases
{
    private const string AllowEncryptedValueModifications = "ALLOW_ENCRYPTED_VALUE_MODIFICATIONS";

    // 權限名稱：sys.fn_builtin_permissions(DEFAULT) 的 permission_name（SQL Server 2022）。GRANT 收任何一串識別字，
    // 剖析器分不出哪些才是權限，只能手寫；ALL PRIVILEGES 是舊寫法、不在那份清單裡，照收。
    private static readonly string[] PermissionNames =
    [
        "ADMINISTER BULK OPERATIONS", "ADMINISTER DATABASE BULK OPERATIONS", "ALTER", "ALTER ANY APPLICATION ROLE",
        "ALTER ANY ASSEMBLY", "ALTER ANY ASYMMETRIC KEY", "ALTER ANY AVAILABILITY GROUP", "ALTER ANY CERTIFICATE",
        "ALTER ANY COLUMN ENCRYPTION KEY", "ALTER ANY COLUMN MASTER KEY", "ALTER ANY CONNECTION", "ALTER ANY CONTRACT",
        "ALTER ANY CREDENTIAL", "ALTER ANY DATABASE", "ALTER ANY DATABASE AUDIT", "ALTER ANY DATABASE DDL TRIGGER",
        "ALTER ANY DATABASE EVENT NOTIFICATION", "ALTER ANY DATABASE EVENT SESSION", "ALTER ANY DATABASE EVENT SESSION ADD EVENT", "ALTER ANY DATABASE EVENT SESSION ADD TARGET",
        "ALTER ANY DATABASE EVENT SESSION DISABLE", "ALTER ANY DATABASE EVENT SESSION DROP EVENT", "ALTER ANY DATABASE EVENT SESSION DROP TARGET", "ALTER ANY DATABASE EVENT SESSION ENABLE",
        "ALTER ANY DATABASE EVENT SESSION OPTION", "ALTER ANY DATABASE SCOPED CONFIGURATION", "ALTER ANY DATASPACE", "ALTER ANY ENDPOINT",
        "ALTER ANY EVENT NOTIFICATION", "ALTER ANY EVENT SESSION", "ALTER ANY EVENT SESSION ADD EVENT", "ALTER ANY EVENT SESSION ADD TARGET",
        "ALTER ANY EVENT SESSION DISABLE", "ALTER ANY EVENT SESSION DROP EVENT", "ALTER ANY EVENT SESSION DROP TARGET", "ALTER ANY EVENT SESSION ENABLE",
        "ALTER ANY EVENT SESSION OPTION", "ALTER ANY EXTERNAL DATA SOURCE", "ALTER ANY EXTERNAL FILE FORMAT", "ALTER ANY EXTERNAL JOB",
        "ALTER ANY EXTERNAL LANGUAGE", "ALTER ANY EXTERNAL LIBRARY", "ALTER ANY EXTERNAL STREAM", "ALTER ANY FULLTEXT CATALOG",
        "ALTER ANY LINKED SERVER", "ALTER ANY LOGIN", "ALTER ANY MASK", "ALTER ANY MESSAGE TYPE",
        "ALTER ANY REMOTE SERVICE BINDING", "ALTER ANY ROLE", "ALTER ANY ROUTE", "ALTER ANY SCHEMA",
        "ALTER ANY SECURITY POLICY", "ALTER ANY SENSITIVITY CLASSIFICATION", "ALTER ANY SERVER AUDIT", "ALTER ANY SERVER ROLE",
        "ALTER ANY SERVICE", "ALTER ANY SYMMETRIC KEY", "ALTER ANY USER", "ALTER LEDGER",
        "ALTER LEDGER CONFIGURATION", "ALTER RESOURCES", "ALTER SERVER STATE", "ALTER SETTINGS",
        "ALTER TRACE", "AUTHENTICATE", "AUTHENTICATE SERVER", "BACKUP DATABASE",
        "BACKUP LOG", "CHECKPOINT", "CONNECT", "CONNECT ANY DATABASE",
        "CONNECT REPLICATION", "CONNECT SQL", "CONTROL", "CONTROL SERVER",
        "CREATE AGGREGATE", "CREATE ANY DATABASE", "CREATE ANY DATABASE EVENT SESSION", "CREATE ANY EVENT SESSION",
        "CREATE ASSEMBLY", "CREATE ASYMMETRIC KEY", "CREATE AVAILABILITY GROUP", "CREATE CERTIFICATE",
        "CREATE CONTRACT", "CREATE DATABASE", "CREATE DATABASE DDL EVENT NOTIFICATION", "CREATE DDL EVENT NOTIFICATION",
        "CREATE DEFAULT", "CREATE ENDPOINT", "CREATE EXTERNAL LANGUAGE", "CREATE EXTERNAL LIBRARY",
        "CREATE FULLTEXT CATALOG", "CREATE FUNCTION", "CREATE LOGIN", "CREATE MESSAGE TYPE",
        "CREATE PROCEDURE", "CREATE QUEUE", "CREATE REMOTE SERVICE BINDING", "CREATE ROLE",
        "CREATE ROUTE", "CREATE RULE", "CREATE SCHEMA", "CREATE SEQUENCE",
        "CREATE SERVER ROLE", "CREATE SERVICE", "CREATE SYMMETRIC KEY", "CREATE SYNONYM",
        "CREATE TABLE", "CREATE TRACE EVENT NOTIFICATION", "CREATE TYPE", "CREATE USER",
        "CREATE VIEW", "CREATE XML SCHEMA COLLECTION", "DELETE", "DROP ANY DATABASE EVENT SESSION",
        "DROP ANY EVENT SESSION", "ENABLE LEDGER", "EXECUTE", "EXECUTE ANY EXTERNAL ENDPOINT",
        "EXECUTE ANY EXTERNAL SCRIPT", "EXECUTE EXTERNAL SCRIPT", "EXTERNAL ACCESS ASSEMBLY", "IMPERSONATE",
        "IMPERSONATE ANY LOGIN", "INSERT", "KILL DATABASE CONNECTION", "RECEIVE",
        "REFERENCES", "SELECT", "SELECT ALL USER SECURABLES", "SEND",
        "SHOWPLAN", "SHUTDOWN", "SUBSCRIBE QUERY NOTIFICATIONS", "TAKE OWNERSHIP",
        "UNMASK", "UNSAFE ASSEMBLY", "UPDATE", "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION",
        "VIEW ANY COLUMN MASTER KEY DEFINITION", "VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION", "VIEW ANY DATABASE", "VIEW ANY DEFINITION",
        "VIEW ANY ERROR LOG", "VIEW ANY PERFORMANCE DEFINITION", "VIEW ANY SECURITY DEFINITION", "VIEW ANY SENSITIVITY CLASSIFICATION",
        "VIEW CHANGE TRACKING", "VIEW CRYPTOGRAPHICALLY SECURED DEFINITION", "VIEW DATABASE PERFORMANCE STATE", "VIEW DATABASE SECURITY AUDIT",
        "VIEW DATABASE SECURITY STATE", "VIEW DATABASE STATE", "VIEW DEFINITION", "VIEW LEDGER CONTENT",
        "VIEW PERFORMANCE DEFINITION", "VIEW SECURITY DEFINITION", "VIEW SERVER PERFORMANCE STATE", "VIEW SERVER SECURITY AUDIT",
        "VIEW SERVER SECURITY STATE", "VIEW SERVER STATE",
        "ALL PRIVILEGES",
    ];

    internal static readonly PhraseDeclaration[] AuditAndPolicies =
    [
        // 稽核：ALTER SERVER AUDIT 的名稱剖析器要看到 TO、WITH 這些字才收，CREATE、ALTER 的展開到不了名稱之後。
        // 目的地之後的 WITH (…) 兩者相同：APPLICATION_LOG 這類目的地是一個字，FILE、URL 帶一組括號。
        // 不寫成 ... WITH (*：SPECIFICATION 不是關鍵字，ALTER SERVER AUDIT SPECIFICATION s WITH ( 也比對得上。
        new("ALTER SERVER AUDIT {name} WITH (*"),
        // 改名與拿掉篩選：MODIFY、REMOVE 剖析器要看到整段才收，整段是證據。
        new("ALTER SERVER AUDIT {name} MODIFY NAME = {name}"),
        new("ALTER SERVER AUDIT {name} REMOVE WHERE"),
        .. AuditDestinations("CREATE SERVER AUDIT {name}"),
        .. AuditDestinations("ALTER SERVER AUDIT {name}"),

        // 稽核規格：FOR SERVER AUDIT 之後是 ADD、DROP 動作群組，以逗號分隔，最後是 WITH (STATE = …)。ADD、DROP 能開始一句，
        // 位置分析把它們當成動詞、判不出前一格，以尾巴認。稽核名稱與規格名稱之後的語句已經完整，ADD、DROP、WITH 被當成
        // 下一句的開頭扣掉，手寫補回。括號裡伺服器稽核規格只收伺服器層級的群組，資料庫稽核規格收資料庫層級的群組與
        // SELECT ON 物件 BY 主體這種動作，執行期分不出是哪一種：兩種都墊、字取聯集。動作之後的 ON、BY 與權限同一種寫法，由位置分析給。
        new("CREATE SERVER AUDIT SPECIFICATION {name}") { Expand = 3 },
        new("CREATE DATABASE AUDIT SPECIFICATION {name}") { Expand = 3 },
        new("CREATE SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}") { Values = ["ADD", "WITH"] },
        new("CREATE DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}") { Values = ["ADD", "WITH"] },
        new("ALTER SERVER AUDIT SPECIFICATION {name}") { Expand = 1, Values = ["ADD", "DROP", "WITH"] },
        new("ALTER DATABASE AUDIT SPECIFICATION {name}") { Expand = 1, Values = ["ADD", "DROP", "WITH"] },
        new("ALTER SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}") { Values = ["ADD", "DROP", "WITH"] },
        new("ALTER DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}") { Values = ["ADD", "DROP", "WITH"] },
        new("CREATE SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*"),
        new("CREATE DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*"),
        new("ALTER SERVER AUDIT SPECIFICATION {name} WITH (*"),
        new("ALTER DATABASE AUDIT SPECIFICATION {name} WITH (*"),
        new("ALTER SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*"),
        new("ALTER DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*"),
        new("ADD (*") { Lead = "ALTER SERVER AUDIT SPECIFICATION s ", AlsoLeads = ["ALTER DATABASE AUDIT SPECIFICATION s "] },
        new("DROP (*") { Lead = "ALTER SERVER AUDIT SPECIFICATION s ", AlsoLeads = ["ALTER DATABASE AUDIT SPECIFICATION s "] },
        new("ADD () ,") { Lead = "ALTER SERVER AUDIT SPECIFICATION s ", Group = "(SCHEMA_OBJECT_ACCESS_GROUP)" },
        new("DROP () ,") { Lead = "ALTER SERVER AUDIT SPECIFICATION s ", Group = "(SCHEMA_OBJECT_ACCESS_GROUP)" },
        new("ADD () WITH (*") { Lead = "ALTER SERVER AUDIT SPECIFICATION s ", Group = "(SCHEMA_OBJECT_ACCESS_GROUP)" },
        new("DROP () WITH (*") { Lead = "ALTER SERVER AUDIT SPECIFICATION s ", Group = "(SCHEMA_OBJECT_ACCESS_GROUP)" },

        // 安全性原則：ADD FILTER、BLOCK PREDICATE 函式 ON 資料表，BLOCK 之後可以帶 AFTER、BEFORE 的作業，述詞以逗號分隔，
        // 最後是 WITH (STATE = …)。CREATE 的名稱之後語句已經完整，ADD 被當成下一句的開頭扣掉，手寫補回。
        // 函式呼叫寫成名稱加括號，整段從語句開頭寫起：ADD 與作業的 INSERT、UPDATE、DELETE 都是語句開頭字，以 … 跨過的話
        // 找動詞會停在它們上。逗號之後的述詞從逗號寫起，前面墊一個述詞；述詞寫完的逗號與 ALTER 的尾巴相同，
        // 分不出動詞，由 ALTER 那一組給（CREATE 也列出 ALTER、DROP），否則 ALTER 的逗號之後只剩 ADD。
        // WITH (…) 之後的 NOT FOR REPLICATION 不分第幾個述詞，從 PREDICATE 寫起，以尾巴認。
        new("CREATE SECURITY POLICY {name}") { Expand = 3, Values = ["ADD"] },
        new("ALTER SECURITY POLICY {name}") { Expand = 3 },
        new("CREATE SECURITY POLICY {name} WITH (*"),
        new("ALTER SECURITY POLICY {name} WITH (*"),
        new("CREATE SECURITY POLICY {name} WITH ()") { Group = "(STATE = ON)" },
        new("PREDICATE {name} () ON {name} WITH ()") { Lead = "CREATE SECURITY POLICY t ADD FILTER ", Group = "(STATE = ON)" },
        new("PREDICATE {name} () ON {name} AFTER {name} WITH ()") { Lead = "CREATE SECURITY POLICY t ADD BLOCK ", Group = "(STATE = ON)" },
        new("PREDICATE {name} () ON {name} BEFORE {name} WITH ()") { Lead = "CREATE SECURITY POLICY t ADD BLOCK ", Group = "(STATE = ON)" },
        new("CREATE SECURITY POLICY {name} ADD FILTER PREDICATE {name} () ON {name}"),
        new("CREATE SECURITY POLICY {name} ADD FILTER PREDICATE {name} () ON {name} ,"),
        new("CREATE SECURITY POLICY {name} ADD FILTER PREDICATE {name} () ON {name} WITH (*"),
        new("CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name}"),
        new("CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} ,"),
        new("CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} WITH (*"),
        new("CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} AFTER INSERT WITH (*"),
        new("CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} AFTER UPDATE WITH (*"),
        new("CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} BEFORE UPDATE WITH (*"),
        new("CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} BEFORE DELETE WITH (*"),
        new(", ADD FILTER PREDICATE {name} () ON {name}") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD FILTER PREDICATE {name} () ON {name} WITH (*") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name}") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name} WITH (*") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name} AFTER INSERT WITH (*") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name} AFTER UPDATE WITH (*") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name} BEFORE UPDATE WITH (*") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name} BEFORE DELETE WITH (*") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },

        // ALTER SECURITY POLICY 一次加、改、刪幾個述詞（ADD、ALTER、DROP），以逗號分隔；DROP 不寫函式，BLOCK 述詞可以帶作業。
        // 動詞都能開始一句，判不出前一格：從 BLOCK 或 PREDICATE 寫起，以尾巴認，ADD 與 ALTER 的寫法相同。
        // Lead 的名稱寫成 t，與 ALTER SECURITY POLICY {name} 展開出來的探測文字相同，前面那段的字補進同一個片語。
        // 逗號之後的動詞從逗號寫起：ADD 由 CREATE 那一組給，DROP 也接擴充事件的 DROP EVENT、DROP TARGET，兩種都墊。
        new("BLOCK PREDICATE {name} () ON {name}") { Lead = "ALTER SECURITY POLICY t ADD ", Expand = 1 },
        new("BLOCK PREDICATE ON {name}") { Lead = "ALTER SECURITY POLICY t DROP ", Expand = 1 },
        new("PREDICATE {name} () ON {name} ,") { Lead = "ALTER SECURITY POLICY t ADD FILTER " },
        new("PREDICATE ON {name} ,") { Lead = "ALTER SECURITY POLICY t DROP FILTER " },
        new("PREDICATE {name} () ON {name} AFTER {name} ,") { Lead = "ALTER SECURITY POLICY t ADD BLOCK " },
        new("PREDICATE {name} () ON {name} BEFORE {name} ,") { Lead = "ALTER SECURITY POLICY t ADD BLOCK " },
        new("PREDICATE ON {name} AFTER {name} ,") { Lead = "ALTER SECURITY POLICY t DROP BLOCK " },
        new("PREDICATE ON {name} BEFORE {name} ,") { Lead = "ALTER SECURITY POLICY t DROP BLOCK " },
        new(", ALTER") { Lead = "ALTER SECURITY POLICY t ADD FILTER PREDICATE f(a) ON t", Expand = 2 },
        new(", DROP")
        {
            Lead = "ALTER SECURITY POLICY t ADD FILTER PREDICATE f(a) ON t",
            AlsoLeads = ["ALTER EVENT SESSION t ON SERVER DROP EVENT t.t", "ALTER EVENT SESSION t ON SERVER DROP TARGET t.t"],
            Expand = 3,
        },
    ];

    // 稽核檔的 MAXSIZE = 之後是數值帶單位（MB、GB、TB）或 UNLIMITED，展開兩層探到單位。
    private static PhraseDeclaration[] AuditDestinations(string head) =>
    [
        new(head + " TO FILE (*") { Expand = 2 },
        new(head + " TO URL (*") { Expand = 2 },
        new(head + " TO {name} WITH (*"),
        new(head + " TO FILE () WITH (*") { Group = "(FILEPATH = 'x')" },
        new(head + " TO URL () WITH (*") { Group = "(PATH = 'x')" },
    ];

    // 對稱金鑰的加密方式以逗號一次寫幾種（PASSWORD = 'x', CERTIFICATE c），第幾種都一樣。第一種由展開立片語，
    // 展開寫在清單之前：逗號之後還沒寫完的那一項（ASYMMETRIC 之後只接 KEY）由清單從逗號之後那一格立。
    private static PhraseDeclaration[] EncryptingMechanisms(string head) =>
    [
        new(head) { Expand = 2 },
        new(head + " ,*"),
    ];

    internal static readonly PhraseDeclaration[] Keys =
    [
        // 金鑰與憑證的標頭：種類與名稱由 CREATE、ALTER 的展開給，這裡往下寫加密方式（ENCRYPTION BY 憑證、密碼或
        // 另一把金鑰）與 WITH 之後的演算法、主旨。等號之後的值（AES_256、RSA_2048）也由展開列，逗號之後由清單片語。
        new("CREATE MASTER KEY") { Expand = 3 },
        new("ALTER MASTER KEY") { Expand = 5 },
        // 服務主要金鑰：FORCE 之後是 REGENERATE，WITH 之後是成對的帳戶與密碼（OLD_ACCOUNT 只接 OLD_PASSWORD）。
        // WITH 與逗號之後剖析器把選項當名稱讀、什麼名稱都收，探不出字，手寫並宣告封閉。
        new("ALTER SERVICE MASTER KEY") { Expand = 2 },
        new("ALTER SERVICE MASTER KEY WITH")
        {
            Values = ["OLD_ACCOUNT", "NEW_ACCOUNT"],
            Closed = true,
            Endings = [" = 'a', OLD_PASSWORD = 'p'", " = 'a', NEW_PASSWORD = 'p'"],
        },
        .. new[] { "OLD", "NEW" }.Select(pair => new PhraseDeclaration($"ALTER SERVICE MASTER KEY WITH {pair}_ACCOUNT = {{value}} ,")
        {
            Values = [$"{pair}_PASSWORD"],
            Closed = true,
            Endings = [" = 'p'"],
        }),
        // 主金鑰改由服務主要金鑰加密：BY 之後剖析器什麼名稱都收，SERVICE MASTER KEY 只有整段是證據。
        new("ALTER MASTER KEY ADD ENCRYPTION BY SERVICE MASTER KEY"),
        new("ALTER MASTER KEY DROP ENCRYPTION BY SERVICE MASTER KEY"),
        // ALTER SYMMETRIC KEY 的 ADD、DROP 之後剖析器把 ENCRYPTION 當名稱讀，逐字探不出來，整段是證據。
        .. EncryptingMechanisms("ALTER SYMMETRIC KEY {name} ADD ENCRYPTION BY"),
        .. EncryptingMechanisms("ALTER SYMMETRIC KEY {name} DROP ENCRYPTION BY"),
        // 可延伸金鑰管理（EKM）的金鑰：DROP 寫完名稱可以接 REMOVE PROVIDER KEY，REMOVE 非接整段不可，整段是證據。
        new("DROP SYMMETRIC KEY {name} REMOVE PROVIDER KEY"),
        new("DROP ASYMMETRIC KEY {name} REMOVE PROVIDER KEY"),

        // 主金鑰的備份與還原、憑證與非對稱金鑰的私密金鑰：檔案路徑、演算法之後的 ENCRYPTION BY、DECRYPTION BY。
        // 剖析器要看到 PASSWORD = 才回頭驗 ENCRYPTION，逐字探不出來，整段寫到 PASSWORD 的片語是證據；
        // 檔案路徑之後的語句已經完整，WITH 也由證據補回。SERVICE 之後剖析器什麼名稱都收，MASTER 寫成名稱，與展開出來的同一格。
        // WITH PRIVATE KEY ( 的項是多字的（ENCRYPTION BY PASSWORD = …），各敘述收的項相近，以尾巴認、取 ALTER CERTIFICATE 探，不封閉。
        // 對稱金鑰的選項清單裡，逗號之後的演算法之後的 ENCRYPTION 以名稱結尾、Lead 立不了，名稱與前面的選項以 … 跨過。
        new("BACKUP MASTER KEY TO FILE = {value}") { Expand = 3 },
        new("BACKUP SERVICE {name} KEY TO FILE = {value}") { Expand = 3 },
        new("RESTORE MASTER KEY FROM FILE = {value}") { Expand = 3 },
        new("RESTORE MASTER KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value}") { Expand = 3 },
        new("RESTORE SERVICE {name} KEY FROM FILE = {value}") { Expand = 3 },
        new("BACKUP MASTER KEY TO FILE = {value} ENCRYPTION BY PASSWORD"),
        new("BACKUP SERVICE {name} KEY TO FILE = {value} ENCRYPTION BY PASSWORD"),
        new("RESTORE MASTER KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value} ENCRYPTION BY PASSWORD"),
        new("RESTORE SERVICE {name} KEY FROM FILE = {value} DECRYPTION BY PASSWORD"),
        // 密碼之後還能寫 FORCE（解不開的金鑰照樣還原）。
        new("RESTORE MASTER KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value} ENCRYPTION BY PASSWORD = {value}"),
        new("RESTORE SERVICE {name} KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value}"),
        new("BACKUP CERTIFICATE {name} TO FILE = {value}") { Expand = 2 },
        new("CREATE CERTIFICATE {name} FROM FILE = {value}") { Expand = 2 },
        new("ALTER CERTIFICATE {name}") { Expand = 2 },
        new("ALTER ASYMMETRIC KEY {name}") { Expand = 2 },
        new("BACKUP CERTIFICATE {name} TO FILE = {value} WITH PRIVATE KEY"),
        new("CREATE CERTIFICATE {name} FROM FILE = {value} WITH PRIVATE KEY"),
        new("ALTER CERTIFICATE {name} WITH PRIVATE KEY"),
        new("PRIVATE KEY (*") { Lead = "ALTER CERTIFICATE t WITH ", Closed = false },
        new("PRIVATE KEY (* ENCRYPTION") { Lead = "ALTER CERTIFICATE t WITH ", Items = "FILE = 'x', " },
        new("PRIVATE KEY (* ENCRYPTION BY") { Lead = "ALTER CERTIFICATE t WITH ", Items = "FILE = 'x', " },
        new("PRIVATE KEY (* ENCRYPTION BY PASSWORD") { Lead = "ALTER CERTIFICATE t WITH ", Items = "FILE = 'x', " },
        new("PRIVATE KEY (* DECRYPTION") { Lead = "ALTER CERTIFICATE t WITH ", Items = "FILE = 'x', " },
        new("PRIVATE KEY (* DECRYPTION BY") { Lead = "ALTER CERTIFICATE t WITH ", Items = "FILE = 'x', " },
        new("PRIVATE KEY (* DECRYPTION BY PASSWORD") { Lead = "ALTER CERTIFICATE t WITH ", Items = "FILE = 'x', " },
        new("CREATE ASYMMETRIC KEY {name} FROM ASSEMBLY {name}") { Expand = 3 },
        new("CREATE ASYMMETRIC KEY {name} FROM FILE = {value}") { Expand = 3 },
        new("CREATE ASYMMETRIC KEY {name} FROM PROVIDER {name} WITH ALGORITHM = {name}") { Expand = 3 },
        new("CREATE ASYMMETRIC KEY {name} FROM ASSEMBLY {name} ENCRYPTION BY PASSWORD"),
        new("CREATE ASYMMETRIC KEY {name} FROM PROVIDER {name} WITH ALGORITHM = {name} ENCRYPTION BY PASSWORD"),
        new("CREATE SYMMETRIC KEY ... ALGORITHM = {name} ENCRYPTION BY PASSWORD") { Gap = "t WITH KEY_SOURCE = 'x'," },
        new("CREATE SYMMETRIC KEY ... ENCRYPTION BY ,*") { Gap = "t WITH KEY_SOURCE = 'x', IDENTITY_VALUE = 'x'" },
        new("CREATE SYMMETRIC KEY ... IDENTITY_VALUE = {value}") { Gap = "t WITH KEY_SOURCE = 'x'," },
        new("CREATE SYMMETRIC KEY ... IDENTITY_VALUE = {value} ENCRYPTION BY PASSWORD") { Gap = "t WITH KEY_SOURCE = 'x'," },
        new("CREATE SYMMETRIC KEY ... KEY_SOURCE = {value}") { Gap = "t WITH IDENTITY_VALUE = 'x'," },
        new("CREATE SYMMETRIC KEY ... KEY_SOURCE = {value} ENCRYPTION BY PASSWORD") { Gap = "t WITH IDENTITY_VALUE = 'x'," },
        new("CREATE CERTIFICATE {name}") { Expand = 4 },
        new("CREATE ASYMMETRIC KEY {name}") { Expand = 5 },
        new("CREATE SYMMETRIC KEY {name}") { Expand = 6 },
        // ALGORITHM = 之後的值寫完，剖析器把下一個字當名稱讀（ENCRYPTION BY 在它眼中是「名稱 BY」），探不出
        // ENCRYPTION；整段剖析得過就是證據，由片語裡的每一個字補進前面那段。金鑰、憑證之後的 WITH 也是 CTE 的開頭，被扣掉了。
        .. EncryptingMechanisms("CREATE SYMMETRIC KEY {name} WITH ALGORITHM = {name} ENCRYPTION BY"),
        new("CREATE ASYMMETRIC KEY {name} WITH ALGORITHM = {name} ENCRYPTION BY") { Expand = 1 },
        // REGENERATE、FORCE 剖析器也當名稱讀，同樣只有整段是證據。
        new("ALTER MASTER KEY REGENERATE WITH ENCRYPTION BY PASSWORD = {value}"),
        new("ALTER MASTER KEY FORCE REGENERATE WITH ENCRYPTION BY PASSWORD = {value}"),
        // 資料庫加密金鑰的 WITH 選項、演算法與 SERVER 之後的種類，剖析器一律當名稱收：演算法手寫，其餘整段是證據。
        new("CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM =") { Values = ["AES_128", "AES_192", "AES_256", "TRIPLE_DES_3KEY"], Closed = true },
        new("CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {name} ENCRYPTION BY SERVER CERTIFICATE {name}"),
        new("CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {name} ENCRYPTION BY SERVER ASYMMETRIC KEY {name}"),
        // Always Encrypted 的金鑰：資料行主金鑰的 WITH (…)，資料行加密金鑰的值一組一組寫（每把主金鑰一組），
        // ALTER 一次加或刪一組。演算法與加密值是字串與二進位值，不列。
        new("CREATE COLUMN MASTER KEY {name} WITH (*"),
        // ENCLAVE_COMPUTATIONS (SIGNATURE = 0x…)：SIGNATURE 剖析器當名稱讀、整句寫完才驗，續尾寫完簽章。
        new("CREATE COLUMN MASTER KEY {name} WITH (* ENCLAVE_COMPUTATIONS (*") { Items = "KEY_STORE_PROVIDER_NAME = 'x', KEY_PATH = 'x', ", Endings = [" = 0x01))"] },
        new("CREATE COLUMN ENCRYPTION KEY {name} WITH") { Expand = 1 },
        new("CREATE COLUMN ENCRYPTION KEY {name} WITH VALUES ,* (*"),
        new("ALTER COLUMN ENCRYPTION KEY {name}") { Expand = 2 },
        new("ALTER COLUMN ENCRYPTION KEY {name} ADD VALUE (*"),
        new("ALTER COLUMN ENCRYPTION KEY {name} DROP VALUE (*"),
        new("OPEN SYMMETRIC KEY {name} DECRYPTION BY ASYMMETRIC KEY {name} WITH PASSWORD"),
        new("OPEN SYMMETRIC KEY {name} DECRYPTION BY CERTIFICATE {name} WITH PASSWORD"),
        new("CREATE CERTIFICATE {name} WITH ,*"),
        new("CREATE CERTIFICATE {name} ENCRYPTION BY PASSWORD = {value} WITH ,*"),
        new("CREATE SYMMETRIC KEY {name} WITH ,*"),
        // EKM 的金鑰：FROM PROVIDER 之後的 WITH 清單另有 PROVIDER_KEY_NAME、CREATION_DISPOSITION，與一般的金鑰分開宣告。
        // 擁有者（AUTHORIZATION）可省，寫成 ... 的話項的等號之後不立，兩種標頭各宣告一份。
        .. new[] { "SYMMETRIC", "ASYMMETRIC" }.SelectMany(kind => new[] { "", " AUTHORIZATION {name}" }
            .Select(owner => new PhraseDeclaration($"CREATE {kind} KEY {{name}}{owner} FROM PROVIDER {{name}} WITH ,*"))),
        new("CREATE CREDENTIAL {name} WITH ,*"),
        new("ALTER CREDENTIAL {name} WITH ,*"),
        new("CREATE DATABASE SCOPED CREDENTIAL {name} WITH ,*"),
        // DROP DATABASE 之後的 SCOPED 剖析器也當成資料庫名稱讀，DROP 的展開到不了認證的名稱那一格。
        new("DROP DATABASE SCOPED CREDENTIAL"),
        // 認證的 WITH 清單之後的 FOR CRYPTOGRAPHIC PROVIDER（EKM）：清單長度不定，以 ... 跨過，整段是證據。
        new("CREATE CREDENTIAL ... FOR CRYPTOGRAPHIC PROVIDER {name}") { Gap = "t WITH IDENTITY = 'x'" },

        // 資料庫加密金鑰的 ALTER：重新產生（REGENERATE WITH ALGORITHM =）或換加密的憑證、非對稱金鑰；演算法照 CREATE 手寫。
        new("ALTER DATABASE ENCRYPTION KEY REGENERATE WITH ALGORITHM =") { Values = ["AES_128", "AES_192", "AES_256", "TRIPLE_DES_3KEY"], Closed = true },
        new("ALTER DATABASE ENCRYPTION KEY ENCRYPTION BY SERVER CERTIFICATE {name}"),
        new("ALTER DATABASE ENCRYPTION KEY ENCRYPTION BY SERVER ASYMMETRIC KEY {name}"),

        // 模組簽章：ADD、DROP [COUNTER] SIGNATURE TO|FROM [類別::]模組 BY 憑證或非對稱金鑰，ADD 的金鑰之後是 WITH PASSWORD 或 SIGNATURE。
        // DROP 的類別由 DROP 的展開列；ADD 不是物件種類的動詞，TO 那一格自己宣告，那一格也可以直接寫模組名稱。
        new("ADD SIGNATURE TO") { Closed = false },
        new("ADD COUNTER SIGNATURE TO") { Closed = false },
        new("ADD SIGNATURE TO {name} BY") { Expand = 3 },
        new("ADD COUNTER SIGNATURE TO {name} BY") { Expand = 3 },
        // 金鑰之後的語句已經完整，WITH 被當成 CTE 的開頭扣掉：整段是證據。
        new("ADD SIGNATURE TO {name} BY CERTIFICATE {name} WITH PASSWORD = {value}"),
        new("ADD SIGNATURE TO {name} BY CERTIFICATE {name} WITH SIGNATURE = {value}"),
        new("ADD SIGNATURE TO {name} BY ASYMMETRIC KEY {name} WITH PASSWORD = {value}"),
        new("ADD COUNTER SIGNATURE TO {name} BY CERTIFICATE {name} WITH PASSWORD = {value}"),
        new("ADD COUNTER SIGNATURE TO {name} BY ASYMMETRIC KEY {name} WITH PASSWORD = {value}"),
        new("DROP SIGNATURE FROM {name} BY") { Expand = 2 },
        new("DROP COUNTER SIGNATURE FROM {name} BY") { Expand = 2 },

        // 敏感度分類：TO 之後一到多個資料行（以 ... 跨過），WITH ( 之後是 LABEL、INFORMATION_TYPE、RANK 與它們的 _ID。
        // 剖析器會驗選項與 RANK 的值，但那些字不在候選字裡，探不出來，照它的錯誤訊息手寫。
        new("ADD SENSITIVITY CLASSIFICATION TO ... WITH (*")
        {
            Gap = "t.c", Values = ["LABEL", "LABEL_ID", "INFORMATION_TYPE", "INFORMATION_TYPE_ID", "RANK"], Closed = true,
        },
        new("ADD SENSITIVITY CLASSIFICATION TO ... WITH (* RANK =")
        {
            Gap = "t.c", Values = ["NONE", "LOW", "MEDIUM", "HIGH", "CRITICAL"], Closed = true,
        },
        new("ALTER DATABASE SCOPED CREDENTIAL {name} WITH ,*"),
    ];

    internal static readonly PhraseDeclaration[] Principals =
    [
        // 權限 ON 之後的類別多半不是關鍵字（OBJECT、TYPE）；那一格也可以直接寫目標名稱，由人宣告不封閉。
        // 多字的類別（SEARCH PROPERTY LIST）由 Classes 補。ALTER AUTHORIZATION ON 的類別相同，那一格由 ALTER 的展開立，
        // 在這裡補多字的類別，名稱照收；擁有者那一格（主體的位置）可以寫 SCHEMA OWNER，交還給結構描述的擁有者。
        // 從 TO 寫起：主體之後的 TO {name} 一樣長，以字面字結尾的優先。
        new("") { After = ["PermissionOn"], Closed = false, Classes = true },
        new("ALTER AUTHORIZATION ON") { Closed = false, Classes = true },
        new("TO SCHEMA OWNER") { Lead = "ALTER AUTHORIZATION ON OBJECT::t " },
        // 把物件移到另一個結構描述：TRANSFER 之後是類別（OBJECT::、TYPE::、XML SCHEMA COLLECTION::）或直接寫物件名稱。
        new("ALTER SCHEMA {name} TRANSFER") { Closed = false, Classes = true },

        // 權限清單：GRANT、DENY、REVOKE 之後以逗號分隔，REVOKE 還可以先寫 GRANT OPTION FOR。權限名稱由 Evidence 一個字一個字列
        // （VIEW 之後是 DEFINITION、CHANGE、ANY…），寫完一個權限之後的 ON、TO 由位置分析（PermissionList）給。
        new("GRANT ,*") { Evidence = PermissionNames },
        new("DENY ,*") { Evidence = PermissionNames },
        new("REVOKE ,*") { Evidence = PermissionNames },
        new("REVOKE GRANT OPTION FOR ,*") { Evidence = PermissionNames },

        // 主體寫完之後：REVOKE、DENY 接 CASCADE 與 AS 授與者，GRANT 接 WITH GRANT OPTION 與 AS。從 TO、FROM 寫起：
        // 單獨的名稱在前一格判不出位置時到處比對得上。前者拿 REVOKE 的樣板探；語句到這裡已經完整，WITH 被當成 CTE 的開頭扣掉，
        // 由 GRANT 那一段整段當證據補回同一格。主體以逗號一次寫幾個（TO a, b），最後一個之後接的字都一樣：中段 ,* 走過前面幾個。
        new("TO ,* {name}") { After = ["PermissionTarget", "PermissionList"], Template = 1 },
        new("FROM ,* {name}") { After = ["PermissionTarget", "PermissionList"], Template = 1 },
        new("TO ,* {name} WITH GRANT OPTION") { After = ["PermissionTarget", "PermissionList"] },

        // CREATE USER 寫完名稱已經是完整的一句，之後的 FOR、WITHOUT 各自接 LOGIN；CREATE LOGIN 之後是 WITH PASSWORD 或 FROM。
        // FROM EXTERNAL 之後是 PROVIDER（Microsoft Entra 的主體）。
        // 只認 CREATE：ALTER USER、ALTER LOGIN 接的是別的字（ENABLE、WITH NAME）。
        new("CREATE USER {name}") { Expand = 2 },
        new("CREATE LOGIN {name}") { Expand = 2 },

        // 角色成員：ALTER 的展開只到名稱之後一層（ADD、DROP、WITH），再往下一層才是 MEMBER 與 NAME =。
        new("ALTER ROLE {name}") { Expand = 2 },
        new("ALTER SERVER ROLE {name}") { Expand = 2 },
        // 登入的 ADD、DROP 之後是 CREDENTIAL（對應的認證）：剖析器要看到整段才收，整段是證據。
        new("ALTER LOGIN {name} ADD CREDENTIAL {name}"),
        new("ALTER LOGIN {name} DROP CREDENTIAL {name}"),

        // 登入與使用者的 WITH 選項清單：四種敘述接的選項各不相同（CREATE LOGIN 第一項只能是 PASSWORD、
        // ALTER LOGIN 另有 NAME、NO CREDENTIAL，USER 才有 DEFAULT_SCHEMA），由標頭分開；應用程式角色同理。
        new("CREATE LOGIN {name} WITH ,*"),
        new("CREATE LOGIN {name} FROM WINDOWS WITH ,*"),
        new("CREATE LOGIN {name} FROM EXTERNAL PROVIDER WITH ,*"),
        new("ALTER LOGIN {name} WITH ,*"),
        // 密碼之後的 MUST_CHANGE、UNLOCK 可以寫兩個：項的等號之後只立一層，再往下一層由展開說。
        new("ALTER LOGIN {name} WITH ,* PASSWORD = {value}") { Expand = 1 },
        // ALLOW_ENCRYPTED_VALUE_MODIFICATIONS（Always Encrypted 的大量複製）官方語法圖寫得出來，ScriptDom TSql170 在值就報錯：
        // 手寫補進清單（Lagging），只有這一處。
        new("CREATE USER {name} WITH ,*") { Lagging = [AllowEncryptedValueModifications] },
        new("CREATE USER {name} FOR LOGIN {name} WITH ,*") { Lagging = [AllowEncryptedValueModifications] },
        new("CREATE USER {name} FROM LOGIN {name} WITH ,*") { Lagging = [AllowEncryptedValueModifications] },
        new("CREATE USER {name} WITHOUT LOGIN WITH ,*") { Lagging = [AllowEncryptedValueModifications] },
        new("ALTER USER {name} WITH ,*") { Lagging = [AllowEncryptedValueModifications] },
        new("CREATE APPLICATION ROLE {name} WITH ,*"),
        new("ALTER APPLICATION ROLE {name} WITH ,*"),
    ];
}
