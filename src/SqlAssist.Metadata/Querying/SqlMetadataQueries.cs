

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 中繼資料查詢。刻意分層，讓第一次按鍵只付出最小代價：
/// 第一層只取物件名稱，第二、三層等到使用者真的選取或停留在某個物件才查，
/// 第四層的索引與外來鍵只有打開結構面板時才查。
/// </summary>
public static class SqlMetadataQueries
{
    /// <summary>參數名稱：目標物件的 object_id。</summary>
    public const string ObjectIdParameterName = "@objectId";

    /// <summary>
    /// 第一層：物件清單。只取識別欄位，不含定義本文——真實資料庫裡
    /// sys.sql_modules.definition 動輒數 MB，不能在每次開啟編輯器時全部拉回來。
    /// </summary>
    /// <remarks>
    /// 同義字與資料表型別不在 <c>sys.objects</c> 裡，各自 UNION 進來並貼上
    /// <c>SN</c>、<c>TT</c> 這兩個自訂標籤——那不是 <c>sys.objects.type</c> 的代碼，
    /// 而是為了讓三份結果共用同一個對應表。
    ///
    /// 資料表型別取的是 <c>type_table_object_id</c> 而不是 <c>user_type_id</c>：
    /// 快取以 object_id 為鍵，用型別自己的識別碼會與真的物件撞在一起；
    /// 而那個 object_id 同時正好是它的欄位在 <c>sys.columns</c> 裡的鍵。
    /// </remarks>
    public const string Objects = @"
SELECT
    o.object_id,
    s.name AS schema_name,
    o.name AS object_name,
    o.type
FROM sys.objects AS o
INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'TR', 'TA', 'SO')
UNION ALL
SELECT
    sn.object_id,
    s.name AS schema_name,
    sn.name AS object_name,
    'SN' AS type
FROM sys.synonyms AS sn
INNER JOIN sys.schemas AS s ON s.schema_id = sn.schema_id
WHERE sn.is_ms_shipped = 0
UNION ALL
SELECT
    tt.type_table_object_id AS object_id,
    s.name AS schema_name,
    tt.name AS object_name,
    'TT' AS type
FROM sys.table_types AS tt
INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
WHERE tt.is_user_defined = 1;";

    /// <summary>第一層：結構描述清單。</summary>
    public const string Schemas = @"
SELECT s.name
FROM sys.schemas AS s
WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
  AND s.principal_id <> 4
ORDER BY s.name;";

    /// <summary>
    /// 第一層：資料庫清單，供 <c>USE</c> 之後的建議使用。
    /// </summary>
    /// <remarks>
    /// 只列線上（state = 0）的資料庫：離線或還原中的資料庫 <c>USE</c> 不進去，
    /// 列出來只會讓使用者選到一個必定失敗的名稱。
    ///
    /// <c>HAS_DBACCESS</c> 把沒有權限的資料庫濾掉——在共用主機上
    /// <c>sys.databases</c> 看得到的名稱遠多於使用者進得去的。
    /// 它對離線資料庫回傳 NULL，因此比較寫成 = 1 而不是 &lt;&gt; 0。
    /// </remarks>
    public const string Databases = @"
SELECT d.name
FROM sys.databases AS d
WHERE d.state = 0
  AND HAS_DBACCESS(d.name) = 1
ORDER BY d.name;";

    /// <summary>
    /// 第二層：單一物件的欄位。主索引鍵資訊由 sys.indexes／sys.index_columns 帶出，
    /// 讓滑鼠停留提示能直接標示 PK。
    /// </summary>
    public const string Columns = @"
SELECT
    c.column_id,
    c.name AS column_name,
    t.name AS type_name,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    c.is_computed,
    CONVERT(bit, CASE WHEN pkc.column_id IS NULL THEN 0 ELSE 1 END) AS is_primary_key,
    dc.definition AS default_definition,
    cc.definition AS computed_definition
FROM sys.columns AS c
INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.indexes AS pk
    ON pk.object_id = c.object_id AND pk.is_primary_key = 1
LEFT JOIN sys.index_columns AS pkc
    ON pkc.object_id = c.object_id
   AND pkc.index_id = pk.index_id
   AND pkc.column_id = c.column_id
LEFT JOIN sys.default_constraints AS dc
    ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
LEFT JOIN sys.computed_columns AS cc
    ON cc.object_id = c.object_id AND cc.column_id = c.column_id
WHERE c.object_id = @objectId
ORDER BY c.column_id;";

    /// <summary>
    /// 第四層：單一資料表的索引。
    /// </summary>
    /// <remarks>
    /// 一個索引有幾個欄位就有幾列，合併交給 <see cref="SqlIndexInfo.FromRows"/>。
    /// <c>type = 0</c> 是堆積，沒有索引名稱也沒有意義，直接排除。
    /// 排序把索引鍵欄位排在 INCLUDE 欄位前面：INCLUDE 欄位的 key_ordinal 是 0，
    /// 只依 key_ordinal 排會讓它們跑到最前面。
    /// </remarks>
    public const string Indexes = @"
SELECT
    i.index_id,
    i.name AS index_name,
    i.is_primary_key,
    i.is_unique,
    i.is_unique_constraint,
    i.type_desc,
    i.filter_definition,
    c.name AS column_name,
    ic.is_descending_key,
    ic.is_included_column
FROM sys.indexes AS i
INNER JOIN sys.index_columns AS ic
    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns AS c
    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = @objectId
  AND i.type <> 0
  AND i.name IS NOT NULL
ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;";

    /// <summary>第四層：單一資料表向外參考的外來鍵；複合鍵會有多列。</summary>
    public const string ForeignKeys = @"
SELECT
    fk.name AS foreign_key_name,
    rs.name AS referenced_schema_name,
    ro.name AS referenced_object_name,
    pc.name AS column_name,
    rc.name AS referenced_column_name,
    fk.delete_referential_action_desc,
    fk.update_referential_action_desc
FROM sys.foreign_keys AS fk
INNER JOIN sys.foreign_key_columns AS fkc
    ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.columns AS pc
    ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
INNER JOIN sys.columns AS rc
    ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
INNER JOIN sys.objects AS ro
    ON ro.object_id = fk.referenced_object_id
INNER JOIN sys.schemas AS rs
    ON rs.schema_id = ro.schema_id
WHERE fk.parent_object_id = @objectId
ORDER BY fk.name, fkc.constraint_column_id;";

    /// <summary>第二層：單一模組的參數。</summary>
    public const string Parameters = @"
SELECT
    p.parameter_id,
    p.name AS parameter_name,
    t.name AS type_name,
    p.max_length,
    p.precision,
    p.scale,
    p.is_output
FROM sys.parameters AS p
INNER JOIN sys.types AS t ON t.user_type_id = p.user_type_id
WHERE p.object_id = @objectId
ORDER BY p.parameter_id;";

    /// <summary>第三層：模組定義本文。加密物件會回傳 NULL。</summary>
    public const string Definition = @"
SELECT OBJECT_DEFINITION(@objectId);";
}
