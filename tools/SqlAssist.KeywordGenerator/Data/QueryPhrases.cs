using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>查詢：MERGE、資料列集函式、FOR、運算式之後的字、TABLESAMPLE、OFFSET … FETCH。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class QueryPhrases
{
    internal static readonly string[] QueryTails = ["SelectListTail", "TableSourceTail", "ExpressionTail", "OrderByTail", "GroupByTail"];

    internal static readonly PhraseDeclaration[] Merge =
    [
        // MERGE 的 WHEN 之後是 MATCHED 與 NOT MATCHED，這兩者之後各再一層；NOT MATCHED 由下一條補出來。
        // NOT MATCHED BY TARGET／SOURCE 之後各再一層（THEN、AND）。
        // THEN 之後的動作：WHEN MATCHED 接 UPDATE、DELETE，WHEN NOT MATCHED 接 INSERT，INSERT 用第二個樣板探測；
        // INSERT 的資料行清單之後是 VALUES（沒有清單的 INSERT VALUES、INSERT DEFAULT VALUES 由 INSERT 那條給）。
        new("") { After = ["MergeWhen"], Expand = 1 },
        new("NOT MATCHED BY") { After = ["MergeWhen"], Expand = 1 },
        new("UPDATE") { After = ["MergeAction"] },
        new("INSERT") { After = ["MergeAction"], Template = 1 },
        new("INSERT ()") { After = ["MergeAction"], Template = 1 },
    ];

    internal static readonly PhraseDeclaration[] Rowsets =
    [
        new("OPENROWSET (*") { After = ["DataSource"] },
        // VECTOR_SEARCH 的具名引數順序固定（TABLE、COLUMN、SIMILAR_TO、METRIC、TOP_N），清單一項一項往下探。
        // METRIC 只收距離的名稱，共用續尾寫不完那一項就探不到後面的 TOP_N。
        new("VECTOR_SEARCH (*") { After = ["DataSource"], Endings = [" = 'cosine'"] },
        // AI_GENERATE_CHUNKS 的具名引數：SOURCE、CHUNK_TYPE 之後是選用的 CHUNK_SIZE、OVERLAP、ENABLE_CHUNK_SET_ID。
        // CHUNK_TYPE 的值剖析器當名稱讀，只收 FIXED。
        new("AI_GENERATE_CHUNKS (*") { After = ["DataSource"], Endings = [" = FIXED"] },
        new("AI_GENERATE_CHUNKS (* CHUNK_TYPE =") { After = ["DataSource"], Items = "SOURCE = 'x', ", Values = ["FIXED"], Closed = true },
        // PREDICT 的具名引數依序是 MODEL、DATA、RUNTIME，括號之後一定要有 WITH 宣告輸出欄位。MODEL 之後的引數名稱
        // 剖析器什麼名字都收（留到語意檢查才擋），DATA 只能手寫；RUNTIME 的值也當名稱讀，只收 ONNX。
        // 全文檢索的 LANGUAGE 寫在查詢條件之後：資料列集函式是第四個引數起（資料表、資料行、條件），述詞是第三個起。
        // 前面幾個引數寫法不一（資料行可以是清單、*、PROPERTY(…)），都是運算式。
        new("CONTAINSTABLE (* {value} , {value} , {value} ,") { After = ["DataSource"] },
        new("FREETEXTTABLE (* {value} , {value} , {value} ,") { After = ["DataSource"] },
        new("CONTAINS (* {value} , {value} ,") { After = ["Predicate"] },
        new("FREETEXT (* {value} , {value} ,") { After = ["Predicate"] },
        new("PREDICT (*") { After = ["DataSource"] },
        new("PREDICT (* MODEL = {value} ,") { After = ["DataSource"], Values = ["DATA"], Closed = true },
        new("PREDICT (* RUNTIME =") { After = ["DataSource"], Items = "MODEL = @m, DATA = t AS d, ", Values = ["ONNX"], Closed = true },
    ];

    internal static readonly PhraseDeclaration[] Clauses =
    [
        // FOR 有好幾種意思，由前一格的位置分開：查詢寫完之後是 XML、JSON、BROWSE、UPDATE、READ，
        // 資料表之後多一個 SYSTEM_TIME，游標選項之後是查詢，觸發程序標頭之後是 INSERT 這些事件。
        // 查詢寫到 FOR UPDATE 已經完整，展開停在那裡；游標要的 OF 另外探。
        new("FOR") { After = QueryTails, Expand = 1 },
        new("FOR UPDATE") { After = QueryTails },
        new("FOR SYSTEM_TIME") { After = ["TableSourceTail"], Expand = 1 },
        new("FOR SYSTEM_TIME BETWEEN {value} AND {value}") { After = ["TableSourceTail"] },
        new("FOR SYSTEM_TIME FROM {value} TO {value}") { After = ["TableSourceTail"] },
        new("FOR") { After = ["CursorOption"] },
        // SELECT … INTO 的新資料表可以指定檔案群組（INTO t ON fg），之後同樣接 FROM、WHERE 這些子句。
        new("ON {name}") { After = ["SelectIntoTail"] },
        new("SYNONYM {name} FOR") { After = ["DdlObject"] },

        // FOR XML、FOR JSON 的模式是清單的第一項，由上面 FOR 往下展開的片語給；逗號之後是指示詞。
        new("FOR XML ,*") { After = QueryTails },
        new("FOR JSON ,*") { After = QueryTails },

        // 運算式寫在哪裡都行，函式引數裡判不出位置；CONSTRAINT df 之後也判不出來。
        // 選取清單裡判得出來，另立帶位置的一條：NEXT、AT 這種第一個字也要列得出下一個字。
        new("NEXT VALUE FOR") { Lead = "SELECT " },
        new("NEXT VALUE FOR") { After = ["SelectList"] },
        new("DEFAULT {value} FOR") { Lead = "ALTER TABLE t ADD " },
        new("AT TIME") { Lead = "SELECT a " },
        new("AT TIME") { After = ["SelectListTail"] },

        // AI_GENERATE_EMBEDDINGS 的來源之後是 USE MODEL 與模型名稱，再來是選用的 PARAMETERS。來源是運算式（{value}
        // 也比對得到資料行）；函式引數裡判不出位置，從呼叫寫起。
        new("AI_GENERATE_EMBEDDINGS (* {value} USE") { Lead = "SELECT " },
        new("AI_GENERATE_EMBEDDINGS (* {value} USE MODEL {name}") { Lead = "SELECT " },

        // 有序集合彙總：STRING_AGG、PERCENTILE_CONT 的呼叫之後是 WITHIN GROUP (ORDER BY …)。剖析器要看到 GROUP
        // 才收 WITHIN，WITHIN 由整段證據補到函式呼叫之後；WITHIN GROUP 之後只接左括號。
        // LAG、FIRST_VALUE 這類函式的呼叫之後可以寫 IGNORE NULLS、RESPECT NULLS，再接 OVER；以位移函式的樣板探測。
        new("WITHIN GROUP") { After = ["FunctionCallTail"] },
        // 圖形彙總的 WITHIN GROUP (GRAPH PATH)：GRAPH 要看到 PATH 與兩層右括號才驗。
        new("WITHIN GROUP (") { After = ["FunctionCallTail"], Endings = [" PATH))"], Expand = 1 },
        new("IGNORE NULLS") { After = ["FunctionCallTail"], Template = 1 },
        new("RESPECT NULLS") { After = ["FunctionCallTail"], Template = 1 },

        // UPDATE、DELETE 的 WHERE CURRENT OF 資料指標：CURRENT 之後只有 OF，OF 之後是資料指標名稱或 GLOBAL。
        // SELECT 的 WHERE 寫不出來，所以用 DELETE 的樣板。
        new("CURRENT") { After = ["Predicate"], Template = 1, Expand = 1 },

        // IS 之後是 NULL、NOT、DISTINCT FROM。述詞尾端的代表樣板寫完了比較，接不上 IS，用第二個。
        new("IS") { After = ["ExpressionTail"], Template = 1, Expand = 2 },
        new("IS") { After = ["CaseArm"], Expand = 2 },

        new("GROUP BY") { After = ["SelectListTail", "TableSourceTail", "ExpressionTail"], Values = ["ROLLUP", "CUBE", "GROUPING SETS"] },

        // TOP 子句寫完（TOP 10、TOP (10)、TOP 10 PERCENT）之後的 WITH 只接 TIES。
        new("WITH") { After = ["TopClauseTail"] },

        // OFFSET … FETCH：每一格只有一兩個字，但沒有它們就得整句背下來。
        // OFFSET 10 ROWS 已經是完整的語句，FETCH 同時是游標語句的開頭，被當成下一句扣掉了。
        new("") { After = ["OffsetTail"] },
        new("") { After = ["FunctionReturns"] },
    ];

    internal static readonly PhraseDeclaration[] TableSample =
    [
        new("") { After = ["TableSampleTail"] },
    ];

    // FETCH 的列數之後（NEXT 10 ROWS ONLY）不看前面是 OFFSET … ROWS 還是 SQL Server 2025 的 FETCH APPROX：尾巴從 NEXT、FIRST 寫起，
    // 兩種寫法共用。剖析器只收直接接在 ORDER BY 之後的 FETCH APPROX，與 OFFSET 並用就報錯。
    // 列數是運算式，比對見 SqlOperand。墊的文字與 OFFSET 的 ROWS FETCH 那一格相同，NEXT、FIRST 才認得是那一格列的字。
    internal static readonly PhraseDeclaration[] OffsetFetch =
    [
        new("ROWS") { After = ["OffsetTail"], Values = ["FETCH"] },
        new("ROW") { After = ["OffsetTail"], Values = ["FETCH"] },
        new("ROWS FETCH") { After = ["OffsetTail"] },
        new("ROW FETCH") { After = ["OffsetTail"] },
        new("FETCH APPROX") { After = ["OrderByTail"] },
        new("FETCH APPROXIMATE") { After = ["OrderByTail"] },
        .. RowCounts(["NEXT {value}", "FIRST {value}", "NEXT {value} ROWS", "NEXT {value} ROW", "FIRST {value} ROWS", "FIRST {value} ROW"]),
    ];

    // 靜態欄位依序初始化，要寫在用到它的 JsonConstructors 之前。子句寫到 RETURNING 為止，之後的 JSON 由探測列出：
    // 寫到 JSON 的話，每一條各多一個什麼字都不接的片語。
    private static readonly string[] JsonClauses = ["NULL ON NULL RETURNING", "ABSENT ON NULL RETURNING", "RETURNING"];

    // JSON 建構函式的引數之後是 NULL 的處理（NULL ON NULL、ABSENT ON NULL）與 RETURNING JSON。函式引數裡判不出位置，
    // 從呼叫寫起；引數是 {value}（一個運算式）。每一格的字由整段證據補進它前面那段，只有引數剛寫完那一格以值結尾、
    // 補不出來（Lead 片語以值結尾的一段不立），另外宣告。沒有引數的 JSON_OBJECT(NULL ON NULL) 從左括號寫起，
    // 左括號本身也宣告：ABSENT 不是運算式的字。空呼叫照剖析器收的寫：JSON_ARRAYAGG 不收，只寫 RETURNING JSON 的也不收。
    internal static readonly PhraseDeclaration[] JsonConstructors =
    [
        .. Json("JSON_OBJECT", "{value} : {value}", empty: true),
        .. Json("JSON_ARRAY", "{value}", empty: true),
        .. Json("JSON_OBJECTAGG", "{value} : {value}", empty: true),
        .. Json("JSON_ARRAYAGG", "{value}", empty: false),
        .. Json("JSON_ARRAYAGG", "{value} ORDER BY {value}", empty: false),
    ];

    private static IEnumerable<PhraseDeclaration> Json(string function, string argument, bool empty) =>
        (empty ? [$"{function} (*"] : Array.Empty<string>())
            .Append($"{function} (* {argument}")
            .Concat(JsonClauses.Select(clause => $"{function} (* {argument} {clause}"))
            .Concat(empty ? JsonClauses.Where(clause => clause.Contains(" ON NULL")).Select(clause => $"{function} (* {clause}") : [])
            .Select(pattern => new PhraseDeclaration(pattern) { Lead = "SELECT " });

    private static IEnumerable<PhraseDeclaration> RowCounts(string[] patterns) =>
        patterns.Select(pattern => new PhraseDeclaration(pattern) { Lead = "SELECT * FROM t ORDER BY a OFFSET 10 ROWS FETCH " });
}
