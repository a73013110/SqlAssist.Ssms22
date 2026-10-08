using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>查詢：MERGE、資料列集函式、FOR、運算式之後的字、TABLESAMPLE、OFFSET … FETCH。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class QueryPhrases
{
    internal static readonly string[] QueryTails = ["SelectListTail", "TableSourceTail", "ExpressionTail", "OrderByTail", "GroupByTail"];

    private static readonly string[] GroupingItems = ["ROLLUP", "CUBE", "GROUPING SETS"];

    private static readonly string[] GroupingSetItems = ["ROLLUP", "CUBE"];

    private static readonly string[] JoinTypes = ["INNER", "LEFT", "RIGHT", "FULL", "LEFT OUTER", "RIGHT OUTER", "FULL OUTER"];

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
        // BULK 的 ORDER 是資料檔的排序：一項是資料行，之後是 ASC、DESC。ORDER 只能寫在資料檔之後，探測墊 BULK 與格式檔。
        new("OPENROWSET (* ORDER (* {name}") { After = ["DataSource"], Items = "BULK 'x', FORMATFILE = 'f', " },
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
        // CONTAINS 的第一個引數另可寫 PROPERTY (資料行, '屬性')：名稱接不上那組括號，續尾手寫；資料行照常列，不封閉。
        new("CONTAINS (") { After = ["Predicate"], Endings = ["(a, 'p'), 'x')"], Closed = false },
        new("CONTAINS (* {value} , {value} ,") { After = ["Predicate"] },
        new("FREETEXT (* {value} , {value} ,") { After = ["Predicate"] },
        new("PREDICT (*") { After = ["DataSource"] },
        // CHANGETABLE 括號裡先寫 CHANGES 或 VERSION，最後一個選用引數是 FORCESEEK：CHANGES 之後是資料表與版本，
        // VERSION 之後是資料表、主索引鍵資料行與值。
        new("CHANGETABLE (") { After = ["DataSource"] },
        new("CHANGETABLE ( CHANGES {name} , {value} ,") { After = ["DataSource"], Endings = [") AS c"] },
        new("CHANGETABLE ( VERSION {name} , () , () ,") { After = ["DataSource"], Endings = [") AS c"] },
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
        // 判得出的另立帶位置的一條：NEXT、AT 這種第一個字也要列得出下一個字；墊的文字與那個位置的樣板相同，
        // 兩條才是同一個片語。AT TIME ZONE 接在任何運算元之後，子句、預設值與計算運算式都一樣，
        // 位置分析把運算元之後疊在那些尾端上；墊在括號裡，AT 才不會被讀成選取清單的別名。
        new("NEXT VALUE FOR") { Lead = "SELECT " },
        new("NEXT VALUE FOR") { After = ["SelectList"] },
        new("DEFAULT {value} FOR") { Lead = "ALTER TABLE t ADD " },
        new("AT TIME") { Lead = "SELECT (a " },
        new("AT TIME") { After = ["OperandTail"] },
        // 時區是運算式：名單由執行個體名單給，片語只說這一格收不收變數。
        new("AT TIME ZONE") { Lead = "SELECT (a " },
        new("AT TIME ZONE") { After = ["OperandTail"] },

        // AI_GENERATE_EMBEDDINGS 的來源之後是 USE MODEL 與模型名稱，再來是選用的 PARAMETERS。來源是運算式（{value}
        // 也比對得到資料行）；函式引數裡判不出位置，從呼叫寫起。
        new("AI_GENERATE_EMBEDDINGS (* {value} USE") { Lead = "SELECT " },
        new("AI_GENERATE_EMBEDDINGS (* {value} USE MODEL {name}") { Lead = "SELECT " },

        // PARSE、TRY_PARSE 的型別之後是 USING 文化特性：型別是名稱，Lead 片語以名稱結尾的一段不立，另外宣告。
        new("PARSE (* {value} AS {name}") { Lead = "SELECT " },
        new("TRY_PARSE (* {value} AS {name}") { Lead = "SELECT " },

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
        // IS NOT DISTINCT FROM 比 IS 多三層；展開到第三層的話 IS DISTINCT FROM 也往下一層，值之後的字立成片語，
        // 藏掉述詞尾端其餘的字。
        new("IS NOT DISTINCT FROM") { After = ["ExpressionTail"], Template = 1 },
        new("IS NOT DISTINCT FROM") { After = ["CaseArm"] },

        // GROUP BY 的一項可以是 ROLLUP、CUBE、GROUPING SETS，第幾項都一樣；剖析器把它們當函式名稱讀，只能手寫。
        // 逗號之後以尾巴認：寫成清單片語的話 GROUP BY 之後成了 OptionItem。GROUPING 之後的 SETS：中段的 ,* 讓第一項與逗號之後同一條尾巴。
        new("GROUP BY") { After = ["SelectListTail", "TableSourceTail", "ExpressionTail"], Values = GroupingItems },
        new("GROUP BY ,* ,") { After = ["SelectListTail", "TableSourceTail", "ExpressionTail"], Values = GroupingItems },
        new("GROUP BY ,* GROUPING") { After = ["SelectListTail", "TableSourceTail", "ExpressionTail"] },
        // GROUPING SETS 括號裡的一項也可以是 ROLLUP、CUBE，或再一組括號，那一組裡的項同樣接得了它們。
        // 那一組的資料行要關兩層括號才寫得完，續尾寫不出來，宣告不封閉。
        new("GROUP BY ,* GROUPING SETS (*") { After = ["SelectListTail", "TableSourceTail", "ExpressionTail"], Values = GroupingSetItems },
        new("GROUP BY ,* GROUPING SETS (* (*") { After = ["SelectListTail", "TableSourceTail", "ExpressionTail"], Values = GroupingSetItems, Closed = false },
        // 舊寫法 GROUP BY a, b WITH ROLLUP／CUBE：分組項寫完之後的 WITH。
        new("WITH") { After = ["GroupByTail"] },

        // 聯結類型之後是 JOIN 或聯結提示（LOOP、HASH、MERGE、REMOTE），提示之後只有 JOIN。
        // JOIN 之後的資料表還要寫 ON 才完整，續尾寫不完，宣告不封閉。
        .. JoinTypes.Select(join => new PhraseDeclaration(join) { After = ["TableSourceTail"], Expand = 1 }),
        .. JoinTypes.Select(join => new PhraseDeclaration(join + " JOIN") { After = ["TableSourceTail"], Closed = false }),

        // 一句開頭的 WITH 之後是 CTE 的新名字，或 XMLNAMESPACES 這種前置子句：要寫完括號與後面那一句才驗。
        // CTE 名稱什麼都收，續尾寫不出 AS (…)，宣告不封閉。
        new("WITH") { Endings = [" ('x' AS n) SELECT 1", " (0x01) DELETE FROM t"], Closed = false },

        // TOP 子句寫完（TOP 10、TOP (10)、TOP 10 PERCENT）之後的 WITH 只接 TIES。
        new("WITH") { After = ["TopClauseTail"] },

        // OFFSET … FETCH：每一格只有一兩個字，但沒有它們就得整句背下來。
        // OFFSET 10 ROWS 已經是完整的語句，FETCH 同時是游標語句的開頭，被當成下一句扣掉了。
        new("") { After = ["OffsetTail"] },
        new("") { After = ["FunctionReturns"] },
    ];

    // 資料表之後的 TABLESAMPLE 接選用的 SYSTEM 或直接接括號。樣本大小寫完、括號關上之後是 REPEATABLE (seed)，
    // 也還是資料來源的尾端（WHERE、JOIN、別名）：只加字，否則別名那一格跟著封閉。
    internal static readonly PhraseDeclaration[] TableSample =
    [
        new("TABLESAMPLE") { After = ["TableSourceTail"] },
        new("") { After = ["TableSampleTail"] },
        new("TABLESAMPLE ()") { After = ["TableSourceTail"], Group = "(10 PERCENT)", Additive = true },
        new("TABLESAMPLE SYSTEM ()") { After = ["TableSourceTail"], Group = "(10 PERCENT)", Additive = true },
    ];

    // OPTION (OPTIMIZE FOR (@a = 1, @b UNKNOWN))：括號裡每個變數之後是 = 常值或 UNKNOWN，第幾個都一樣。
    // OPTION ( 是查詢提示的封閉清單，位置分析判不出那一格，從提示寫起。
    internal static readonly PhraseDeclaration[] QueryHints =
    [
        new("OPTIMIZE FOR (* {name}") { Lead = "SELECT 1 OPTION (" },
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

    // 靜態欄位依序初始化，要寫在用到它的 JsonFunctions 之前。子句寫到 RETURNING 為止，之後的 JSON 由探測列出（JSON_VALUE 的型別由型別清單給）：
    // 寫到 JSON 的話，每一條各多一個什麼字都不接的片語。
    private static readonly string[] JsonClauses = ["NULL ON NULL RETURNING", "ABSENT ON NULL RETURNING", "RETURNING"];

    // JSON 建構函式的引數之後是 NULL 的處理（NULL ON NULL、ABSENT ON NULL）與 RETURNING JSON。函式引數裡判不出位置，
    // 從呼叫寫起；引數是 {value}（一個運算式）。每一格的字由整段證據補進它前面那段，只有引數剛寫完那一格以值結尾、
    // 補不出來（Lead 片語以值結尾的一段不立），另外宣告。沒有引數的 JSON_OBJECT(NULL ON NULL) 從左括號寫起，
    // 左括號本身也宣告：ABSENT 不是運算式的字。空呼叫照剖析器收的寫：JSON_ARRAYAGG 不收，只寫 RETURNING JSON 的也不收。
    // JSON_VALUE 的路徑之後只有 RETURNING 與型別（SQL Server 2025），沒有 NULL 的處理；路徑是第二個引數，探測墊第一個。
    // 左括號之後的引數是運算式，資料行寫得進去，只是後面還要寫 : 值，續尾寫不完：宣告不封閉。
    // 彙總只有一個引數，之後是子句：左括號寫單獨的 (，否則 JSON_ARRAYAGG(a ORDER BY a DESC, b 的逗號比對成下一個引數，
    // 列的是引數之後的字、沒有 ASC。排序項是 ORDER BY 的逗號清單，第幾項都一樣，項寫完的 ASC、DESC 之後也接 NULL 的處理。
    internal static readonly PhraseDeclaration[] JsonFunctions =
    [
        .. Json("JSON_OBJECT", "{value} : {value}", empty: true),
        .. Json("JSON_ARRAY", "{value}", empty: true),
        .. Json("JSON_OBJECTAGG", "{value} : {value}", empty: true, open: "("),
        .. Json("JSON_ARRAYAGG", "{value}", empty: false, open: "("),
        .. new[] { "", " ASC", " DESC" }.SelectMany(order => Json("JSON_ARRAYAGG", "{value} ORDER BY ,* {value}" + order, empty: false, open: "(")),
        .. Json("JSON_VALUE", "{value}", empty: false, onNull: false, items: "'x', "),
    ];

    private static IEnumerable<PhraseDeclaration> Json(string function, string argument, bool empty, bool onNull = true, string? items = null, string open = "(*") =>
        (empty ? [$"{function} {open}"] : Array.Empty<string>())
            .Append($"{function} {open} {argument}")
            .Concat(JsonClauses.Where(clause => onNull || !clause.Contains(" ON NULL")).Select(clause => $"{function} {open} {argument} {clause}"))
            .Concat(empty ? JsonClauses.Where(clause => clause.Contains(" ON NULL")).Select(clause => $"{function} {open} {clause}") : [])
            .Select(pattern => new PhraseDeclaration(pattern) { Lead = "SELECT ", Items = items, Closed = pattern == $"{function} {open}" ? false : null });

    private static IEnumerable<PhraseDeclaration> RowCounts(string[] patterns) =>
        patterns.Select(pattern => new PhraseDeclaration(pattern) { Lead = "SELECT * FROM t ORDER BY a OFFSET 10 ROWS FETCH " });
}
