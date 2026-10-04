namespace SqlAssist.KeywordGenerator.Data;

/// <summary>安全性：稽核、安全性原則、金鑰與憑證、權限、登入與使用者。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class SecurityPhrases
{
    private const string AllowEncryptedValueModifications = "ALLOW_ENCRYPTED_VALUE_MODIFICATIONS";

    internal static readonly PhraseDeclaration[] AuditAndPolicies =
    [
        // 稽核：ALTER SERVER AUDIT 的名稱剖析器要看到 TO、WITH 這些字才收，CREATE、ALTER 的展開到不了名稱之後。
        new("ALTER SERVER AUDIT {name} WITH (*"),
        // 稽核檔的 MAXSIZE = 之後是數值帶單位（MB、GB、TB）或 UNLIMITED，展開兩層探到單位。
        new("ALTER SERVER AUDIT {name} TO FILE (*") { Expand = 2 },
        new("CREATE SERVER AUDIT {name} TO FILE (*") { Expand = 2 },
        new("CREATE SERVER AUDIT {name} TO {name} WITH (*"),
        new("CREATE SERVER AUDIT {name} TO FILE () WITH (*") { Group = "(FILEPATH = 'x')" },

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
        // 找動詞會停在它們上。逗號之後的述詞從逗號寫起，前面墊一個述詞。
        new("CREATE SECURITY POLICY {name}") { Expand = 3, Values = ["ADD"] },
        new("ALTER SECURITY POLICY {name}") { Expand = 3 },
        new("CREATE SECURITY POLICY {name} WITH (*"),
        new("ALTER SECURITY POLICY {name} WITH (*"),
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
        new(", ADD FILTER PREDICATE {name} () ON {name} ,") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD FILTER PREDICATE {name} () ON {name} WITH (*") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name}") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
        new(", ADD BLOCK PREDICATE {name} () ON {name} ,") { Lead = "CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t" },
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
            AlsoLeads = ["ALTER EVENT SESSION t ON SERVER DROP EVENT t.t"],
            Expand = 3,
        },
    ];

    internal static readonly PhraseDeclaration[] Keys =
    [
        // 金鑰與憑證的標頭：種類與名稱由 CREATE、ALTER 的展開給，這裡往下寫加密方式（ENCRYPTION BY 憑證、密碼或
        // 另一把金鑰）與 WITH 之後的演算法、主旨。等號之後的值（AES_256、RSA_2048）也由展開列，逗號之後由清單片語。
        new("CREATE MASTER KEY") { Expand = 3 },
        new("ALTER MASTER KEY") { Expand = 5 },
        // ALTER SYMMETRIC KEY 的 ADD、DROP 之後剖析器把 ENCRYPTION 當名稱讀，逐字探不出來，整段是證據。
        new("ALTER SYMMETRIC KEY {name} ADD ENCRYPTION BY") { Expand = 2 },
        new("ALTER SYMMETRIC KEY {name} DROP ENCRYPTION BY") { Expand = 2 },

        // 主金鑰的備份與還原、憑證與非對稱金鑰的私密金鑰：檔案路徑、演算法之後的 ENCRYPTION BY、DECRYPTION BY。
        // 剖析器要看到 PASSWORD = 才回頭驗 ENCRYPTION，逐字探不出來，整段寫到 PASSWORD 的片語是證據；
        // 檔案路徑之後的語句已經完整，WITH 也由證據補回。SERVICE 之後剖析器什麼名稱都收，MASTER 寫成名稱，與展開出來的同一格。
        // WITH PRIVATE KEY ( 的項是多字的（ENCRYPTION BY PASSWORD = …），各敘述收的項相近，以尾巴認、取 ALTER CERTIFICATE 探，不封閉。
        // 對稱金鑰的選項清單裡，逗號之後的 ALGORITHM = 也以尾巴認；演算法之後的 ENCRYPTION 以名稱結尾、Lead 立不了，
        // 名稱與前面的選項以 … 跨過。
        new("BACKUP MASTER KEY TO FILE = {value}") { Expand = 3 },
        new("BACKUP SERVICE {name} KEY TO FILE = {value}") { Expand = 3 },
        new("RESTORE MASTER KEY FROM FILE = {value}") { Expand = 3 },
        new("RESTORE MASTER KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value}") { Expand = 3 },
        new("RESTORE SERVICE {name} KEY FROM FILE = {value}") { Expand = 3 },
        new("BACKUP MASTER KEY TO FILE = {value} ENCRYPTION BY PASSWORD"),
        new("BACKUP SERVICE {name} KEY TO FILE = {value} ENCRYPTION BY PASSWORD"),
        new("RESTORE MASTER KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value} ENCRYPTION BY PASSWORD"),
        new("RESTORE SERVICE {name} KEY FROM FILE = {value} DECRYPTION BY PASSWORD"),
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
        new(", ALGORITHM =") { Lead = "CREATE SYMMETRIC KEY t WITH KEY_SOURCE = 'x' ", Expand = 3 },
        new("CREATE SYMMETRIC KEY ... ALGORITHM = {name} ENCRYPTION BY PASSWORD") { Gap = "t WITH KEY_SOURCE = 'x'," },
        new("CREATE SYMMETRIC KEY ... ENCRYPTION BY") { Gap = "t WITH KEY_SOURCE = 'x', IDENTITY_VALUE = 'x'" },
        new("CREATE SYMMETRIC KEY ... IDENTITY_VALUE = {value}") { Gap = "t WITH KEY_SOURCE = 'x'," },
        new("CREATE SYMMETRIC KEY ... IDENTITY_VALUE = {value} ENCRYPTION BY PASSWORD") { Gap = "t WITH KEY_SOURCE = 'x'," },
        new("CREATE SYMMETRIC KEY ... KEY_SOURCE = {value}") { Gap = "t WITH IDENTITY_VALUE = 'x'," },
        new("CREATE SYMMETRIC KEY ... KEY_SOURCE = {value} ENCRYPTION BY PASSWORD") { Gap = "t WITH IDENTITY_VALUE = 'x'," },
        new("CREATE CERTIFICATE {name}") { Expand = 4 },
        new("CREATE ASYMMETRIC KEY {name}") { Expand = 5 },
        new("CREATE SYMMETRIC KEY {name}") { Expand = 6 },
        // ALGORITHM = 之後的值寫完，剖析器把下一個字當名稱讀（ENCRYPTION BY 在它眼中是「名稱 BY」），探不出
        // ENCRYPTION；整段剖析得過就是證據，由片語裡的每一個字補進前面那段。金鑰、憑證之後的 WITH 也是 CTE 的開頭，被扣掉了。
        new("CREATE SYMMETRIC KEY {name} WITH ALGORITHM = {name} ENCRYPTION BY") { Expand = 2 },
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
        new("CREATE CREDENTIAL {name} WITH ,*"),
        new("ALTER CREDENTIAL {name} WITH ,*"),
        new("CREATE DATABASE SCOPED CREDENTIAL {name} WITH ,*"),
        // DROP DATABASE 之後的 SCOPED 剖析器也當成資料庫名稱讀，DROP 的展開到不了認證的名稱那一格。
        new("DROP DATABASE SCOPED CREDENTIAL"),
        new("ALTER DATABASE SCOPED CREDENTIAL {name} WITH ,*"),
    ];

    internal static readonly PhraseDeclaration[] Principals =
    [
        // 權限 ON 之後的類別多半不是關鍵字（OBJECT、TYPE）；那一格也可以直接寫目標名稱，由人宣告不封閉。
        // 多字的類別（SEARCH PROPERTY LIST）由 Classes 補。ALTER AUTHORIZATION ON 的類別相同，那一格由 ALTER 的展開立，
        // 在這裡補多字的類別，名稱照收；擁有者那一格（主體的位置）可以寫 SCHEMA OWNER，交還給結構描述的擁有者。
        new("") { After = ["PermissionOn"], Closed = false, Classes = true },
        new("ALTER AUTHORIZATION ON") { Closed = false, Classes = true },
        new("SCHEMA") { After = ["PermissionGrantee"], Template = 2 },

        // CREATE USER 寫完名稱已經是完整的一句，之後的 FOR、WITHOUT 各自接 LOGIN；CREATE LOGIN 之後是 WITH PASSWORD 或 FROM。
        // FROM EXTERNAL 之後是 PROVIDER（Microsoft Entra 的主體）。
        // 只認 CREATE：ALTER USER、ALTER LOGIN 接的是別的字（ENABLE、WITH NAME）。
        new("CREATE USER {name}") { Expand = 2 },
        new("CREATE LOGIN {name}") { Expand = 2 },

        // 角色成員：ALTER 的展開只到名稱之後一層（ADD、DROP、WITH），再往下一層才是 MEMBER 與 NAME =。
        new("ALTER ROLE {name}") { Expand = 2 },
        new("ALTER SERVER ROLE {name}") { Expand = 2 },

        // 登入與使用者的 WITH 選項清單：四種敘述接的選項各不相同（CREATE LOGIN 第一項只能是 PASSWORD、
        // ALTER LOGIN 另有 NAME、NO CREDENTIAL，USER 才有 DEFAULT_SCHEMA），由標頭分開；應用程式角色同理。
        new("CREATE LOGIN {name} WITH ,*"),
        new("CREATE LOGIN {name} FROM WINDOWS WITH ,*"),
        new("ALTER LOGIN {name} WITH ,*"),
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
