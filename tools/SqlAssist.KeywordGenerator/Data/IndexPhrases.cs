using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>索引：CREATE／ALTER INDEX 與其餘接索引選項的 WITH (…)。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class IndexPhrases
{
    // 靜態欄位依序初始化，要寫在用到它的 Options 之前。
    private static readonly string[] Compressions = ["DATA_COMPRESSION", "XML_COMPRESSION"];

    internal static readonly PhraseDeclaration[] Options =
    [
        // CREATE INDEX 寫完欄位就是完整的語句；WITH 同時是 CTE 的開頭，被當成下一句扣掉了，手寫補回來。
        // WITH ( 之後的選項由位置給（IndexOption），INCLUDE、篩選的 WHERE 夾在中間也一樣。
        // INDEX 前面可以夾 UNIQUE、CLUSTERED 這些字，那一格判不出位置；尾巴本身只出現在 CREATE INDEX。
        new("ALTER INDEX {name} ON {name}"),
        new("INDEX {name} ON {name} ()") { Lead = "CREATE ", Values = ["WITH"] },
        new("INCLUDE ()") { Lead = "CREATE INDEX t ON t (a) ", Values = ["WITH"] },
        new("") { After = ["IndexOption"] },

        // 其餘接索引選項的 WITH (…)：ALTER INDEX 的 REBUILD、REORGANIZE、SET，ALTER TABLE 的 REBUILD、SWITCH，
        // 以及條件約束的索引鍵之後（PRIMARY KEY (a) WITH (…)）。各敘述收的選項不同，標頭固定的寫成括號清單由剖析器探；
        // 條件約束的前一格判不出位置（CONSTRAINT pk 之後），KEY ( ) WITH ( 這條尾巴本身認得出來。
        // ONLINE = ON ( 與 WAIT_AT_LOW_PRIORITY ( 是清單裡再開的清單，MAX_DURATION 是固定的第一項：
        // ABORT_AFTER_WAIT 要墊在它之後，值之後還要關兩層括號。
        new("ALTER INDEX {name} ON {name} REBUILD WITH (*"),
        new("ALTER INDEX {name} ON {name} REORGANIZE WITH (*"),
        new("ALTER INDEX {name} ON {name} SET (*"),
        new("ALTER INDEX {name} ON {name} RESUME WITH (*"),
        new("ALTER TABLE {name} REBUILD WITH (*"),
        new("ALTER TABLE {name} SWITCH TO {name} WITH (*"),
        // 記憶體最佳化資料表的索引在 ALTER TABLE 裡重建：REBUILD 非接 WITH (…) 不可，整段是證據。
        new("ALTER TABLE {name} ALTER INDEX {name}"),
        new("ALTER TABLE {name} ALTER INDEX {name} REBUILD WITH (*"),
        new("KEY () WITH (*") { Lead = "ALTER TABLE t ADD PRIMARY " },
        new("UNIQUE () WITH (*") { Lead = "ALTER TABLE t ADD " },
        new("CLUSTERED () WITH (*") { Lead = "ALTER TABLE t ADD PRIMARY KEY " },
        new("NONCLUSTERED () WITH (*") { Lead = "ALTER TABLE t ADD PRIMARY KEY " },
        // 墊的文字與上面的清單片語相同：ONLINE、WAIT_AT_LOW_PRIORITY 已由那一份列出，不必另加到判不出位置的地方。
        new("ONLINE = ON (*") { Lead = "ALTER INDEX t ON t REBUILD WITH (" },
        new("WAIT_AT_LOW_PRIORITY (*") { Lead = "ALTER TABLE t SWITCH TO t WITH (" },
        new("WAIT_AT_LOW_PRIORITY (* MAX_DURATION = {value}") { Lead = "ALTER TABLE t SWITCH TO t WITH (" },
        new("WAIT_AT_LOW_PRIORITY (* ABORT_AFTER_WAIT =") { Lead = "ALTER TABLE t SWITCH TO t WITH (", Items = "MAX_DURATION = 1 MINUTES, ", Endings = [" ))"] },
        // 值之後的單位（MAX_DURATION = 5 MINUTES）：接這兩個選項的 WITH (…) 有十幾種標頭，CREATE INDEX 的還夾著 INCLUDE、WHERE；
        // 尾巴認 WITH 開的那組括號就夠了，判不出位置也不比對到 WHERE max_duration = 5 這種欄位。
        new("WITH (* MAX_DURATION = {value}") { Lead = "ALTER INDEX t ON t RESUME " },
        new("WITH (* COMPRESSION_DELAY = {value}") { Lead = "CREATE CLUSTERED COLUMNSTORE INDEX t ON t " },
        new("SET (* COMPRESSION_DELAY = {value}") { Lead = "ALTER INDEX t ON t " },
        // 資料與 XML 壓縮的值同理：索引、重建、條件約束與資料表選項的 WITH (…) 收的是同一份值，一條尾巴共用。
        // 取資料表選項探：每一種值都收，值之後的 ON PARTITIONS 也不必先寫 PARTITION = ALL（重建要）。
        .. Compressions.Select(option => new PhraseDeclaration($"WITH (* {option} =") { Lead = "CREATE TABLE t (a int) ", Expand = 2 }),
        // 分割區清單的範圍（ON PARTITIONS (2 TO 4)）：資料與 XML 壓縮共用。
        new("ON PARTITIONS (* {value}") { Lead = "CREATE TABLE t (a int) WITH (DATA_COMPRESSION = ROW " },

        // 其餘帶 WITH (ONLINE = …) 的。DROP INDEX 一次刪得了幾個，… 從動詞跨過前面幾個；ALTER TABLE 裡的 DROP、ALTER
        // 位置分析當成動詞，DROP CONSTRAINT、DROP COLUMN 之後長度不定的一段同樣以 … 跨過；ALTER COLUMN 型別之後的 WITH 屬於
        // 型別之後那一格（DdlPhrases）。
        // DROP COLUMN 本身不帶 WITH：那是同一句後面 CONSTRAINT 的選項。
        // ALTER COLUMN 的名稱之後是型別或 ADD、DROP（PERSISTED、NOT FOR REPLICATION）。名稱之後不展開：型別那一格的
        // NATIONAL 之後接得上名稱，再往下每一個字都是一整輪探測（展開三層探了三百多萬次）；ADD、DROP 各展開一層。
        // ADD、DROP 也是語句開頭字，… 找動詞會停在它們上，之後的 WITH ( 從語句開頭寫起；以 Lead 認的話，探測文字也比對得到
        // 展開出來的 ALTER TABLE … ADD NOT，兩條搶同一格。
        new("DROP INDEX ... WITH (*") { Gap = "i ON t" },
        new("DROP INDEX ... WITH (* MOVE TO") { Gap = "i ON t" },
        new("DROP CONSTRAINT ... WITH (*") { Lead = "ALTER TABLE t ", Gap = "k" },
        new("DROP COLUMN ... WITH (*") { Lead = "ALTER TABLE t ", Gap = "a, CONSTRAINT k" },
        new("ALTER TABLE {name} ALTER COLUMN {name}"),
        new("ALTER TABLE {name} ALTER COLUMN {name} ADD") { Expand = 1 },
        new("ALTER TABLE {name} ALTER COLUMN {name} DROP") { Expand = 1 },
        new("ALTER TABLE {name} ALTER COLUMN {name} ADD PERSISTED WITH (*"),
        new("ALTER TABLE {name} ALTER COLUMN {name} DROP PERSISTED WITH (*"),
        new("ALTER TABLE {name} ALTER COLUMN {name} ADD SPARSE WITH (*"),
        new("ALTER TABLE {name} ALTER COLUMN {name} DROP SPARSE WITH (*"),
        new("ALTER TABLE {name} ALTER COLUMN {name} ADD ROWGUIDCOL WITH (*"),
        new("ALTER TABLE {name} ALTER COLUMN {name} DROP ROWGUIDCOL WITH (*"),
        new("ALTER TABLE {name} ALTER COLUMN {name} ADD NOT FOR REPLICATION WITH (*"),
        new("ALTER TABLE {name} ALTER COLUMN {name} DROP NOT FOR REPLICATION WITH (*"),
        new("ALTER TABLE {name} REBUILD PARTITION = {value} WITH (*"),
        new("ALTER INDEX {name} ON {name} REBUILD PARTITION = {value} WITH (*"),
        new("ALTER TABLE {name} REBUILD PARTITION = ALL WITH (*"),
        new("ALTER INDEX {name} ON {name} REBUILD PARTITION = ALL WITH (*"),
        new("CREATE CLUSTERED COLUMNSTORE INDEX {name} ON {name} WITH (*"),
        new("CREATE NONCLUSTERED COLUMNSTORE INDEX {name} ON {name} () WITH (*"),
        new("CREATE COLUMNSTORE INDEX {name} ON {name} () WITH (*"),

        // XML 與 JSON 索引：XML、JSON 剖析器要看到整段才收，CREATE 的展開探不出來，整段是證據（種類也交給 CreatedKinds）。
        // 索引鍵之後各接自己的字，比 CREATE INDEX 那條長，比對取它；寫得完一句的 WITH 與 CREATE INDEX 那條一樣補回來，之後的選項由位置給。
        // 次要 XML 索引是 USING XML INDEX 主索引 FOR PATH|VALUE|PROPERTY，主索引之後非接 FOR 不可，整段是證據；
        // 選擇性 XML 索引的 FOR (…) 是路徑清單，一項是 名稱 = '路徑' AS SQL 型別或 AS XQUERY '型別'，型別由型別位置給；
        // FOR 前面可以夾 WITH XMLNAMESPACES (…)，路徑清單從 FOR 寫起。
        new("CREATE XML INDEX {name} ON {name} ()"),
        new("CREATE XML INDEX {name} ON {name} () USING XML INDEX {name} FOR") { Expand = 1 },
        new("CREATE PRIMARY XML INDEX {name} ON {name} ()") { Values = ["WITH"] },
        new("CREATE SELECTIVE XML INDEX {name} ON {name} ()") { Expand = 2 },
        new("FOR (* {name} = {value} AS") { Lead = "CREATE SELECTIVE XML INDEX t ON t (a) ", Expand = 2, Endings = [" int)", " 'x')"] },
        new("CREATE JSON INDEX {name} ON {name} ()") { Values = ["WITH"] },

        // 向量索引：METRIC、TYPE 的值是字串，METRIC 只收距離的名稱。
        new("CREATE VECTOR INDEX {name} ON {name} () WITH (*") { Endings = [" = 'cosine'"] },

        // 空間索引：SPATIAL 要看到整段才收，CREATE 的展開探不出來，整段是證據。USING 之後是四種鑲嵌配置，
        // 格線配置的 WITH (…) 多出 GRIDS = (LEVEL_1 = HIGH, …) 或 GRIDS = (HIGH, LOW, …)；幾何格線另有 BOUNDING_BOX。
        // 鑲嵌配置換成名稱剖析不過，各寫一條。
        new("CREATE SPATIAL INDEX {name} ON {name} ()") { Expand = 1 },
        new("CREATE SPATIAL INDEX {name} ON {name} () WITH (*") { Expand = 2 },
        new("CREATE SPATIAL INDEX {name} ON {name} () WITH (* GRIDS = (*") { Expand = 2 },
        new("CREATE SPATIAL INDEX {name} ON {name} () USING GEOMETRY_GRID WITH (*") { Expand = 2 },
        new("CREATE SPATIAL INDEX {name} ON {name} () USING GEOMETRY_GRID WITH (* GRIDS = (*") { Expand = 2 },
        new("CREATE SPATIAL INDEX {name} ON {name} () USING GEOGRAPHY_GRID WITH (*") { Expand = 2 },
        new("CREATE SPATIAL INDEX {name} ON {name} () USING GEOGRAPHY_GRID WITH (* GRIDS = (*") { Expand = 2 },
        new("CREATE SPATIAL INDEX {name} ON {name} () USING GEOMETRY_AUTO_GRID WITH (*") { Expand = 2 },
        new("CREATE SPATIAL INDEX {name} ON {name} () USING GEOGRAPHY_AUTO_GRID WITH (*") { Expand = 2 },

        // 全文檢索索引：資料行清單之後是 KEY INDEX，再來是 ON 目錄與 WITH 選項，WITH 可以帶括號也可以不帶。
        // SEARCH PROPERTY LIST 剖析器要看到整段才收，整段是證據；不帶括號的清單逗號之後由尾巴認。
        new("CREATE FULLTEXT INDEX ON {name} ()") { Expand = 2 },
        new("CREATE FULLTEXT INDEX ON {name} () KEY INDEX {name}") { Expand = 1 },
        new("CREATE FULLTEXT INDEX ON {name} () KEY INDEX {name} WITH") { Expand = 2 },
        new("CREATE FULLTEXT INDEX ON {name} () KEY INDEX {name} WITH ,*"),
        new("CREATE FULLTEXT INDEX ON {name} () KEY INDEX {name} WITH SEARCH PROPERTY LIST ="),
        new(", SEARCH PROPERTY LIST =") { Lead = "CREATE FULLTEXT INDEX ON t (a) KEY INDEX t WITH STOPLIST = OFF" },
        new("CREATE FULLTEXT INDEX ON {name} () KEY INDEX {name} WITH (*") { Expand = 2 },
        new("CREATE FULLTEXT INDEX ON {name} () KEY INDEX {name} WITH (* SEARCH PROPERTY LIST ="),
        new("ALTER FULLTEXT INDEX ON {name} SET") { Expand = 2 },
        new("ALTER FULLTEXT INDEX ON {name} SET SEARCH PROPERTY LIST"),

        // 資料行清單的每一項：資料行之後依序是 TYPE COLUMN 型別資料行、LANGUAGE、STATISTICAL_SEMANTICS，CREATE 與 ALTER … ADD 相同。
        // TYPE 要看到 COLUMN 與型別資料行才收，整段是證據。一項寫完要關上括號：CREATE 還要 KEY INDEX 才完整，續尾各自寫完。
        .. FullTextColumns("CREATE FULLTEXT INDEX ON {name} (*", " KEY INDEX k"),
        .. FullTextColumns("ALTER FULLTEXT INDEX ON {name} ADD (*", ""),
        // NO POPULATION 剖析器要看到整段才收，整段是證據。
        new("ALTER FULLTEXT INDEX ON {name} ADD () WITH NO POPULATION"),
        new("ALTER FULLTEXT INDEX ON {name} DROP () WITH NO POPULATION"),
        // 目錄名稱之後非接 REBUILD、REORGANIZE、AS DEFAULT 不可，ALTER 的展開探不出 CATALOG，整段是證據。
        // REBUILD 已是完整的語句，WITH 被當成下一句扣掉了，另外宣告。
        new("ALTER FULLTEXT CATALOG {name}") { Expand = 1 },
        new("ALTER FULLTEXT CATALOG {name} REBUILD WITH") { Expand = 1 },
        // 停用字詞表的語句非以分號結尾不可。
        new("ALTER FULLTEXT STOPLIST {name} ADD {value}") { Endings = [" 1;"] },
        new("ALTER FULLTEXT STOPLIST {name} DROP {value}") { Endings = [" 1;"] },
    ];

    private static PhraseDeclaration[] FullTextColumns(string head, string statementEnd)
    {
        string[] endings = [")" + statementEnd, " 1)" + statementEnd];

        return
        [
            new($"{head} {{name}}") { Expand = 2, Endings = endings },
            new($"{head} {{name}} TYPE COLUMN {{name}}") { Expand = 2, Endings = endings },
        ];
    }
}
