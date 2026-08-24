namespace SqlAssist.Metadata;

/// <summary>
/// 中繼資料查詢。刻意分成三層，讓第一次按鍵只付出最小代價：
/// 第一層只取物件名稱，第二、三層等到使用者真的選取或停留在某個物件才查。
/// </summary>
public static class SqlMetadataQueries
{
    /// <summary>參數名稱：目標物件的 object_id。</summary>
    public const string ObjectIdParameterName = "@objectId";

    /// <summary>
    /// 第一層：物件清單。只取識別欄位，不含定義本文——真實資料庫裡
    /// sys.sql_modules.definition 動輒數 MB，不能在每次開啟編輯器時全部拉回來。
    /// </summary>
    public const string Objects = @"
SELECT
    o.object_id,
    s.name AS schema_name,
    o.name AS object_name,
    o.type
FROM sys.objects AS o
INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
UNION ALL
SELECT
    sn.object_id,
    s.name AS schema_name,
    sn.name AS object_name,
    'SN' AS type
FROM sys.synonyms AS sn
INNER JOIN sys.schemas AS s ON s.schema_id = sn.schema_id
WHERE sn.is_ms_shipped = 0;";

    /// <summary>第一層：結構描述清單。</summary>
    public const string Schemas = @"
SELECT s.name
FROM sys.schemas AS s
WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
  AND s.principal_id <> 4
ORDER BY s.name;";

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
    dc.definition AS default_definition
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
WHERE c.object_id = @objectId
ORDER BY c.column_id;";

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
