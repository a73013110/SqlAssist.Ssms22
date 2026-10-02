namespace SqlAssist.KeywordGenerator.Data;

/// <summary>PolyBase、外部模型、外部程式庫、事件通知與擴充事件。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class PolyBaseXePhrases
{
    internal static readonly PhraseDeclaration[] ExternalData =
    [
        // PolyBase：外部資料來源與檔案格式的 WITH (…)。資料來源的 TYPE = 之後剖析器什麼名稱都收，值取 SQL Server 的文件
        // （RDBMS、SHARD_MAP_MANAGER 是 Azure SQL Database 的）；檔案格式的 FORMAT_TYPE 剖析器會驗，而且要寫在第一項。
        new("CREATE EXTERNAL DATA SOURCE {name} WITH (*"),
        new("CREATE EXTERNAL DATA SOURCE {name} WITH (* TYPE =") { Values = ["HADOOP", "BLOB_STORAGE"], Closed = true },
        new("ALTER EXTERNAL DATA SOURCE {name} SET ,*"),
        new("CREATE EXTERNAL FILE FORMAT {name} WITH (*"),
        new("CREATE EXTERNAL FILE FORMAT {name} WITH (* FORMAT_TYPE ="),
        new("CREATE EXTERNAL FILE FORMAT {name} WITH (* FORMAT_OPTIONS (*") { Items = "FORMAT_TYPE = DELIMITEDTEXT, " },
    ];

    internal static readonly PhraseDeclaration[] ExternalModels =
    [
        // 外部模型（SQL Server 2025）：選項清單順序固定，MODEL_TYPE 的值剖析器會驗。
        new("CREATE EXTERNAL MODEL {name} WITH (*"),
        new("CREATE EXTERNAL MODEL {name} AUTHORIZATION {name} WITH (*"),
        new("CREATE EXTERNAL MODEL {name} WITH (* MODEL_TYPE =") { Items = "LOCATION = 'x', API_FORMAT = 'x', " },
        new("CREATE EXTERNAL MODEL {name} AUTHORIZATION {name} WITH (* MODEL_TYPE =") { Items = "LOCATION = 'x', API_FORMAT = 'x', " },
    ];

    // 宣告在用到它的片語之前：靜態欄位照書寫順序初始化。
    private static readonly string[] LibraryPlatforms = ["WINDOWS", "LINUX"];

    internal static readonly PhraseDeclaration[] ExternalLibraries =
    [
        // 外部程式庫：名稱之後可以寫 AUTHORIZATION，FROM 之後一個平台一組檔案，SET 只換一組，WITH (LANGUAGE = …) 的值是字串。
        // PLATFORM 的值剖析器什麼名稱都收，只能手寫，取文件的兩個平台；CONTENT 要寫在第一項。
        new("CREATE EXTERNAL LIBRARY {name} FROM ,* (*"),
        new("CREATE EXTERNAL LIBRARY {name} AUTHORIZATION {name} FROM ,* (*"),
        new("CREATE EXTERNAL LIBRARY {name} FROM ,* (* PLATFORM =") { Items = "CONTENT = 'x', ", Values = LibraryPlatforms, Closed = true },
        new("CREATE EXTERNAL LIBRARY {name} AUTHORIZATION {name} FROM ,* (* PLATFORM =") { Items = "CONTENT = 'x', ", Values = LibraryPlatforms, Closed = true },
        new("CREATE EXTERNAL LIBRARY ... WITH (*") { Gap = "t FROM (CONTENT = 'x')" },
        new("ALTER EXTERNAL LIBRARY {name} SET (*"),
        new("ALTER EXTERNAL LIBRARY {name} SET (* PLATFORM =") { Items = "CONTENT = 'x', ", Values = LibraryPlatforms, Closed = true },
        new("ALTER EXTERNAL LIBRARY {name} SET () WITH (*") { Group = "(CONTENT = 'x')" },
        new("ALTER EXTERNAL LIBRARY {name} AUTHORIZATION {name} SET (*"),
        new("ALTER EXTERNAL LIBRARY {name} AUTHORIZATION {name} SET (* PLATFORM =") { Items = "CONTENT = 'x', ", Values = LibraryPlatforms, Closed = true },
        new("ALTER EXTERNAL LIBRARY {name} AUTHORIZATION {name} SET () WITH (*") { Group = "(CONTENT = 'x')" },
    ];

    internal static readonly PhraseDeclaration[] Events =
    [
        // 事件通知：ON SERVER、DATABASE、QUEUE，FOR 之後的事件與 DDL 觸發程序同一份，事件之後 TO SERVICE。
        // ON 只展開兩層：第三層是每一個事件各立一個只接 TO 的片語（上千個），事件由 FOR 那一格的片語列。
        // DROP 一次刪得了幾個，名稱以 … 跨過。
        new("CREATE EVENT NOTIFICATION {name} ON") { Expand = 2 },
        new("CREATE EVENT NOTIFICATION {name} ON SERVER FOR"),
        new("CREATE EVENT NOTIFICATION {name} ON DATABASE FOR"),
        new("CREATE EVENT NOTIFICATION {name} ON SERVER FOR {name}") { Expand = 2 },
        new("CREATE EVENT NOTIFICATION {name} ON DATABASE FOR {name}") { Expand = 2 },
        new("DROP EVENT NOTIFICATION ... ON") { Gap = "a, b" },

        // 擴充事件：名稱之後的 ON SERVER，之後是 ADD EVENT、ADD TARGET。ALTER 的 ADD、DROP 剖析器要看到 EVENT、TARGET
        // 才收，整段是證據。ADD 能開始一句（ADD SIGNATURE），位置分析把每一段 ADD 當成動詞，後面的 ADD 判不出前一格：
        // 以尾巴認，事件之後接逗號是下一個事件、不接逗號是目標。
        new("CREATE EVENT SESSION {name} ON") { Expand = 3 },
        new("ALTER EVENT SESSION {name} ON") { Expand = 2 },
        new("DROP EVENT SESSION {name} ON"),
        new("EVENT {name} ,") { Lead = "CREATE EVENT SESSION t ON SERVER ADD " },
        new("EVENT {name} , ADD") { Lead = "CREATE EVENT SESSION t ON SERVER ADD " },
        new("EVENT {name} ADD") { Lead = "CREATE EVENT SESSION t ON SERVER ADD " },
        new("TARGET {name} ,") { Lead = "CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD " },
        new("TARGET {name} , ADD") { Lead = "CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD " },
        new("ALTER EVENT SESSION {name} ON SERVER ADD EVENT"),
        new("ALTER EVENT SESSION {name} ON SERVER ADD TARGET"),
        new("ALTER EVENT SESSION {name} ON SERVER DROP EVENT"),
        new("ALTER EVENT SESSION {name} ON SERVER DROP TARGET"),
        new("EVENT {name} WITH (*") { Lead = "CREATE EVENT SESSION t ON SERVER ADD " },
        new("TARGET {name} WITH (*") { Lead = "CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD " },
        // 事件與目標可以帶一組括號（事件的 SET、ACTION、WHERE，目標的 SET），括號之後同樣接逗號、ADD、WITH。
        // SET 之後的欄位不列：剖析器什麼名稱都收，探不出來；欄位又依目標而不同（event_file 的 filename、ring_buffer 的
        // max_memory），尾巴的 {name} 分不出是哪一個目標，手寫聯集會列出別的目標的欄位。
        new("EVENT {name} (*") { Lead = "CREATE EVENT SESSION t ON SERVER ADD " },
        new("EVENT {name} () ,") { Lead = "CREATE EVENT SESSION t ON SERVER ADD ", Group = "(ACTION (t.t))" },
        new("EVENT {name} () , ADD") { Lead = "CREATE EVENT SESSION t ON SERVER ADD ", Group = "(ACTION (t.t))" },
        new("EVENT {name} () ADD") { Lead = "CREATE EVENT SESSION t ON SERVER ADD ", Group = "(ACTION (t.t))" },
        new("EVENT {name} () WITH (*") { Lead = "CREATE EVENT SESSION t ON SERVER ADD ", Group = "(ACTION (t.t))" },
        new("TARGET {name} (*") { Lead = "CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD " },
        new("TARGET {name} () ,") { Lead = "CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD ", Group = "(SET t = 1)" },
        new("TARGET {name} () , ADD") { Lead = "CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD ", Group = "(SET t = 1)" },
        new("TARGET {name} () WITH (*") { Lead = "CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD ", Group = "(SET t = 1)" },
    ];
}
