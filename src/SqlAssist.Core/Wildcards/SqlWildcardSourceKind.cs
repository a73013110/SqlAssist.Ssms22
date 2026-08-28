namespace SqlAssist.Core.Wildcards;

/// <summary>展開萬用字元時，一個欄位來源的名稱是怎麼來的。</summary>
/// <remarks>
/// 分成兩種是因為解析的責任在不同的地方：資料表與檢視的欄位只有中繼資料層知道，
/// 而子查詢與 CTE 的輸出欄位寫在指令碼裡，詞法分析當場就讀得出來。
/// </remarks>
public enum SqlWildcardSourceKind
{
    /// <summary>要向中繼資料層查詢欄位的資料來源。</summary>
    Table,

    /// <summary>欄位名稱已經從指令碼讀出來了。</summary>
    Names
}
