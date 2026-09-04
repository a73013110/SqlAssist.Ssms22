

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

    /// <summary>
    /// 系統物件：<c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 底下的目錄檢視、動態管理檢視
    /// 與系統預存程序。
    /// </summary>
    /// <remarks>
    /// <b>與第一層分開，而且只在使用者真的打出 <c>sys.</c> 或落在 <c>EXEC </c> 之後
    /// 才查。</b>光是一個使用者資料庫底下，這一份就有一兩千列——併進第一層等於讓每一次
    /// 開啟查詢視窗都多付兩倍的代價，換來的東西九成的時間沒有人要。
    ///
    /// 只收這兩個結構描述：<c>sys.all_objects</c> 裡 <c>is_ms_shipped = 1</c> 的東西
    /// 還包含一堆內部物件，而使用者打得出來的就是這兩個名字。
    ///
    /// <c>X</c> 是擴充預存程序，<c>sp_executesql</c> 就在那一類。
    /// </remarks>
    public const string SystemObjects = @"
SELECT
    o.object_id,
    s.name AS schema_name,
    o.name AS object_name,
    CASE WHEN o.type = 'X' THEN 'P' ELSE o.type END AS type
FROM sys.all_objects AS o
INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 1
  AND s.name IN ('sys', 'INFORMATION_SCHEMA')
  AND o.type IN ('U', 'V', 'P', 'PC', 'X', 'FN', 'IF', 'TF', 'FS', 'FT');";

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
    /// 第一層：這台伺服器掛的連結伺服器，四段式名稱的第一段。
    /// </summary>
    /// <remarks>
    /// <c>is_linked = 1</c> 把自己這一列（<c>server_id = 0</c>）排除掉：本機伺服器
    /// 的名字寫在四段式名稱裡雖然合法，走的卻不是連結伺服器那條路，
    /// 列進來會讓使用者選到一條繞遠路的寫法。
    ///
    /// <c>sys.servers</c> 依權限過濾列，沒有權限的登入看到的是空的而不是錯誤，
    /// 所以這條查詢不需要另外的降級。
    /// </remarks>
    public const string LinkedServers = @"
SELECT s.name
FROM sys.servers AS s
WHERE s.is_linked = 1
ORDER BY s.name;";

    /// <summary>
    /// 第二層：單一物件的欄位。主索引鍵資訊由 sys.indexes／sys.index_columns 帶出，
    /// 讓滑鼠停留提示能直接標示 PK。
    /// </summary>
    /// <remarks>
    /// <c>GENERATED ALWAYS</c>（時態資料表的期間欄位、帳本資料表的異動欄位）走
    /// <c>COLUMNPROPERTY</c> 而不是 <c>sys.columns.generated_always_type</c>：
    /// 那一欄要 SQL Server 2016 才有，直接 SELECT 它會讓整份欄位查詢在更舊的執行個體上
    /// 變成語法錯誤——而 <c>TryLoad</c> 會把它降級成「這一輪沒有資料」，
    /// 於是欄位建議、萬用字元展開與結構預覽在那些伺服器上會一起安靜地消失。
    /// <c>COLUMNPROPERTY</c> 對認不得的屬性名稱回傳 NULL，NULL &gt; 0 不成立，
    /// 舊版因此自然得到 0，不必為此再開一條依版本組字串的路。
    /// </remarks>
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
    cc.definition AS computed_definition,
    CONVERT(bit, CASE
        WHEN COLUMNPROPERTY(c.object_id, c.name, 'GeneratedAlwaysType') > 0 THEN 1
        ELSE 0
    END) AS is_generated_always
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
    /// <remarks>
    /// 讀 <c>sys.sql_modules</c> 而不是 <c>OBJECT_DEFINITION</c>，雖然兩者讀的是
    /// 同一欄：那是本機函式，加不了限定字，跨到連結伺服器時會在<b>對方登入的
    /// 預設資料庫</b>裡找 object_id，於是拿到另一個資料庫裡剛好同號的那個物件的
    /// 定義——而畫面上看不出來。目錄檢視則跟著 <see cref="SqlCatalogQualifier"/> 走。
    ///
    /// 行為完全一致：加密物件的那一列 <c>definition</c> 是 NULL，沒有
    /// <c>VIEW DEFINITION</c> 權限時整列看不到，而呼叫端用 <c>ExecuteScalar</c>
    /// 讀，兩種都得到 null。
    /// </remarks>
    public const string Definition = @"
SELECT m.definition
FROM sys.sql_modules AS m
WHERE m.object_id = @objectId;";

    /// <summary>
    /// 第三層：同義字指向的物件。
    /// </summary>
    /// <remarks>
    /// 同義字沒有 <c>sys.sql_modules</c> 的列，<c>OBJECT_DEFINITION</c> 對它一律
    /// 回傳 NULL——這一欄就是它的定義。取回來之後由
    /// <see cref="SqlAssist.Metadata.Formatting.SqlCatalogScript.ForSynonym"/> 組成 <c>CREATE SYNONYM</c>。
    ///
    /// <c>base_object_name</c> 存的是已經加好方括號的多段式名稱，而且不保證指得到
    /// 存在的物件：同義字可以指向一個還沒建立、甚至在別台伺服器上的東西。
    /// 因此這裡不 JOIN 回 <c>sys.objects</c>——那會讓一個完全合法的同義字查不到定義。
    /// </remarks>
    public const string SynonymBase = @"
SELECT sn.base_object_name
FROM sys.synonyms AS sn
WHERE sn.object_id = @objectId;";

    /// <summary>
    /// 第三層：序列的界限、循環與快取設定。
    /// </summary>
    /// <remarks>
    /// 四個界限值在 <c>sys.sequences</c> 裡是 <c>sql_variant</c>，實際型別隨序列
    /// 自己的型別而變。在伺服器端 <c>CONVERT</c> 成字串再讀：用
    /// <c>IDataRecord.GetValue</c> 收 <c>sql_variant</c> 拿到的是裝箱的原生型別，
    /// 一個 <c>decimal(38,0)</c> 的序列會讓任何一種整數轉型當場溢位，
    /// 而那一整份中繼資料會被降級成「這一輪沒有資料」。
    ///
    /// <c>current_value</c> 刻意不取：它每取一次號就變，而這裡要的是定義。
    /// </remarks>
    public const string Sequence = @"
SELECT
    t.name AS type_name,
    s.precision,
    s.scale,
    CONVERT(nvarchar(64), s.start_value) AS start_value,
    CONVERT(nvarchar(64), s.increment) AS increment,
    CONVERT(nvarchar(64), s.minimum_value) AS minimum_value,
    CONVERT(nvarchar(64), s.maximum_value) AS maximum_value,
    s.is_cycling,
    s.is_cached,
    s.cache_size
FROM sys.sequences AS s
INNER JOIN sys.types AS t ON t.user_type_id = s.user_type_id
WHERE s.object_id = @objectId;";
}
