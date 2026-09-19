namespace SqlAssist.Metadata.Search;

/// <summary>
/// 搜尋索引專用的查詢，分成兩段：識別欄位一段、定義本文一段。
/// </summary>
/// <remarks>
/// <b>兩段分開是這一層最重要的一件事。</b>定義本文是第一次搜尋最貴的一段——真實資料庫裡
/// 單一個模組動輒數 MB，整個資料庫加起來沒有上界。使用者這一輪只想找名稱時
/// （<see cref="Core.Search.SearchTargets"/> 不含 <c>Text</c>），第二段連送都不送，
/// 也不佔記憶體。併成一條 <c>LEFT JOIN sys.sql_modules</c> 的話省不掉——伺服器仍然要讀，
/// 網路仍然要傳，而那一份會在讀取端被丟掉。
///
/// 刻意不重用 <see cref="Querying.SqlMetadataQueries.Objects"/>：那一條是按需載入的第一層，
/// 種類清單也不一樣（這裡多了條件約束）。結構描述清單則相反，與第一層要的完全一樣，走
/// <see cref="Querying.SqlMetadataQueries.Schemas"/>；抄第二份的症狀是其中一份忘了排除
/// <c>sys</c>／<c>INFORMATION_SCHEMA</c>，而兩邊看到的結構描述開始不一樣。
///
/// 查詢一律寫成不加限定的 <c>sys.</c>：決定查哪一個資料庫的是連線，不是 SQL，
/// 跨資料庫只要換 <see cref="Querying.SqlDatabaseScopedConnectionSource"/>。
/// 也刻意不用 <c>OBJECT_DEFINITION</c> 這一族本機函式——它們加不了限定字，
/// 跨連結伺服器時會在對方登入的預設資料庫裡解析，而畫面上看不出退過。
/// </remarks>
public static class SqlCatalogSearchQueries
{
    /// <summary>
    /// 增量重新整理的界線；<c>NULL</c> 表示整份重撈。
    /// </summary>
    /// <remarks>
    /// 比較寫成 <c>&gt;=</c> 而不是 <c>&gt;</c>：<c>modify_date</c> 是 <c>datetime</c>，
    /// 解析度 3.33 毫秒。同一個刻度裡改了兩個物件時，<c>&gt;</c> 會把上一輪已經看過的那個
    /// 刻度整個排除，而另一個的變更就此消失。<c>&gt;=</c> 只多撈回上一輪最後那幾個物件，
    /// 代價是幾列，換到的是「重新整理之後不會少東西」。
    ///
    /// 寫成 <c>@modifiedAfter IS NULL OR …</c> 而不是另備一份沒有 <c>WHERE</c> 的查詢：
    /// 兩份幾乎一樣的 SQL 一定會有一份忘記跟著改，而那一份的症狀是重新整理之後
    /// 少了某一種物件的定義本文。目錄檢視本來就是掃描，這個條件不會改變存取計畫的量級。
    ///
    /// 只有 <see cref="Definitions"/> 與 <see cref="Columns"/> 吃它。<see cref="Objects"/>
    /// <b>一律整份重撈</b>：它是唯一看得出「哪一個物件被卸除了」的一條，而卸除不會留下
    /// 任何時間戳。那一條也是三條裡最便宜的——只有識別欄位，沒有本文。
    /// </remarks>
    public const string ModifiedAfterParameterName = "@modifiedAfter";

    /// <summary>
    /// 第一段：物件、種類與版本戳；不含定義本文。
    /// </summary>
    /// <remarks>
    /// 欄位順序：object_id、schema_name、object_name、type、modify_date。
    /// 前四欄與 <see cref="Querying.SqlMetadataReader.ReadObject"/> 一致，讓物件那一段
    /// 直接共用既有的對應，不再寫第二份「哪一欄是什麼」。
    ///
    /// 同義字與資料表型別照第一層的作法 UNION 進來並貼上 <c>SN</c>、<c>TT</c>。
    /// 資料表型別沒有自己的 <c>modify_date</c>，繞回 <c>sys.objects</c> 取；
    /// 接不到列時是 NULL，讀取端當成「這一列沒有戳」——而沒有戳的物件在增量重新整理時
    /// 一律算成變更過，不會因為問不到時間就被當成沒變。
    ///
    /// 條件約束四種各走自己的目錄檢視而不是把 <c>C</c>／<c>D</c>／<c>PK</c>／<c>UQ</c>／<c>F</c>
    /// 塞進上面那個 <c>IN</c> 清單：<c>sys.objects</c> 上的條件約束<b>沒有</b>自己的
    /// <c>schema_id</c>（它跟著父物件走），照上面那條 JOIN 會接到錯的結構描述，
    /// 而畫面上那個結構描述看起來完全正常。四種一次 UNION 回來，不是四次來回。
    ///
    /// 這一條<b>沒有</b>增量條件，理由見 <see cref="ModifiedAfterParameterName"/>。
    /// </remarks>
    public const string Objects = @"
SELECT
    o.object_id,
    s.name AS schema_name,
    o.name AS object_name,
    o.type,
    o.modify_date
FROM sys.objects AS o
INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'TR', 'TA', 'SO')
UNION ALL
SELECT
    sn.object_id,
    s.name AS schema_name,
    sn.name AS object_name,
    'SN' AS type,
    sn.modify_date
FROM sys.synonyms AS sn
INNER JOIN sys.schemas AS s ON s.schema_id = sn.schema_id
WHERE sn.is_ms_shipped = 0
UNION ALL
SELECT
    tt.type_table_object_id AS object_id,
    s.name AS schema_name,
    tt.name AS object_name,
    'TT' AS type,
    o.modify_date
FROM sys.table_types AS tt
INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
LEFT JOIN sys.objects AS o ON o.object_id = tt.type_table_object_id
WHERE tt.is_user_defined = 1
UNION ALL
SELECT
    cc.object_id,
    s.name AS schema_name,
    cc.name AS object_name,
    cc.type,
    cc.modify_date
FROM sys.check_constraints AS cc
INNER JOIN sys.objects AS p ON p.object_id = cc.parent_object_id
INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
WHERE cc.is_ms_shipped = 0
UNION ALL
SELECT
    dc.object_id,
    s.name AS schema_name,
    dc.name AS object_name,
    dc.type,
    dc.modify_date
FROM sys.default_constraints AS dc
INNER JOIN sys.objects AS p ON p.object_id = dc.parent_object_id
INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
WHERE dc.is_ms_shipped = 0
UNION ALL
SELECT
    kc.object_id,
    s.name AS schema_name,
    kc.name AS object_name,
    kc.type,
    kc.modify_date
FROM sys.key_constraints AS kc
INNER JOIN sys.objects AS p ON p.object_id = kc.parent_object_id
INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
WHERE kc.is_ms_shipped = 0
UNION ALL
SELECT
    fk.object_id,
    s.name AS schema_name,
    fk.name AS object_name,
    fk.type,
    fk.modify_date
FROM sys.foreign_keys AS fk
INNER JOIN sys.objects AS p ON p.object_id = fk.parent_object_id
INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
WHERE fk.is_ms_shipped = 0;";

    /// <summary>
    /// 第二段：定義本文。
    /// </summary>
    /// <remarks>
    /// 欄位順序：object_id、definition。
    ///
    /// <c>CHECK</c> 與 <c>DEFAULT</c> 的運算式一起 UNION 進來：那正是條件約束身上唯一
    /// 值得搜的文字（「哪一條規則提到 <c>PUBL_CODE</c>」），而它們不在
    /// <c>sys.sql_modules</c> 裡。主索引鍵與外來鍵沒有運算式，所以只有名稱命中。
    ///
    /// 同義字與序列一律沒有本文：<c>OBJECT_DEFINITION</c> 對它們回傳 NULL，
    /// 而把目錄檢視的欄位組回 <c>CREATE SYNONYM</c>／<c>CREATE SEQUENCE</c> 是
    /// <see cref="Formatting.SqlCatalogScript"/> 的事，它要的是第三層的資料——
    /// 為全量索引再撈那兩份等於多兩輪掃全表。代價是同義字指向的目標名稱搜不到，
    /// 名稱命中不受影響。
    /// </remarks>
    public const string Definitions = @"
SELECT
    m.object_id,
    m.definition
FROM sys.sql_modules AS m
INNER JOIN sys.objects AS o ON o.object_id = m.object_id
WHERE o.is_ms_shipped = 0
  AND (@modifiedAfter IS NULL OR o.modify_date >= @modifiedAfter)
UNION ALL
SELECT
    cc.object_id,
    cc.definition
FROM sys.check_constraints AS cc
WHERE cc.is_ms_shipped = 0
  AND (@modifiedAfter IS NULL OR cc.modify_date >= @modifiedAfter)
UNION ALL
SELECT
    dc.object_id,
    dc.definition
FROM sys.default_constraints AS dc
WHERE dc.is_ms_shipped = 0
  AND (@modifiedAfter IS NULL OR dc.modify_date >= @modifiedAfter);";

    /// <summary>
    /// 整個資料庫的資料行名稱。
    /// </summary>
    /// <remarks>
    /// 欄位順序：object_id、column_name。只取這兩欄——搜尋比對的是名字，
    /// 型別、可否為 NULL 那些是第二層在使用者選了某一個物件之後才要的東西，
    /// 全量撈回來等於把第二層的成本乘上整個資料庫。
    ///
    /// JOIN 回 <c>sys.objects</c> 是為了增量：加一個資料行會動到那張表的
    /// <c>modify_date</c>，所以「只撈變更過的物件」對資料行同樣成立。順帶把系統內部物件
    /// 的資料行擋在伺服器端，而那幾列本來就會在讀取端被丟掉。
    ///
    /// <c>ORDER BY</c> 走的正是 <c>sys.columns</c> 的叢集鍵，換到的是「同一個資料庫每次
    /// 跑出來的順序一樣」——少了它，同分的結果每一輪的先後由伺服器決定，清單會自己跳。
    /// </remarks>
    public const string Columns = @"
SELECT
    c.object_id,
    c.name AS column_name
FROM sys.columns AS c
INNER JOIN sys.objects AS o ON o.object_id = c.object_id
WHERE o.is_ms_shipped = 0
  AND (@modifiedAfter IS NULL OR o.modify_date >= @modifiedAfter)
ORDER BY c.object_id, c.column_id;";

    /// <summary>
    /// 這條連線看得到、而且進得去的資料庫。
    /// </summary>
    /// <remarks>
    /// 欄位順序：name、is_system。
    ///
    /// 給資料庫多選用的<b>清單</b>，不是預先索引的名單：每指名一個就是一次全表掃描，
    /// 而「把每一個進得去的資料庫都索引一遍」是明文禁止的。這一條只回答
    /// 「有哪些可以選」，建索引仍然只發生在使用者真的勾了之後。
    ///
    /// <c>state = 0</c> 是 ONLINE。離線、還原中與緊急模式的資料庫列出來只會讓使用者
    /// 勾一個必然失敗的目標，而失敗在畫面上與「這裡面沒有東西」長得一樣。
    /// <c>HAS_DBACCESS</c> 同理：<c>sys.databases</c> 看得到不等於進得去。
    /// 這一族函式吃的是資料庫名稱而不是 object_id，不在「加不了限定字」那個坑裡。
    ///
    /// <c>database_id &lt;= 4</c> 就是 master／tempdb／model／msdb 四個；讓 UI 決定
    /// 要不要預設收起來，而不是在這裡直接不回傳——使用者確實會去搜 msdb 的作業指令碼。
    /// </remarks>
    public const string Databases = @"
SELECT
    d.name,
    CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END AS is_system
FROM sys.databases AS d
WHERE d.state = 0
  AND HAS_DBACCESS(d.name) = 1
ORDER BY d.name;";
}
