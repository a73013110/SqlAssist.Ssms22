namespace SqlAssist.Metadata.Search;

/// <summary>
/// 搜尋索引專用的查詢：一個資料庫要的東西一次撈完。
/// </summary>
/// <remarks>
/// 刻意不重用 <see cref="Querying.SqlMetadataQueries.Objects"/>：那一條是按需載入的第一層，
/// 只取識別欄位，定義本文留到第三層真的要顯示時才問。搜尋要的是全量——為每一個物件
/// 各送一次 <c>OBJECT_DEFINITION</c> 的話，一個幾千物件的資料庫等於幾千次來回，
/// 而使用者要的只是一輪搜尋。因此這裡把 <c>sys.sql_modules</c> 併進同一條查詢，
/// 一次來回拿回名稱與定義。
///
/// 結構描述清單不在這裡：那一份與第一層要的完全一樣，走
/// <see cref="Querying.SqlMetadataQueries.Schemas"/>。抄第二份的症狀是其中一份忘了
/// 排除 <c>sys</c>／<c>INFORMATION_SCHEMA</c>，而兩邊看到的結構描述開始不一樣。
///
/// 查詢一律寫成不加限定的 <c>sys.</c>：決定查哪一個資料庫的是連線，不是 SQL，
/// 跨資料庫只要換 <see cref="Querying.SqlDatabaseScopedConnectionSource"/>。
/// 也刻意不用 <c>OBJECT_DEFINITION</c> 這一族本機函式——它們加不了限定字，
/// 跨連結伺服器時會在對方登入的預設資料庫裡解析，而畫面上看不出退過。
/// </remarks>
public static class SqlCatalogSearchQueries
{
    /// <summary>
    /// 物件、版本戳與定義本文；一次來回。
    /// </summary>
    /// <remarks>
    /// 欄位順序：object_id、schema_name、object_name、type、modify_date、definition。
    /// 前四欄與 <see cref="Querying.SqlMetadataReader.ReadObject"/> 一致，讓物件那一段
    /// 直接共用既有的對應，不再寫第二份「哪一欄是什麼」。
    ///
    /// 同義字與資料表型別照第一層的作法 UNION 進來並貼上 <c>SN</c>、<c>TT</c>；
    /// 前者的定義在 <c>sys.synonyms.base_object_name</c>、後者根本沒有定義本文，
    /// 兩者的 <c>definition</c> 因此一律是 NULL。v1 <b>只索引它們的名稱</b>，
    /// 不在這裡把目錄檢視的欄位組回 <c>CREATE SYNONYM</c>／<c>CREATE SEQUENCE</c>：
    /// 組回去的那一份只有 <see cref="Formatting.SqlCatalogScript"/> 一支，
    /// 而它要的是第三層的 <c>SqlSequenceInfo</c> 與 base_object_name，
    /// 為全量索引再撈那兩份等於多兩輪掃全表。代價是同義字指向的目標名稱
    /// 搜不到（<c>SqlObjectKinds.HasSynthesizedDefinition</c> 那兩種沒有本文命中），
    /// 名稱命中不受影響。
    ///
    /// 資料表型別沒有自己的 <c>modify_date</c>，繞回 <c>sys.objects</c> 取；
    /// 接不到列時是 NULL，讀取端當成「這一列沒有戳」，不影響其他列算出來的最大值。
    /// </remarks>
    public const string ObjectsWithDefinitions = @"
SELECT
    o.object_id,
    s.name AS schema_name,
    o.name AS object_name,
    o.type,
    o.modify_date,
    m.definition
FROM sys.objects AS o
INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'TR', 'TA', 'SO')
UNION ALL
SELECT
    sn.object_id,
    s.name AS schema_name,
    sn.name AS object_name,
    'SN' AS type,
    sn.modify_date,
    NULL AS definition
FROM sys.synonyms AS sn
INNER JOIN sys.schemas AS s ON s.schema_id = sn.schema_id
WHERE sn.is_ms_shipped = 0
UNION ALL
SELECT
    tt.type_table_object_id AS object_id,
    s.name AS schema_name,
    tt.name AS object_name,
    'TT' AS type,
    o.modify_date,
    NULL AS definition
FROM sys.table_types AS tt
INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
LEFT JOIN sys.objects AS o ON o.object_id = tt.type_table_object_id
WHERE tt.is_user_defined = 1;";

    /// <summary>
    /// 整個資料庫的資料行名稱。
    /// </summary>
    /// <remarks>
    /// 欄位順序：object_id、column_name。只取這兩欄——搜尋比對的是名字，
    /// 型別、可否為 NULL 那些是第二層在使用者選了某一個物件之後才要的東西，
    /// 全量撈回來等於把第二層的成本乘上整個資料庫。
    ///
    /// 不加 <c>WHERE</c>：<c>sys.columns</c> 本來就只看得到這個登入有權限的物件，
    /// 而對不上物件清單的列（系統內部物件）由索引在合併時丟掉——在這裡 JOIN 回
    /// <c>sys.objects</c> 只是讓伺服器多做一次同樣的事。
    ///
    /// <c>ORDER BY</c> 走的正是 <c>sys.columns</c> 的叢集鍵，不會多一次排序，
    /// 換到的是「同一個資料庫每次跑出來的順序一樣」——少了它，同分的結果
    /// 每一輪的先後由伺服器決定，清單會自己跳。
    /// </remarks>
    public const string Columns = @"
SELECT
    c.object_id,
    c.name AS column_name
FROM sys.columns AS c
ORDER BY c.object_id, c.column_id;";
}
