using SqlAssist.Core.Keywords;

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
    /// 這台伺服器支援的定序名稱。
    /// </summary>
    /// <remarks>
    /// <c>sys.fn_helpcollations()</c> 從 SQL Server 2000 就有，而且不看權限——
    /// 這是一份與資料無關的常數表。它與物件清單分開快取：名單屬於<b>伺服器</b>，
    /// 與目前連線的是哪一個資料庫無關，跟著每一份目錄各存一次的話，
    /// 使用者每打出一個跨資料庫的限定字就多五千多個字串。
    ///
    /// 不做成寫死的內建目錄：SQL Server 2019 之後有五千五百筆以上，
    /// 而每一版都在增加——寫死的那一份會在下一版開始漏掉名稱，
    /// 而漏掉哪一個使用者完全看不出來。
    /// </remarks>
    public const string Collations = @"
SELECT c.name
FROM sys.fn_helpcollations() AS c
ORDER BY c.name;";

    /// <summary>
    /// 目前這個資料庫的定序。
    /// </summary>
    /// <remarks>
    /// 與定序名單分開查：這一個屬於資料庫，名單屬於伺服器，兩者的快取層級不同。
    ///
    /// 走 <c>DATABASEPROPERTYEX</c> 而不是 <c>sys.databases.collation_name</c>：
    /// 後者在共用主機上讀得到的列只有自己進得去的那幾個，而這條查的正是
    /// 目前連線的那一個——問自己一定答得出來。查不到時是 NULL，
    /// 呼叫端當成「這一輪沒有」，不是錯誤。
    /// </remarks>
    public const string DatabaseCollation = @"
SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));";

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
    ///
    /// <c>IsSparse</c> 與 <c>IsRowGuidCol</c> 走同一個函式，理由相同：
    /// <c>sys.columns.is_sparse</c> 要 SQL Server 2008 才有。
    ///
    /// 識別值的種子與遞增量在伺服器端就 <c>CONVERT</c> 成字串：那兩欄是
    /// <c>sql_variant</c>，用 <c>GetValue</c> 收到的是裝箱的原生型別，
    /// 一個 <c>decimal(38,0)</c> 的識別資料行會讓任何一種整數轉型當場溢位。
    ///
    /// 資料行的說明（<c>MS_Description</c>）掛在這一條上而不是另開一次查詢：
    /// <c>sys.extended_properties</c> 的鍵是 class＋major_id＋minor_id＋name，
    /// 四個都給定就最多接得到一列，多的只有一欄，不是多一輪來回。
    /// 值同樣在伺服器端 <c>CONVERT</c>——它也是 <c>sql_variant</c>。
    /// </remarks>
    public const string Columns = ColumnsHead + "sys.columns" + ColumnsTail;

    /// <summary>
    /// 第二層：系統物件的欄位。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Columns"/> 是同一份本體，只換掉資料行的目錄檢視：
    /// <c>sys.columns</c> 只收使用者物件，<c>sys.triggers</c>、
    /// <c>INFORMATION_SCHEMA.TABLES</c> 這一類系統檢視的資料行全都只在
    /// <c>sys.all_columns</c> 上。抄成第二份完整查詢的症狀是改了一邊另一邊沒改，
    /// 而少掉的那幾欄在畫面上看不出來。
    ///
    /// 反過來讓所有物件都走 <c>sys.all_columns</c> 也不行：那是一個聯集檢視，
    /// 而這一條在「使用者選了一張表」的路徑上，多付的是每一張使用者資料表。
    /// </remarks>
    public const string SystemColumns = ColumnsHead + "sys.all_columns" + ColumnsTail;

    /// <summary>
    /// 某個結構描述底下的物件該問哪一條欄位查詢。
    /// </summary>
    /// <remarks>
    /// 判斷放在查詢這一邊而不是載入那一邊：那裡拿得到的只有「這個物件是誰」，
    /// 而「這個名稱要問哪一個目錄檢視」是查詢自己的事，也只有在這裡測得到。
    /// </remarks>
    public static string ColumnsFor(string? schemaName)
    {
        return SqlSystemSchemas.IsSystem(schemaName) ? SystemColumns : Columns;
    }

    /// <summary>欄位查詢的前半段，到資料行的目錄檢視名稱為止。</summary>
    private const string ColumnsHead = @"
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
    END) AS is_generated_always,
    c.collation_name,
    CONVERT(nvarchar(64), ic.seed_value) AS identity_seed,
    CONVERT(nvarchar(64), ic.increment_value) AS identity_increment,
    dc.name AS default_constraint_name,
    dc.is_system_named AS default_is_system_named,
    cc.is_persisted,
    CONVERT(bit, CASE
        WHEN COLUMNPROPERTY(c.object_id, c.name, 'IsSparse') > 0 THEN 1
        ELSE 0
    END) AS is_sparse,
    CONVERT(bit, CASE
        WHEN COLUMNPROPERTY(c.object_id, c.name, 'IsRowGuidCol') > 0 THEN 1
        ELSE 0
    END) AS is_row_guid_col,
    CONVERT(nvarchar(max), ep.value) AS column_description
FROM ";

    /// <summary>欄位查詢的後半段，從資料行的目錄檢視名稱之後接下去。</summary>
    private const string ColumnsTail = @" AS c
INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.identity_columns AS ic
    ON ic.object_id = c.object_id AND ic.column_id = c.column_id
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
LEFT JOIN sys.extended_properties AS ep
    ON ep.class = 1
   AND ep.major_id = c.object_id
   AND ep.minor_id = c.column_id
   AND ep.name = 'MS_Description'
WHERE c.object_id = @objectId
ORDER BY c.column_id;";

    /// <summary>
    /// 第二層：物件自己的 <c>MS_Description</c>。
    /// </summary>
    /// <remarks>
    /// 說明放在第二層而不是跟著第四層的擴充屬性走，理由是<b>誰要看它</b>：
    /// 滑鼠停留提示只讀快取、不等查詢，而第四層要使用者主動打開結構才載入——
    /// 併在那裡的話，提示上的說明只有「剛好開過結構」的物件才有，
    /// 而畫面上看不出那個差別。
    ///
    /// 資料行的說明不走這一條，它跟著 <see cref="Columns"/> 的
    /// <c>LEFT JOIN</c> 一起回來：那是同一列多取一欄，不是多一次來回。
    /// 這一條問的是資料表本身（<c>minor_id = 0</c>），一列都沒有就是沒有說明。
    ///
    /// 只取 <c>MS_Description</c>。其餘擴充屬性一個都不會顯示在提示或預覽上，
    /// 撈回來只是讓每一次停留多付流量；要寫進指令碼的那一份仍由第四層的
    /// <see cref="ExtendedProperties"/> 整批取回。
    /// </remarks>
    public const string ObjectDescription = @"
SELECT CONVERT(nvarchar(max), ep.value) AS object_description
FROM sys.extended_properties AS ep
WHERE ep.class = 1
  AND ep.major_id = @objectId
  AND ep.minor_id = 0
  AND ep.name = 'MS_Description';";

    /// <summary>
    /// 第四層：單一資料表的索引。
    /// </summary>
    /// <remarks>
    /// 一個索引有幾個欄位就有幾列，合併交給 <see cref="SqlIndexInfo.FromRows"/>。
    /// <c>type = 0</c> 是堆積，沒有索引名稱也沒有意義，直接排除。
    /// 排序把索引鍵欄位排在 INCLUDE 欄位前面：INCLUDE 欄位的 key_ordinal 是 0，
    /// 只依 key_ordinal 排會讓它們跑到最前面。
    ///
    /// <c>STATISTICS_NORECOMPUTE</c> 的來源是 <c>sys.stats.no_recompute</c>，
    /// 不在 <c>sys.indexes</c> 上——每個索引有一份同號的統計資料，鍵是
    /// <c>stats_id = index_id</c>。<c>DATA_COMPRESSION</c> 則在
    /// <c>sys.partitions</c>，只取第一個分割：其餘分割各自可以不同，
    /// 而那要寫成 <c>ON PARTITIONS (…)</c>，不是這一層能表達的。
    ///
    /// 三個 JOIN 都限制成最多一列（<c>stats_id</c>、<c>partition_number = 1</c>、
    /// <c>partition_ordinal = 1</c>），不會讓每個資料行多出幾列。
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
    ic.is_included_column,
    i.fill_factor,
    i.is_padded,
    i.ignore_dup_key,
    i.allow_row_locks,
    i.allow_page_locks,
    i.is_disabled,
    st.no_recompute,
    p.data_compression_desc,
    ds.name AS data_space_name,
    ds.type AS data_space_type,
    pc.name AS partition_column_name
FROM sys.indexes AS i
INNER JOIN sys.index_columns AS ic
    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns AS c
    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
LEFT JOIN sys.stats AS st
    ON st.object_id = i.object_id AND st.stats_id = i.index_id
LEFT JOIN sys.partitions AS p
    ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
LEFT JOIN sys.data_spaces AS ds
    ON ds.data_space_id = i.data_space_id
LEFT JOIN sys.index_columns AS pic
    ON pic.object_id = i.object_id AND pic.index_id = i.index_id AND pic.partition_ordinal = 1
LEFT JOIN sys.columns AS pc
    ON pc.object_id = pic.object_id AND pc.column_id = pic.column_id
WHERE i.object_id = @objectId
  AND i.type <> 0
  AND i.name IS NOT NULL
ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;";

    /// <summary>
    /// 第四層：資料表本身的儲存位置與建立時的 SET 選項。
    /// </summary>
    /// <remarks>
    /// 資料表沒有自己的 <c>data_space_id</c>，那個值在它的叢集索引或堆積上
    /// （<c>index_id</c> 0 或 1）——所以要繞回 <c>sys.indexes</c>。
    ///
    /// <c>lob_data_space_id</c> 只有真的有 LOB 資料行時才不是 NULL，
    /// 而它正是 <c>TEXTIMAGE_ON</c> 的來源。判斷「這張表要不要寫
    /// <c>TEXTIMAGE_ON</c>」用它，不要自己掃資料行的型別：<c>xml</c>、CLR 型別
    /// 與 <c>varchar(max)</c> 都算，漏一種就是一份與來源不同的資料表。
    ///
    /// 兩個 <c>SET</c> 反推建立當時的值，而不是一律寫 <c>ON</c>：計算資料行、
    /// 篩選索引與索引檢視對它們的值有要求，一張在 <c>OFF</c> 之下建起來的資料表，
    /// 用 <c>ON</c> 重建可能直接失敗。
    ///
    /// <c>ANSI_NULLS</c> 在 <c>sys.tables.uses_ansi_nulls</c>；<c>QUOTED_IDENTIFIER</c>
    /// <b>不在任何一個目錄檢視上</b>，只問得到 <c>OBJECTPROPERTY</c>——
    /// <c>sys.tables</c> 沒有 <c>uses_quoted_identifier</c> 這一欄（那是
    /// <c>sys.sql_modules</c> 的欄位）。直接 SELECT 它會讓整條查詢變成
    /// <c>Invalid column name</c>，而那是 <c>DbException</c>，會被降級吃掉，
    /// 與 <c>COLUMNPROPERTY(…, 'GeneratedAlwaysType')</c> 是同一條規則。
    ///
    /// <c>CONVERT(bit, …)</c> 不能省：<c>OBJECTPROPERTY</c> 回傳 <c>int</c>，
    /// 讀取端要的是 <c>GetBoolean</c>，型別不合會丟 <c>InvalidCastException</c>——
    /// 那不是 <c>DbException</c>，接不住。跨連結伺服器時它在對方登入的預設資料庫裡
    /// 找 object_id，多半得到 NULL，讀取端的 fallback 是 <c>true</c>，
    /// 與 <c>COLUMNPROPERTY</c> 那幾條的降級一致。
    /// </remarks>
    public const string TableStorage = @"
SELECT
    ds.name AS filegroup_name,
    ds.type AS filegroup_type,
    lob.name AS lob_filegroup_name,
    t.uses_ansi_nulls,
    pc.name AS partition_column_name,
    CONVERT(bit, OBJECTPROPERTY(t.object_id, 'IsQuotedIdentOn')) AS uses_quoted_identifier
FROM sys.tables AS t
LEFT JOIN sys.indexes AS i
    ON i.object_id = t.object_id AND i.index_id IN (0, 1)
LEFT JOIN sys.data_spaces AS ds
    ON ds.data_space_id = i.data_space_id
LEFT JOIN sys.data_spaces AS lob
    ON lob.data_space_id = t.lob_data_space_id
LEFT JOIN sys.index_columns AS pic
    ON pic.object_id = i.object_id AND pic.index_id = i.index_id AND pic.partition_ordinal = 1
LEFT JOIN sys.columns AS pc
    ON pc.object_id = pic.object_id AND pc.column_id = pic.column_id
WHERE t.object_id = @objectId;";

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

    /// <summary>
    /// 第四層：掛在單一資料表上的觸發程序。
    /// </summary>
    /// <remarks>
    /// 定義走 <c>sys.sql_modules</c> 的 <c>LEFT JOIN</c> 而不是 <c>OBJECT_DEFINITION</c>：
    /// 那是本機函式，加不了限定字，跨到連結伺服器時會在對方登入的預設資料庫裡
    /// 找 object_id——與模組定義那一條同一個理由。加密的觸發程序那一欄是 NULL，
    /// 讀取端據此整個跳過。
    ///
    /// <c>parent_class = 1</c> 只收掛在物件上的那些；掛在資料庫或伺服器上的
    /// DDL 觸發程序不屬於任何一張資料表。
    /// </remarks>
    public const string Triggers = @"
SELECT
    tr.name AS trigger_name,
    m.definition,
    tr.is_disabled
FROM sys.triggers AS tr
LEFT JOIN sys.sql_modules AS m ON m.object_id = tr.object_id
WHERE tr.parent_id = @objectId
  AND tr.parent_class = 1
  AND tr.is_ms_shipped = 0
ORDER BY tr.name;";

    /// <summary>
    /// 第四層：單一資料表上的 <c>CHECK</c> 條件約束。
    /// </summary>
    /// <remarks>
    /// <c>is_disabled</c> 一定要取。停用的條件約束在重建出來的資料表上如果變成
    /// 啟用的，那張表會開始擋掉來源允許的資料——而那是在資料匯入到一半才發現的
    /// 那種差異。
    ///
    /// <c>parent_column_id</c> 是 0 時代表寫在資料表層級而不是某個資料行上；
    /// <c>LEFT JOIN</c> 因此接不到列，資料行名稱是 NULL，正好是要的結果。
    /// </remarks>
    public const string CheckConstraints = @"
SELECT
    cc.name AS constraint_name,
    cc.definition,
    cc.is_disabled,
    cc.is_not_for_replication,
    cc.is_system_named,
    c.name AS column_name
FROM sys.check_constraints AS cc
LEFT JOIN sys.columns AS c
    ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
WHERE cc.parent_object_id = @objectId
ORDER BY cc.name;";

    /// <summary>
    /// 第四層：單一資料表上的擴充屬性，含資料行、索引與條件約束三層。
    /// </summary>
    /// <remarks>
    /// <c>value</c> 在伺服器端就 <c>CONVERT</c> 成字串：那一欄是 <c>sql_variant</c>，
    /// 用 <c>GetValue</c> 收到的是裝箱的原生型別，而下游要的一律是寫進指令碼的那串字。
    /// 與序列的界限值同一個理由。
    ///
    /// 三段 <c>UNION</c> 對應三種掛法，而它們的 <c>major_id</c> 根本不是同一個東西：
    /// 資料表與資料行掛在資料表自己身上（<c>class = 1</c>，<c>minor_id</c> 是
    /// <c>column_id</c>，0 代表資料表本身）；索引是 <c>class = 7</c>，
    /// <c>minor_id</c> 是 <c>index_id</c>；條件約束則掛在<b>條件約束自己</b>的
    /// object_id 上，所以要從 <c>parent_object_id</c> 回頭找。少掉第三段的症狀是
    /// 條件約束上的說明安靜地消失。
    ///
    /// <c>level</c> 這一欄是本查詢自己編的號，不是目錄檢視上的欄位；排序也照它走，
    /// 讓資料表的說明排在資料行前面——與 SSMS 的輸出順序一致。
    /// </remarks>
    public const string ExtendedProperties = @"
SELECT level, property_name, property_value, minor_id, target_name
FROM (
    SELECT
        CONVERT(int, CASE WHEN ep.minor_id = 0 THEN 0 ELSE 1 END) AS level,
        ep.name AS property_name,
        CONVERT(nvarchar(max), ep.value) AS property_value,
        ep.minor_id,
        c.name AS target_name
    FROM sys.extended_properties AS ep
    LEFT JOIN sys.columns AS c
        ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
    WHERE ep.class = 1 AND ep.major_id = @objectId
    UNION ALL
    SELECT
        2 AS level,
        ep.name AS property_name,
        CONVERT(nvarchar(max), ep.value) AS property_value,
        ep.minor_id,
        i.name AS target_name
    FROM sys.extended_properties AS ep
    INNER JOIN sys.indexes AS i
        ON i.object_id = ep.major_id AND i.index_id = ep.minor_id
    WHERE ep.class = 7 AND ep.major_id = @objectId
    UNION ALL
    SELECT
        3 AS level,
        ep.name AS property_name,
        CONVERT(nvarchar(max), ep.value) AS property_value,
        0 AS minor_id,
        o.name AS target_name
    FROM sys.extended_properties AS ep
    INNER JOIN sys.objects AS o
        ON o.object_id = ep.major_id
    WHERE ep.class = 1
      AND ep.minor_id = 0
      AND o.parent_object_id = @objectId
      AND o.type IN ('C', 'D', 'F', 'PK', 'UQ')
) AS properties
ORDER BY level, minor_id, target_name, property_name;";

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
