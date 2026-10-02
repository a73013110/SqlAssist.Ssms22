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
    ];

    internal static readonly PhraseDeclaration[] Clauses =
    [
        // FOR 有好幾種意思，由前一格的位置分開：查詢寫完之後是 XML、JSON、BROWSE、UPDATE、READ，
        // 資料表之後多一個 SYSTEM_TIME，游標選項之後是查詢，觸發程序標頭之後是 INSERT 這些事件。
        // 查詢寫到 FOR UPDATE 已經完整，展開停在那裡；游標要的 OF 另外探。
        new("FOR") { After = QueryTails, Expand = 1 },
        new("FOR UPDATE") { After = QueryTails },
        new("FOR SYSTEM_TIME") { After = ["TableSourceTail"], Expand = 1 },
        new("FOR") { After = ["CursorOption"] },
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

        // 有序集合彙總：STRING_AGG、PERCENTILE_CONT 的呼叫之後是 WITHIN GROUP (ORDER BY …)。剖析器要看到 GROUP
        // 才收 WITHIN，WITHIN 由整段證據補到函式呼叫之後；WITHIN GROUP 之後只接左括號。
        new("WITHIN GROUP") { After = ["FunctionCallTail"] },
        new("WITHIN GROUP (*") { After = ["FunctionCallTail"], Clause = true },

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

    internal static readonly PhraseDeclaration[] OffsetFetch =
    [
        new("ROWS") { After = ["OffsetTail"], Values = ["FETCH"] },
        new("ROW") { After = ["OffsetTail"], Values = ["FETCH"] },
        new("ROWS FETCH") { After = ["OffsetTail"] },
        new("ROW FETCH") { After = ["OffsetTail"] },
        new("FETCH NEXT {value}") { Lead = "SELECT a FROM t ORDER BY a OFFSET 0 ROWS " },
        new("FETCH FIRST {value}") { Lead = "SELECT a FROM t ORDER BY a OFFSET 0 ROWS " },
        new("FETCH NEXT {value} ROWS") { Lead = "SELECT a FROM t ORDER BY a OFFSET 0 ROWS " },
        new("FETCH NEXT {value} ROW") { Lead = "SELECT a FROM t ORDER BY a OFFSET 0 ROWS " },
        new("FETCH FIRST {value} ROWS") { Lead = "SELECT a FROM t ORDER BY a OFFSET 0 ROWS " },
        new("FETCH FIRST {value} ROW") { Lead = "SELECT a FROM t ORDER BY a OFFSET 0 ROWS " },
    ];
}
