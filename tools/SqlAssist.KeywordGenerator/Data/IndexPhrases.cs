namespace SqlAssist.KeywordGenerator.Data;

/// <summary>索引：CREATE／ALTER INDEX 與其餘接索引選項的 WITH (…)。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class IndexPhrases
{
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
        // 資料壓縮的值同理：索引、重建、條件約束與資料表選項的 WITH (…) 收的是同一份值，一條尾巴共用。
        // 取資料表選項探：每一種值都收，值之後的 ON PARTITIONS 也不必先寫 PARTITION = ALL（重建要）。
        new("WITH (* DATA_COMPRESSION =") { Lead = "CREATE TABLE t (a int) ", Expand = 2 },
        // 分割區清單的範圍（ON PARTITIONS (2 TO 4)）：資料與 XML 壓縮共用。
        new("ON PARTITIONS (* {value}") { Lead = "CREATE TABLE t (a int) WITH (DATA_COMPRESSION = ROW " },

        // 其餘帶 WITH (ONLINE = …) 的。DROP INDEX 一次刪得了幾個，… 從動詞跨過前面幾個；ALTER TABLE 裡的 DROP、ALTER
        // 位置分析當成動詞，DROP CONSTRAINT、DROP COLUMN、ALTER COLUMN 之後長度不定的一段同樣以 … 跨過。
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
        new("ALTER COLUMN ... WITH (*") { Lead = "ALTER TABLE t ", Gap = "a int" },
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
    ];
}
