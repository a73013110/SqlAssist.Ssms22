using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 一個資料庫的資料字典：一次兩段，整個資料庫的資料表、檢視與資料行。
/// </summary>
/// <remarks>
/// 以集合為單位問，不是每張表一輪：字典一打開就要看得到每張表的說明覆蓋率，
/// 而照第二層那樣一張一張問，一百張表就是一百輪來回。兩段在同一個命令裡（<see cref="Load"/>），
/// 讀取端用 <c>NextResult</c> 接，仍然只有一輪。
///
/// 範圍只有 <c>U</c> 與 <c>V</c>：說明掛得上去、也有資料行的就這兩種。資料表型別與資料表值函式的
/// 資料行也有說明，但它們不是使用者會在「這張表的欄位是什麼意思」這件事上找的東西。
/// <c>is_ms_shipped</c> 擋掉複寫、CDC 那些系統建立的表。
///
/// 只在選定的那個資料庫上執行（連線已經指向它），不跨連結伺服器，所以沒有限定字的問題；
/// 照樣不用 <c>OBJECT_NAME</c> 那一族——寫成目錄檢視的 JOIN 一樣便宜，也不必另立豁免。
/// </remarks>
public static class SqlDataDictionaryQueries
{
    /// <summary>
    /// 第一段：物件與物件本身的說明。
    /// </summary>
    /// <remarks>欄位順序：object_id、schema_name、object_name、type、description。</remarks>
    public const string Objects = @"
SELECT
    o.object_id,
    s.name AS schema_name,
    o.name AS object_name,
    o.type,
    CONVERT(nvarchar(max), ep.value) AS description
FROM sys.objects AS o
INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
LEFT JOIN sys.extended_properties AS ep
    ON ep.class = 1
   AND ep.major_id = o.object_id
   AND ep.minor_id = 0
   AND ep.name = 'MS_Description'
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V')
ORDER BY s.name, o.name;";

    /// <summary>
    /// 第二段：上面那些物件的資料行、型別與說明。
    /// </summary>
    /// <remarks>
    /// 欄位順序：object_id、column_id、column_name、type_name、max_length、precision、scale、
    /// is_nullable、is_identity、is_computed、is_primary_key、description、vector_dimensions、vector_base_type_desc。
    ///
    /// <c>vector</c> 的維度與基底型別走第二層同一條退路（<c>SqlMetadataQueries.NoVector</c>）：
    /// 直接 SELECT 那兩欄在 SQL Server 2025 之前是語法錯誤，整個字典會降級成一列都沒有。
    /// 主索引鍵的 JOIN 與第二層同形；照 <c>object_id</c>、<c>column_id</c> 排序，讀取端逐物件收成一組。
    /// </remarks>
    public const string Columns = @"
SELECT
    c.object_id,
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
    CONVERT(nvarchar(max), ep.value) AS description,
    vec.vector_dimensions,
    vec.vector_base_type_desc
FROM sys.columns AS c
CROSS JOIN " + SqlMetadataQueries.NoVector + @"
OUTER APPLY (
    SELECT vector_dimensions, vector_base_type_desc
    FROM sys.columns AS v
    WHERE v.object_id = c.object_id AND v.column_id = c.column_id
) AS vec
INNER JOIN sys.objects AS o ON o.object_id = c.object_id
INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.indexes AS pk
    ON pk.object_id = c.object_id AND pk.is_primary_key = 1
LEFT JOIN sys.index_columns AS pkc
    ON pkc.object_id = c.object_id
   AND pkc.index_id = pk.index_id
   AND pkc.column_id = c.column_id
LEFT JOIN sys.extended_properties AS ep
    ON ep.class = 1
   AND ep.major_id = c.object_id
   AND ep.minor_id = c.column_id
   AND ep.name = 'MS_Description'
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V')
ORDER BY c.object_id, c.column_id;";

    /// <summary>兩段一起送：一輪來回。</summary>
    public const string Load = Objects + Columns;
}
