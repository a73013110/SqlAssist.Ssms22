#Requires -Version 7.0
<#
.SYNOPSIS
    以 ScriptDom 產生 T-SQL 關鍵字目錄（SqlKeywordCatalog.Generated.cs）。

.DESCRIPTION
    關鍵字清單刻意不手寫，改由 Microsoft 自己的剖析器推導，換版本重跑即可更新。
    三個階段都會自我驗證，不猜任何一個字：

    一、取字面值
        列舉 TSqlTokenType 的成員名稱，大寫後丟回 tokenizer；token 型別對得回原成員
        才採用。標點與字面值（Comma、HexLiteral…）自然對不回來，因此被排除。
        名稱含 camelCase 轉折的再試一次補底線的寫法，撈回 CURRENT_TIMESTAMP、
        IDENTITY_INSERT、TRY_CONVERT 這一類。

    二、判保留字
        「這個字當名字寫，剖析器接不接受」跟「它能出現在哪個位置」是兩回事，
        因此另外探測一次：把字塞進識別字的洞裡（SELECT ? FROM t、FROM ?、
        CREATE TABLE t (? int)…），被拒的就是插入時一定要加方括號的保留字。
        目錄裡有 13 個字是非保留字（APPLY、OUTPUT、ROWS、GO…），當欄位名寫
        完全合法，靠這一階段才不會被多加一層括號。

    三、定位置
        把關鍵字塞進樣板的洞裡剖析，依錯誤碼判定它在該位置合不合法：
            46005  必須是 X 卻發現 Y     → 不合法
            46010  語法不正確            → 不合法
            46014  只可存在於資料行層級  → 不合法
            46001  剖析器內部錯誤        → 不合法
            46029  出現未預期的檔案結尾  → 合法，只是語句還沒寫完
        單一續尾會誤判——BACKUP 之後是檔案結尾、SELECT 之後卻是語法錯誤，兩者都合法。
        因此每個位置試一組續尾取聯集：任一組能過就算合法。
        非保留字另有一條：同一組續尾換成普通名稱也過的話，那一次只證明它能當名字，
        不算它屬於這個位置。

    需要手寫的只有 $ContextTemplates 的樣板，每個關鍵字的分類全部由剖析器決定。
    每個樣板必須是分析器判得出、而且回報含該位置的文字——兩邊說的是同一個位置；
    樣板表隨產物輸出，Core 的測試逐條回驗。樣板都進不去的字產出為 None，
    執行期只在分析器也判不出位置時出現。

    四、子句片語
        SET 選項、ALTER INDEX 的動作、FOR XML 的模式這些字在文法上不是關鍵字
        （ScriptDom 把它們掃成識別字），前三個階段撈不到。這一階段換一個問法：
        每個片語是一段「游標前面的尾巴」（SET STATISTICS、ALTER INDEX {name} ON {name}），
        候選字取 ScriptDom 內部 CodeGenerationSupporter 的所有字串常數加上關鍵字清單，
        以與第三階段相同的規則（普通名稱過不了而它過得了）決定哪些字接得上。
        普通名稱在每一組續尾都過不了的片語是「封閉」的：那裡除了這幾個字沒有別的東西是對的。
        片語也記下它前面那一格的位置（After），探測借用第三階段的樣板，執行期以同一個位置
        分析回驗：一句開頭的 SET 與 UPDATE t SET 的 SET 是同一條尾巴、不同的意思。
        手寫的只有片語的尾巴；片語表與探測文字一併輸出，Core 的測試逐條回驗。

    五、寫完一項的字
        NULL、CURRENT_USER、DESC 這種字本身就把前一格開的那一項寫完，之後的位置與
        識別字之後相同。判法：任一個樣板接上它就是完整的一句，而且語法樹裡以它結尾的
        是語句以外的片段（運算式、排序項、提示）——BEGIN TRAN 的 TRAN 寫完的是語句本身。

    六、寫完一句的字
        第五階段排除的那一半：以它結尾的是語句本身（BREAK、COMMIT、TRAN）。另外記下前一格
        在哪些位置時那一句再也接不了語句開頭以外的東西——COMMIT 還接 TRAN，就不算。

.PARAMETER SsmsInstallDir
    SSMS 22 安裝路徑。ScriptDom 隨 SSMS 附帶，不必另外安裝。

.PARAMETER OutputPath
    產出的 .cs 檔路徑。

.PARAMETER CachePath
    剖析結果快取的路徑，預設在不進版控的 artifacts/cache/。沒有快取的第一次約二十多分鐘，
    之後只剖析新出現的文字；ScriptDom 換版本時整份自動作廢。跑的途中每兩分鐘存一次，
    中途失敗也會存，中斷或失敗之後重跑從存下的地方接著算。

.PARAMETER NoCache
    不讀舊快取、全部重新剖析，結果照樣寫回快取。懷疑快取與剖析器不一致時用。

.NOTES
    產物要進版控。SqlAssist.Core 是 netstandard2.0 且刻意零相依，建置時不會、
    也不該去碰 SSMS 的組件，所以這支腳本是手動執行、結果 commit 進去。
#>
[CmdletBinding()]
param(
    [string]$SsmsInstallDir,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\SqlAssist.Core\Keywords\SqlKeywordCatalog.Generated.cs'),
    [string]$CachePath = (Join-Path $PSScriptRoot '..\artifacts\cache\Generate-Keywords.cache'),
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$SsmsInstallDir = Get-SsmsInstallPath -InstallDir $SsmsInstallDir
$scriptDomPath = Join-Path $SsmsInstallDir 'Common7\IDE\Extensions\Application\Microsoft.SqlServer.TransactSql.ScriptDom.dll'

if (-not (Test-Path $scriptDomPath)) {
    throw "找不到 ScriptDom：$scriptDomPath。請以 -SsmsInstallDir 指定 SSMS 22 的安裝路徑。"
}

$assembly = [System.Reflection.Assembly]::LoadFrom($scriptDomPath)
$scriptDomVersion = (Get-Item $scriptDomPath).VersionInfo.FileVersion

# 取得到得了的最新剖析器。SSMS 22 是 TSql170Parser（SQL Server 2025 相容層級）。
$parserType = @('TSql170Parser', 'TSql160Parser', 'TSql150Parser') |
    ForEach-Object { $assembly.GetType("Microsoft.SqlServer.TransactSql.ScriptDom.$_") } |
    Where-Object { $_ } |
    Select-Object -First 1

if (-not $parserType) {
    throw 'ScriptDom 裡找不到可用的 TSqlNNNParser 型別。'
}

# 建構參數是 initialQuotedIdentifiers。
$parser = [Activator]::CreateInstance($parserType, @($true))
$tokenTypeEnum = $assembly.GetType('Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType')

Write-Host "ScriptDom $scriptDomVersion（$($parserType.Name)）"

# ---------------------------------------------------------------- 一、取字面值

function Test-RoundTrip {
    param([string]$Text, [string]$ExpectedTokenType)

    $reader = [System.IO.StringReader]::new($Text)
    $errors = $null
    $tokens = $parser.GetTokenStream($reader, [ref]$errors)

    return $tokens.Count -ge 1 -and "$($tokens[0].TokenType)" -eq $ExpectedTokenType
}

$keywords = [System.Collections.Generic.List[string]]::new()

foreach ($name in [Enum]::GetNames($tokenTypeEnum)) {
    $upper = $name.ToUpperInvariant()

    if (Test-RoundTrip -Text $upper -ExpectedTokenType $name) {
        $keywords.Add($upper)
        continue
    }

    # CurrentTimestamp → CURRENT_TIMESTAMP
    $underscored = [regex]::Replace($name, '(?<!^)([A-Z])', '_$1').ToUpperInvariant()

    if ($underscored -ne $upper -and (Test-RoundTrip -Text $underscored -ExpectedTokenType $name)) {
        $keywords.Add($underscored)
    }
}

$lexerCount = ($keywords | Sort-Object -Unique).Count

# 非保留字的補充清單。
#
# 這是整支腳本唯一「條列」出來的東西，而且是不得已的：非保留字在文法上本來就不是
# 關鍵字，THROW 與 APPLY 對詞法器來說跟 Lib_Reader 沒有兩樣，因此 ScriptDom 的
# TSqlTokenType 沒有它們、SqlParser 的 Scanner 也一律回報識別字。任何工具在這一塊
# 都只能自己維護清單。
#
# 內容刻意等於「舊的手寫清單裡有、但 ScriptDom 認不得」的那些字——換掉手寫清單
# 不能是退步。要新增非保留字就加在這裡，位置一樣由下面的探測自動決定。
$NonReservedSupplement = @(
    'APPLY', 'CATCH', 'NEXT', 'NOLOCK', 'OFFSET', 'OUTPUT',
    'PARTITION', 'ROWS', 'THROW', 'TRY', 'USING'
)

foreach ($supplement in $NonReservedSupplement) {
    if ($keywords -contains $supplement) {
        # 這個字已經升格成保留字了，補充清單該把它拿掉，否則會一直是死條目。
        Write-Warning "補充清單裡的 $supplement 已經是保留字，可以移除。"
        continue
    }

    $keywords.Add($supplement)
}

$keywords = $keywords | Sort-Object -Unique
Write-Host "字面值：$($keywords.Count) 個關鍵字（詞法器認得的 $lexerCount + 非保留字補充 $($NonReservedSupplement.Count)）"

# ---------------------------------------------------------------- 二、判保留字

# 插入識別字時要不要加方括號，問的是「這個字當名字寫，剖析器吃不吃」，
# 跟位置分類是兩回事：OUTPUT 在文法上是關鍵字，但 SELECT Output FROM t
# 完全合法；反過來 ORDER 當欄位名寫就是語法錯誤。所以另外探測一次。
# 排在定位置之前，因為定位置要知道哪些字可以被當成名字吃下去。
#
# 洞在樣板的中間而不是結尾，因此這裡是前後綴成對。
$IdentifierTemplates = @(
    @{ Prefix = 'SELECT ';           Suffix = ' FROM t' }
    @{ Prefix = 'SELECT * FROM ';    Suffix = '' }
    @{ Prefix = 'SELECT * FROM ';    Suffix = '.t' }
    @{ Prefix = 'SELECT t.';         Suffix = ' FROM t' }
    @{ Prefix = 'CREATE TABLE t ('; Suffix = ' int)' }
)

# 保留字的補充清單，跟 $NonReservedSupplement 是同一個問題的另一面：
# IDENTITYCOL 與 ROWGUIDCOL 不在 TSqlTokenType 裡（詞法器把它們掃成識別字），
# 但剖析器不接受它們當名字，不加括號插進去就壞掉。它們不進關鍵字清單——
# 建議清單與自動大寫不該因為這個修正而多出兩個字——只影響括號判定。
#
# 下面的探測會回驗這份清單：真的不需要括號就會警告，不會變成死條目。
$IdentifierReservedSupplement = @('IDENTITYCOL', 'ROWGUIDCOL')

function Test-IdentifierRejected {
    param([string]$Name)

    foreach ($template in $IdentifierTemplates) {
        $limit = $template.Prefix.Length + $Name.Length
        $reader = [System.IO.StringReader]::new($template.Prefix + $Name + $template.Suffix)
        $errors = $null
        $null = $parser.Parse($reader, [ref]$errors)

        foreach ($error in $errors) {
            # 樣板本身是完整語句，名字之前（含名字）出現任何錯誤都只可能是它造成的。
            if ($error.Offset -le $limit) {
                return $true
            }
        }
    }

    return $false
}

$reserved = [System.Collections.Generic.List[string]]::new()

foreach ($keyword in $keywords) {
    if (Test-IdentifierRejected -Name $keyword) {
        $reserved.Add($keyword)
    }
}

$nonReserved = $keywords | Where-Object { $reserved -notcontains $_ }

foreach ($supplement in $IdentifierReservedSupplement) {
    if ($reserved -contains $supplement) {
        Write-Warning "補充清單裡的 $supplement 已經在關鍵字清單裡，可以移除。"
        continue
    }

    if (-not (Test-IdentifierRejected -Name $supplement)) {
        # 剖析器接受它當名字，加了括號只是多餘。
        Write-Warning "補充清單裡的 $supplement 不需要方括號，可以移除。"
        continue
    }

    $reserved.Add($supplement)
}

$reserved = $reserved | Sort-Object -Unique
Write-Host "保留字：$($reserved.Count) 個必須加方括號；非保留字 $(@($nonReserved).Count) 個可以直接寫：$($nonReserved -join ', ')"

# ------------------------------------------------------------------ 三、定位置

# 唯一手寫的部分：每個位置一個樣板，"洞" 就是樣板的結尾。
# 名稱必須與 SqlKeywordPosition 的成員一致。
#
# 樣板一律切在「游標前一個詞元」的後面，因為那正是執行期的分析器認得的東西。
# 樣板本身也要是合法的 T-SQL 片段：WHERE a 之後接 AND 在 T-SQL 裡是錯的
# （a 不是布林運算式），所以 ExpressionTail 的樣板必須寫成 WHERE a = 1。
$ContextTemplates = [ordered]@{
    # 批次的第一句可以省略 EXEC，普通名稱在那裡也合法；分號之後才分得出 THROW 是關鍵字。
    StatementStart   = @('', 'SELECT 1; ')
    SelectList       = @('SELECT ')

    # TOP 子句寫完之後，分析器同時回報這一格與 SelectList；PERCENT、WITH TIES 只在這裡。
    # TOP (10) 得到的字與 TOP 10 相同，不必另列。
    TopClauseTail    = @('SELECT TOP 10 ')
    SelectListTail   = @('SELECT a ')
    DataSource       = @('SELECT * FROM ')

    # 分析器只知道「前一個詞元是識別字」，分不出那個識別字是資料表、
    # 聯結對象還是授權目標。目錄跟著這個粒度走，不假裝分得出來。
    TableSourceTail  = @(
        'SELECT * FROM t ', 'SELECT * FROM t JOIN y ',
        'INSERT INTO t ', 'MERGE INTO t ')

    # 同一個子句錨點在別的敘述裡寫完之後接的字不同：SELECT … INTO 的新資料表之後接 FROM，
    # FETCH 的游標之後接 INTO，UPDATE 的 SET 指派之後接 FROM、WHERE、OUTPUT。
    SelectIntoTail   = @('SELECT a INTO t ')
    FetchTail        = @('FETCH NEXT FROM c ')
    UpdateSetTail    = @('UPDATE t SET a = 1 ')

    # 索引鍵清單裡的資料行之後：ASC、DESC。CREATE INDEX 的 WITH ( 之後是選項，多半不是關鍵字，由子句片語給。
    IndexKeyTail     = @('CREATE INDEX i ON t (a ')
    IndexOption      = @('CREATE INDEX i ON t (a) WITH (', 'CREATE INDEX i ON t (a) WITH (ONLINE = ON, ')

    # GRANT／DENY／REVOKE：權限寫完之後是 ON、TO，REVOKE 還有 FROM；ON 之後是類別（SCHEMA::）或目標，
    # 目標寫完之後是 TO、FROM。
    # 權限名稱是一串識別字（VIEW DEFINITION），GRANT SELECT 之後什麼非保留字都接得上；
    # 樣板以資料行清單收掉權限，探到的才只有權限之後的字。
    PermissionList   = @('GRANT SELECT (a) ', 'REVOKE SELECT (a) ')
    PermissionOn     = @('GRANT SELECT ON ', 'REVOKE SELECT ON ')
    PermissionTarget = @('GRANT SELECT ON t ', 'REVOKE SELECT ON t ')

    # 資料表之後的 TABLESAMPLE (10 是 PERCENT、ROWS；PIVOT 的彙總與 UNPIVOT 的值之後是 FOR，FOR 的資料行之後是 IN。
    TableSampleTail  = @('SELECT * FROM t TABLESAMPLE (10 ')
    PivotClause      = @(
        'SELECT * FROM t PIVOT (COUNT(a) ', 'SELECT * FROM t PIVOT (COUNT(a) FOR b ',
        'SELECT * FROM t UNPIVOT (a ', 'SELECT * FROM t UNPIVOT (a FOR b ')

    # WHERE CURRENT OF 只有 UPDATE 與 DELETE 寫得出來。
    Predicate        = @('SELECT * FROM t WHERE ', 'DELETE FROM t WHERE ')

    # 兩個都要：WHERE a 之後是 IN、IS、LIKE、BETWEEN，
    # WHERE a = 1 之後才是 AND、OR 與後續子句。分析器一樣分不出來。
    # LIKE 的樣式寫完之後同樣回報這個位置，ESCAPE 只在那裡。
    # 第一個是代表寫法，子句片語拿它探測。
    ExpressionTail   = @(
        'SELECT * FROM t WHERE a = 1 ', 'SELECT * FROM t WHERE a ',
        "SELECT * FROM t WHERE a LIKE 'x' ")
    OrderByTail      = @('SELECT * FROM t ORDER BY a ')

    # 查詢 ORDER BY 的 OFFSET 值之後是 ROW、ROWS；視窗 OVER (ORDER BY a 之後是 ASC、DESC 與視窗框架。
    OffsetTail       = @('SELECT * FROM t ORDER BY a OFFSET 10 ')
    WindowOrderTail  = @('SELECT SUM(a) OVER (ORDER BY a ')

    # 函式呼叫之後的 OVER、COLLATE。包在括號裡，選取清單尾端的 FROM、別名這些字就接不上。
    FunctionCallTail = @('SELECT (SUM(a) ')

    # 外部索引鍵的參考寫完之後：ON（DELETE、UPDATE）、NOT（FOR REPLICATION）與其他資料行條件約束。
    ReferencesTail   = @('CREATE TABLE t (a int REFERENCES u (a) ')

    # 模組的 WITH 選項寫完之後是本體的 AS；函式參數清單之後的 RETURNS 不是關鍵字，由子句片語給。
    ModuleHeader     = @('CREATE VIEW v WITH SCHEMABINDING ', 'CREATE PROCEDURE p WITH RECOMPILE ', 'CREATE FUNCTION f () RETURNS int WITH SCHEMABINDING ')
    FunctionReturns  = @('CREATE FUNCTION f () ')

    # WITH RESULT SETS 的兩層括號：外層每一項是一組資料行定義或 AS OBJECT／TYPE／FOR XML，
    # 內層每一項是「名稱 型別 [COLLATE] [NULL | NOT NULL]」，名稱之後的型別走型別清單。
    ResultSetList    = @('EXEC p WITH RESULT SETS (', 'EXEC p WITH RESULT SETS ((a int), ')
    ResultSetColumn  = @('EXEC p WITH RESULT SETS ((', 'EXEC p WITH RESULT SETS ((a int, ')
    ResultSetColumnTail = @('EXEC p WITH RESULT SETS ((a int ', 'EXEC p WITH RESULT SETS ((a varchar(10) COLLATE Latin1_General_CI_AS ')

    # 清單片語（,*）宣告的選項清單：所有敘述共用這一格，選項由片語的標頭分。樣板要有對應的清單片語才判得出來。
    # 每種清單都放：這一格的關鍵字（RESTORE 的 FILE）要從它自己那句撈；選項多半不是關鍵字，由片語給。
    OptionItem       = @(
        'ALTER USER u WITH ', 'ALTER USER u WITH NAME = n, ',
        'EXEC p WITH ', 'EXEC p WITH RECOMPILE, ',
        "RAISERROR ('x', 16, 1) WITH ", "RAISERROR ('x', 16, 1) WITH NOWAIT, ",
        'DBCC CHECKDB WITH ', 'DBCC CHECKDB WITH NO_INFOMSGS, ',
        "BACKUP DATABASE d TO DISK = 'x' WITH ", "BACKUP DATABASE d TO DISK = 'x' WITH COMPRESSION, ",
        "RESTORE DATABASE d FROM DISK = 'x' WITH ", "RESTORE DATABASE d FROM DISK = 'x' WITH REPLACE, ",
        'CREATE TRIGGER tr ON DATABASE FOR ', 'CREATE TRIGGER tr ON DATABASE FOR CREATE_TABLE, ')

    # GROUP BY 的欄位之後：HAVING、ORDER 與 WITH ROLLUP，不接 ASC、DESC。
    GroupByTail      = @('SELECT * FROM t GROUP BY a ')

    # 欄位本身的位置。兩個都要：ORDER BY 接得了 ASC／DESC 以外的運算式關鍵字
    # （CASE、CONVERT、IIF），GROUP BY 接得了 ROLLUP、CUBE、GROUPING SETS。
    OrderByColumn    = @('SELECT * FROM t ORDER BY ', 'SELECT * FROM t GROUP BY ')

    ByAnchor         = @('SELECT * FROM t ORDER ', 'SELECT * FROM t GROUP ')

    # ALTER TABLE 的三個位置。少了它們，這三處一律回 Any，於是整份關鍵字目錄
    # 與所有片段全部進場——而成熟的補全工具在 ADD 之後只給九個字。
    AlterTableAction = @('ALTER TABLE t ')
    AlterTableAdd    = @('ALTER TABLE t ADD ', 'ALTER TABLE t ADD a int, ')
    AlterTableColumn = @('ALTER TABLE t ALTER COLUMN ', 'ALTER TABLE t DROP COLUMN ')
    DdlObject        = @('CREATE ', 'ALTER ', 'DROP ')

    # WHEN 的條件寫到哪裡都是同一個位置：WHEN a 之後是 IN、IS、LIKE、BETWEEN，
    # WHEN a = 1 之後才是 THEN、AND、OR——與 ExpressionTail 的兩條同一個道理，
    # 只是這裡不接 WHERE、GROUP 那些子句。
    CaseArm          = @('SELECT CASE WHEN a ', 'SELECT CASE WHEN a = 1 ', 'SELECT CASE a WHEN 1 ')
    CaseBody         = @('SELECT CASE WHEN a = 1 THEN 1 ')

    # 資料行定義清單的每一項開頭：新資料行名稱，或 CONSTRAINT、PRIMARY KEY 這些字。
    # DECLARE @t TABLE (、RETURNS @t TABLE ( 得到的字與 CREATE TABLE 相同，不必另列。
    # 型別寫完之後（a int |）沒有樣板：分析器在那裡判不出位置，NOT NULL、IDENTITY、
    # REFERENCES 照樣靠 Any 列得出來；放一個分析器回不出的位置只是自欺。
    ColumnDefinition = @('CREATE TABLE t (', 'CREATE TABLE t (a int, ')
    BlockStart       = @('BEGIN ', 'BEGIN TRY SELECT 1 END TRY BEGIN ')

    # 區塊寫完：下一句，以及 IF 的 ELSE、TRY／CATCH 區塊的 END TRY、END CATCH。
    BlockEnd         = @(
        'BEGIN SELECT 1 END ', 'IF 1 = 1 BEGIN SELECT 1 END ', 'BEGIN TRY SELECT 1 END ',
        'BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT 1 END ')

    # IF 的主體只有一句，那一句寫完：下一句，以及 ELSE。主體用寫完就沒有續寫子句的一句，
    # 這一格才只多出 ELSE，不會把 SELECT 之後的 FROM、INTO 也掛上來。
    IfBodyEnd        = @('IF 1 = 1 SET NOCOUNT ON ')

    # 游標的選項不是關鍵字（LOCAL、FAST_FORWARD 是識別字），由子句片語給；這裡只撈得到 FOR。
    CursorOption     = @('DECLARE c CURSOR ', 'DECLARE c CURSOR LOCAL FAST_FORWARD ')

    # 序列的選項（START WITH、INCREMENT BY、NO CYCLE）同樣不是關鍵字，也不以逗號分隔；這裡撈得到 AS、NO。
    SequenceOption   = @('CREATE SEQUENCE t ', 'CREATE SEQUENCE t START WITH 1 ')

    # 下面兩個同一個道理：AFTER、INSTEAD、MATCHED 都不是關鍵字，由子句片語給。
    TriggerHeader    = @('CREATE TRIGGER tr ON t ', 'CREATE TRIGGER tr ON t WITH ENCRYPTION ')
    MergeWhen        = @('MERGE t USING s ON 1 = 1 WHEN ', 'MERGE t USING s ON 1 = 1 WHEN MATCHED THEN DELETE WHEN ')

    # MERGE 的 THEN 之後是動作；ON 條件或一個動作寫完之後是下一個 WHEN、OUTPUT、OPTION。
    MergeAction      = @('MERGE t USING s ON 1 = 1 WHEN MATCHED THEN ', 'MERGE t USING s ON 1 = 1 WHEN NOT MATCHED THEN ')
    MergeClause      = @('MERGE t USING s ON 1 = 1 WHEN MATCHED THEN DELETE ')

    # 模組的 WITH 選項（ENCRYPTION、SCHEMABINDING、RECOMPILE）同樣不是關鍵字，四種模組各自一格。
    # 不寫成清單片語：選項寫完之後要回報標頭的尾端（AS、FOR），EXECUTE AS 這種多字選項也以位置為鍵。
    ProcedureOption  = @('CREATE PROCEDURE p WITH ', 'CREATE PROCEDURE p WITH ENCRYPTION, ')
    FunctionOption   = @('CREATE FUNCTION f () RETURNS int WITH ', 'CREATE FUNCTION f () RETURNS int WITH SCHEMABINDING, ')
    ViewOption       = @('CREATE VIEW v WITH ', 'CREATE VIEW v WITH SCHEMABINDING, ')
    TriggerOption    = @('CREATE TRIGGER tr ON t WITH ', 'CREATE TRIGGER tr ON t WITH ENCRYPTION, ')

    # 觸發程序的事件清單：AFTER|FOR|INSTEAD OF 與逗號之後是事件，事件寫完之後是 AS、WITH APPEND、NOT FOR REPLICATION。
    TriggerEvent     = @('CREATE TRIGGER tr ON t AFTER ', 'CREATE TRIGGER tr ON t INSTEAD OF ', 'CREATE TRIGGER tr ON t AFTER INSERT, ')
    TriggerEventEnd  = @('CREATE TRIGGER tr ON t AFTER INSERT ', 'CREATE TRIGGER tr ON DATABASE FOR CREATE_TABLE ')
    SetTarget        = @('SET ')

    # SET 的選項名稱寫完之後的 ON／OFF。各選項自己的值（隔離等級、STATISTICS IO…）
    # 由第四階段的子句片語逐一探測，這裡只是片語比對不上時（選項清單的逗號之後）的退路。
    SetOptionValue   = @('SET NOCOUNT ', 'SET IDENTITY_INSERT t ')
    InsertTarget     = @('INSERT ')
}

# 洞後面接的東西。單一續尾會誤判，取聯集。
$Continuations = @(
    '', ' x', ' x FROM y', ' * FROM y', ' TABLE x', ' TABLE x (a int)',
    ' x = 1', ' 1', ' 1 END', ' (1)', ' x.y', ' PROC p AS SELECT 1',
    ' x AS SELECT 1', ' DATABASE x', ' VIEW v AS SELECT 1', ' BY x',
    ' JOIN y ON x.a = y.a', ' NULL', ' KEY', ' ON x TO y', ' OFF', ')',

    # ALTER TABLE t ALTER 在剖析器眼中直接是語法錯誤——它要看到 COLUMN 才收。
    # 少了這一條，ALTER 就不會分到 AlterTableAction，而「猜錯位置的代價是使用者
    # 永遠打不出來」。續尾取聯集，多一條只會讓分類更寬鬆。
    ' COLUMN x int',

    # BEGIN DISTRIBUTED 同理：後面不是 TRAN／TRANSACTION 就是語法錯誤。
    ' TRANSACTION',

    # NEXT 是非保留字，要有 NEXT VALUE FOR 才分得出它不是欄位名稱。
    ' VALUE FOR s',

    # 權限 ON 之後的類別（OBJECT、TYPE）是非保留字，要有 :: 才分得出它不是物件名稱。
    '::x TO y'
)

# 46010 = "'X' 附近的語法不正確"。出現在關鍵字結尾之前代表剖析器根本吃不下它。
# 46005 = "必須是 X，但卻發現 Y"。ORDER BY a Lib_Reader 1 報的是這一條而不是 46010，
#         不算進來的話任何名稱都「接受」，非保留字的 OFFSET 就分不出來。
# 46014 = "Default 條件約束只可存在於資料行層級"。剖析器吃得下 CREATE TABLE t (DEFAULT
#         卻另外報這一條，不算進來的話 DEFAULT 會被分到資料行定義的開頭。
# 46001 = "剖析器內部錯誤"。剖析器在報錯的詞元上當掉、之後都沒檢查，與 46010 一樣是它過不去的地方。
#         不算進來的話 WITHIN GROUP (GRAPH 在檔案結尾只報這一條、一個拒收都沒有，GRAPH 就接得上。
#         算在它報的位置而不是整段作廢：換一條續尾剖析器照常走完的話，那個字仍然成立。
# 46029 = "出現未預期的檔案結尾"，代表吃下去了、只是語句沒寫完，那是合法的。
#
# 另有一族訊息說「這個字不是這裡的選項」（{0} is not a WITH option for a procedure.）：
# 選項名稱在文法上是任意識別字，認不認得留到之後才判。不算進來的話任何名稱都是合法的選項，
# CREATE TRIGGER … WITH 之後的 ENCRYPTION 就分不出來。號碼不手寫，從剖析器的訊息資源撈。
$parserMessages = [System.Resources.ResourceManager]::new('Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserResource', $assembly).
    GetResourceSet([System.Globalization.CultureInfo]::InvariantCulture, $true, $true)
$optionRejections = @($parserMessages | Where-Object {
    $_.Key -match '^SQL\d+Message$' -and $_.Value -match "^(\{0\}|Option '\{0\}') is not a .*\b(option|hint|function)\b"
} | ForEach-Object { [int]($_.Key -replace '\D', '') } | Sort-Object)

if ($optionRejections.Count -eq 0) {
    throw '剖析器的訊息資源裡找不到「不是這裡的選項」那一族；資源名稱或文案變了。'
}

Write-Host "選項拒收訊息：$($optionRejections -join ', ')"
$RejectingErrorNumbers = @(46001, 46005, 46010, 46014) + $optionRejections

# 非保留字的對照名稱：不是任何關鍵字的普通識別字。
$PlainName = 'Lib_Reader'

# 封閉那一格收不收變數的對照名稱：SET 之後除了選項還接 @ReaderId = 1。
$PlainVariable = '@ReaderId'

# 剖析一律交給 C#：第三階段與片語要把上千個候選字逐一配上幾十條續尾剖析，PowerShell 單執行緒要一個多小時，
# 平行、並且前綴與續尾各只斷詞一次之後仍要二十多分鐘，所以剖析結果另存成快取，重跑只剖析新的文字。快取只記剖析器說了什麼
# （拒收落在哪一段、整段完不完整），怎麼解讀每次重算：改片語、續尾或判定規則都用得上舊的結果，
# 只有 ScriptDom 版本、拒收錯誤碼或 <cache-facts> 區段變了才整份作廢。
#
# 片語的判定規則與第三階段相同，只多了一條：普通名稱在字本身就被拒、而候選字撐過了字本身，
# 也算——ROWS BETWEEN UNBOUNDED 後面要接 PRECEDING 才完整，整段比對的話它與普通名稱一起被拒，
# 永遠分不出來。
# ScriptDom 是 .NET Framework 組件，編譯時要 mscorlib 的轉送組件。
$proberSource = @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

public static class SqlAssistPhraseProber
{
    private const string CacheFormat = "SqlAssistProbeCache/1";

    // 中斷（Ctrl+C）攔不到，只能定期存：最多白算這麼久。
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(2);

    private static ThreadLocal<TSqlParser> _parser;
    private static HashSet<int> _rejecting;
    private static string _cacheKey;
    private static string _cachePath;
    private static long _parses;
    private static readonly Stopwatch _sinceSave = Stopwatch.StartNew();
    private static long _savedMisses;

    private static readonly Memo<byte[]> _classes = new Memo<byte[]>();
    private static readonly Memo<bool> _complete = new Memo<bool>();
    private static readonly Memo<int> _rejection = new Memo<int>();
    private static readonly Memo<long> _accepted = new Memo<long>();

    public static void Initialize(Type parserType, int[] rejecting, string cacheKey, string cachePath, bool loadCache)
    {
        // 建構參數是 initialQuotedIdentifiers；只傳 true 會落到 nonPublic 那個多載。
        _parser = new ThreadLocal<TSqlParser>(() => (TSqlParser)Activator.CreateInstance(parserType, new object[] { true }));
        _rejecting = new HashSet<int>(rejecting);
        _cacheKey = cacheKey;
        _cachePath = cachePath;

        if (loadCache)
        {
            LoadCache(cachePath);
        }
    }

    // <cache-facts>
    // 這個區段產生的是存進快取的事實，文字一變快取就整份作廢；解讀事實的規則放在區段外。
    private const byte Recanted = 0;
    private const byte InWord = 1;
    private const byte InContinuation = 2;
    private const byte Whole = 3;

    // 每拼接這麼多次，就另外整段剖析一次比對；對不上代表拼接的前提有漏洞，快取不能留。
    private const int VerifyEvery = 64;

    private static readonly ConcurrentDictionary<string, TSqlParserToken[]> _heads =
        new ConcurrentDictionary<string, TSqlParserToken[]>(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, TSqlParserToken[]> _tails =
        new ConcurrentDictionary<string, TSqlParserToken[]>(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> _joins =
        new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private static long _spliced;
    private static volatile bool _mismatch;

    private static IList<ParseError> ParseErrors(string text)
    {
        IList<ParseError> errors;
        _parser.Value.Parse(new StringReader(text), out errors);
        Interlocked.Increment(ref _parses);
        return errors;
    }

    /// <summary>剖析 prefix + word + continuation，結果與整段剖析相同，但斷詞只做一次。</summary>
    /// <remarks>
    /// 整段剖析一次約 80µs，改餵斷好的詞元只要約 20µs；同一個前綴配同一個字要接幾十條續尾，不必每次重新斷詞。
    /// 前綴連同那個字一起斷（字要自成一個詞元，才知道前綴沒有吃進它），續尾另外斷，接起來餵剖析器。斷詞不是處處與上下文無關：GO 只有在行首才是批次分隔，SELECT a FROM t GO 的 GO
    /// 是識別字、單獨斷卻是分隔符號，所以字與續尾連在一起斷的結果要與分開斷的相同才拼，否則整段剖析。
    /// </remarks>
    private static IList<ParseError> ParseErrors(string prefix, string word, string continuation)
    {
        var head = _heads.GetOrAdd(prefix + word, key => LexHead(key, prefix.Length));
        var tail = _tails.GetOrAdd(continuation, LexTail);

        if (head == null || tail == null || !_joins.GetOrAdd(word + "\u0001" + continuation, key => JoinsCleanly(word, continuation)))
        {
            return ParseErrors(prefix + word + continuation);
        }

        // 同一份詞元會被幾個執行緒同時拿去剖析，每次複製一份，不賭剖析器不改它。
        var tokens = new List<TSqlParserToken>(head.Length + tail.Length);

        foreach (var token in head)
        {
            tokens.Add(new TSqlParserToken(token.TokenType, token.Offset, token.Text, token.Line, token.Column));
        }

        var last = head[head.Length - 1];
        var length = prefix.Length + word.Length;
        var column = last.Column + last.Text.Length;

        foreach (var token in tail)
        {
            tokens.Add(new TSqlParserToken(token.TokenType, length + token.Offset, token.Text, last.Line, column + token.Offset));
        }

        IList<ParseError> errors;
        _parser.Value.Parse(tokens, out errors);
        Interlocked.Increment(ref _parses);

        if (Interlocked.Increment(ref _spliced) % VerifyEvery == 0)
        {
            var text = prefix + word + continuation;
            var expected = ErrorSignature(ParseErrors(text));
            var actual = ErrorSignature(errors);

            if (expected != actual)
            {
                _mismatch = true;
                throw new InvalidOperationException(string.Format(
                    "拼接詞元的剖析結果與整段剖析不同，快取已刪除：{0}（整段 {1}，拼接 {2}）", text, expected, actual));
            }
        }

        return errors;
    }

    /// <summary>斷詞，含最後的檔案結尾詞元；有斷詞錯誤就回傳 null，交給整段剖析。</summary>
    private static TSqlParserToken[] Lex(string text, bool keepEnd)
    {
        IList<ParseError> errors;
        var tokens = _parser.Value.GetTokenStream(new StringReader(text), out errors);

        if (errors.Count > 0 || tokens.Count == 0 || tokens[tokens.Count - 1].TokenType != TSqlTokenType.EndOfFile)
        {
            return null;
        }

        var result = new TSqlParserToken[keepEnd ? tokens.Count : tokens.Count - 1];

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = tokens[index];
        }

        return result;
    }

    private static TSqlParserToken[] LexHead(string text, int wordStart)
    {
        var tokens = Lex(text, false);

        if (tokens == null || tokens.Length == 0)
        {
            return null;
        }

        var last = tokens[tokens.Length - 1];
        return last.Offset == wordStart && last.Text.Length == text.Length - wordStart ? tokens : null;
    }

    // 檔案結尾詞元要用斷詞器給的那一個：自己造的 Text 是空字串，剖析器在 WITHIN GROUP (GRAPH 的結尾
    // 走的路就不同（整段剖析報內部錯誤 46001，自己造的報 46010）。
    // 續尾的詞元沿用字所在的那一行，跨行的話行號對不上。
    private static TSqlParserToken[] LexTail(string continuation)
    {
        return continuation.IndexOf('\n') < 0 && continuation.IndexOf('\r') < 0 ? Lex(continuation, true) : null;
    }

    private static bool JoinsCleanly(string word, string continuation)
    {
        var joined = Lex(word + continuation, true);
        var alone = Lex(word, false);
        var tail = _tails.GetOrAdd(continuation, LexTail);

        if (joined == null || alone == null || tail == null || joined.Length != alone.Length + tail.Length)
        {
            return false;
        }

        for (var index = 0; index < joined.Length; index++)
        {
            var expected = index < alone.Length ? alone[index] : tail[index - alone.Length];
            var offset = index < alone.Length ? expected.Offset : word.Length + expected.Offset;

            if (joined[index].TokenType != expected.TokenType || joined[index].Offset != offset || joined[index].Text != expected.Text)
            {
                return false;
            }
        }

        return true;
    }

    // 產生器用得到的只有每個錯誤碼最早落在哪裡；拼接與整段在錯誤復原之後報的重複錯誤次數會不同，不比。
    private static string ErrorSignature(IList<ParseError> errors)
    {
        var earliest = new SortedDictionary<int, int>();

        foreach (var error in errors)
        {
            int offset;

            if (!earliest.TryGetValue(error.Number, out offset) || error.Offset < offset)
            {
                earliest[error.Number] = error.Offset;
            }
        }

        var signature = new StringBuilder();

        foreach (var pair in earliest)
        {
            signature.Append(pair.Key).Append('@').Append(pair.Value).Append(';');
        }

        return signature.ToString();
    }

    private static int ComputeRejection(string text)
    {
        return FirstRejectionIn(ParseErrors(text));
    }

    private static int ComputeRejection(string prefix, string word, string continuation)
    {
        return FirstRejectionIn(ParseErrors(prefix, word, continuation));
    }

    private static int FirstRejectionIn(IList<ParseError> errors)
    {
        var first = int.MaxValue;

        foreach (var error in errors)
        {
            if (_rejecting.Contains(error.Number) && error.Offset < first)
            {
                first = error.Offset;
            }
        }

        return first;
    }

    private static bool ComputeComplete(string text)
    {
        return ParseErrors(text).Count == 0;
    }

    private static bool ComputeComplete(string prefix, string word, string continuation)
    {
        return ParseErrors(prefix, word, continuation).Count == 0;
    }

    /// <summary>每個字接上續尾之後最早的拒收落在哪一段，每字兩個位元。</summary>
    private static byte[] ComputeClasses(string probe, string[] words, string continuation)
    {
        var classes = new byte[words.Length];

        Parallel.For(0, words.Length, index =>
        {
            var word = words[index];
            var wordEnd = probe.Length + word.Length;
            var rejection = ComputeRejection(probe, word, continuation);

            classes[index] =
                rejection < probe.Length ? Recanted :
                rejection <= wordEnd ? InWord :
                rejection <= wordEnd + continuation.Length ? InContinuation :
                Whole;
        });

        var packed = new byte[(words.Length + 3) / 4];

        for (var index = 0; index < words.Length; index++)
        {
            packed[index / 4] |= (byte)(classes[index] << (index % 4 * 2));
        }

        return packed;
    }

    /// <summary>第三階段的事實：最早的拒收位置（低 32 位元），以及補上分號仍報 46097（第 32 位元）。</summary>
    /// <remarks>
    /// 46097 = "MERGE 陳述式必須以分號結尾"，只在 MERGE 已經完整時出現。少了分號時剖析器只報這一條，
    /// 之後的字一路跳到分號都不再檢查，動作寫完之後的格子「接受」普通名稱，非保留字的 OUTPUT 就分不出來。
    /// 補上分號再剖析一次：落在分號上的錯誤是語句沒寫完，與出現未預期的檔案結尾同義，不算；
    /// 分號補上了還報 46097，代表剖析器又跳過了一段，整段過不了。
    /// </remarks>
    private static long ComputeAcceptedFacts(string prefix, string word, string continuation)
    {
        var text = prefix + word + continuation;
        var errors = ParseErrors(prefix, word, continuation);
        var skipped = false;

        if (HasError(errors, 46097))
        {
            var retried = ParseErrors(prefix, word, continuation + ";");
            skipped = HasError(retried, 46097);
            errors = new List<ParseError>();

            foreach (var error in retried)
            {
                if (error.Offset < text.Length)
                {
                    errors.Add(error);
                }
            }
        }

        var first = int.MaxValue;

        foreach (var error in errors)
        {
            if (_rejecting.Contains(error.Number) && error.Offset < first)
            {
                first = error.Offset;
            }
        }

        return (skipped ? 1L << 32 : 0L) | (uint)first;
    }

    private static bool HasError(IList<ParseError> errors, int number)
    {
        foreach (var error in errors)
        {
            if (error.Number == number)
            {
                return true;
            }
        }

        return false;
    }
    // </cache-facts>

    private static byte ClassOf(byte[] packed, int index)
    {
        return (byte)((packed[index / 4] >> (index % 4 * 2)) & 3);
    }

    /// <summary>最早一個拒收錯誤的位置；沒有就是 int.MaxValue。</summary>
    public static int FirstRejection(string text)
    {
        return _rejection.Get(text, ComputeRejection);
    }

    public static bool IsComplete(string text)
    {
        return _complete.Get(text, ComputeComplete);
    }

    private static bool IsComplete(string prefix, string word, string continuation)
    {
        return _complete.Get(prefix + word + continuation, key => ComputeComplete(prefix, word, continuation));
    }

    /// <summary>第三階段：每個關鍵字在每個位置是否合法；位置的樣板任一個接得上就算。</summary>
    public static bool[][] ClassifyPositions(string[] keywords, bool[] canBeName, string[][] templates, string[] continuations, string plain)
    {
        var result = new bool[keywords.Length][];

        Parallel.For(0, keywords.Length, index =>
        {
            result[index] = new bool[templates.Length];

            for (var position = 0; position < templates.Length; position++)
            {
                foreach (var prefix in templates[position])
                {
                    if (KeywordAllowed(prefix, keywords[index], canBeName[index], continuations, plain))
                    {
                        result[index][position] = true;
                        break;
                    }
                }
            }
        });

        EndBatch();
        return result;
    }

    // 前綴連同字的詞元只在同一批探測裡重複用到，留著只會把記憶體吃光；檢查點也趁批次之間存，
    // 那時沒有平行工作在寫快取。
    private static void EndBatch()
    {
        _heads.Clear();

        if (_sinceSave.Elapsed >= CheckpointInterval && Misses() != _savedMisses)
        {
            SaveCache(false);
        }
    }

    private static long Misses()
    {
        return (long)_classes.Misses + _complete.Misses + _rejection.Misses + _accepted.Misses;
    }

    // 非保留字（APPLY、NOLOCK、GO…）當名字寫也合法，所以任何接受名稱的位置都「接受」它們：
    // CREATE TABLE t ( 之後的 NOLOCK 只是一個叫 NOLOCK 的資料行。一條規則分開兩種情形，
    // 不分位置：同一組續尾換成普通名稱也過的話，那一次只證明它能當名字，不算數；
    // 普通名稱過不了而它過得了，才是它以關鍵字的身分屬於這個位置（BEGIN TRY）。
    // 這一比看的是整段而不只到字為止：SELECT Lib_Reader VALUE FOR s 在名稱之後才出錯，
    // 只看到名稱為止的話它也「過」，NEXT VALUE FOR 就分不出來。
    // 保留字不必比：它們當不了名字，被接受就一定是以關鍵字的身分。
    private static bool KeywordAllowed(string prefix, string keyword, bool canBeName, string[] continuations, string plain)
    {
        foreach (var continuation in continuations)
        {
            var accepted = canBeName
                ? Accepted(prefix, keyword, continuation, true) && !Accepted(prefix, plain, continuation, true)
                : Accepted(prefix, keyword, continuation, false);

            if (accepted)
            {
                return true;
            }
        }

        return false;
    }

    // whole：整段都要過，不只到這個字為止。只到字為止的判定看不出 46097 跳過的是哪裡，不看它。
    private static bool Accepted(string prefix, string word, string continuation, bool whole)
    {
        var facts = _accepted.Get(prefix + word + continuation, key => ComputeAcceptedFacts(prefix, word, continuation));

        if (whole && (facts >> 32) != 0)
        {
            return false;
        }

        var limit = prefix.Length + word.Length + (whole ? continuation.Length : 0);
        return (int)(facts & 0xFFFFFFFFL) > limit;
    }

    /// <summary>樣板接上這個字就是完整的一句，而且這個字寫完的是語句裡的一項，不是語句本身。</summary>
    /// <remarks>
    /// 一項是語法樹裡語句以外的片段：運算式、排序項、資料表提示。BEGIN TRAN 也完整，但以 TRAN
    /// 結尾的只有語句本身——那種字之後往回找子句，找到的是上一句的；SELECT a COMMIT 的 COMMIT
    /// 是下一句，批次分隔的 GO 不在任何片段裡，同樣不算。
    /// </remarks>
    public static bool EndsItem(string template, string word)
    {
        IList<ParseError> errors;
        var text = template + word;
        var fragment = _parser.Value.Parse(new StringReader(text), out errors);

        if (errors.Count > 0 || fragment == null)
        {
            return false;
        }

        var finder = new ItemEndingFinder(template.Length, text.Length);
        fragment.Accept(finder);
        return finder.Found;
    }

    /// <summary>樣板接上這個字就是完整的一句，而且這個字寫完的是那一句本身，不是其中的一項。</summary>
    /// <remarks>
    /// 游標所在的是包住這個字最內層的那一句：IF 的主體、BEGIN … END 裡的一句都算自己的一句。
    /// </remarks>
    public static bool EndsStatement(string template, string word)
    {
        IList<ParseError> errors;
        var text = template + word;
        var fragment = _parser.Value.Parse(new StringReader(text), out errors);

        if (errors.Count > 0 || fragment == null)
        {
            return false;
        }

        var statements = new StatementFinder(template.Length);
        fragment.Accept(statements);

        if (statements.Innermost == null ||
            statements.Innermost.StartOffset + statements.Innermost.FragmentLength != text.Length)
        {
            return false;
        }

        var items = new ItemEndingFinder(template.Length, text.Length);
        fragment.Accept(items);
        return !items.Found;
    }

    private sealed class StatementFinder : TSqlFragmentVisitor
    {
        private readonly int _offset;

        public StatementFinder(int offset)
        {
            _offset = offset;
        }

        public TSqlStatement Innermost { get; private set; }

        public override void Visit(TSqlStatement node)
        {
            if (node.StartOffset <= _offset &&
                node.StartOffset + node.FragmentLength > _offset &&
                (Innermost == null || node.FragmentLength < Innermost.FragmentLength))
            {
                Innermost = node;
            }
        }
    }

    private sealed class ItemEndingFinder : TSqlFragmentVisitor
    {
        private readonly int _wordStart;
        private readonly int _end;

        public ItemEndingFinder(int wordStart, int end)
        {
            _wordStart = wordStart;
            _end = end;
        }

        public bool Found { get; private set; }

        public override void Visit(TSqlFragment node)
        {
            if (!(node is TSqlStatement) && !(node is TSqlBatch) && !(node is TSqlScript) &&
                node.StartOffset >= 0 &&
                node.StartOffset <= _wordStart &&
                node.StartOffset + node.FragmentLength == _end)
            {
                Found = true;
            }
        }
    }

    /// <summary>普通名稱配上任何一條續尾組得成完整的語句，這一格就不封閉。</summary>
    /// <remarks>
    /// 要完整而不只是沒被拒：SET TRANSACTION Lib_Reader 在檔案結尾之前一個錯都沒有，
    /// 剖析器要看到後面的 LEVEL 才說「必須是 ISOLATION」。
    /// </remarks>
    public static bool AcceptsName(string probe, string plain, string[] continuations)
    {
        foreach (var continuation in continuations)
        {
            if (IsComplete(probe, plain, continuation))
            {
                return true;
            }
        }

        return false;
    }

    public static string[] Probe(string probe, string[] pool, string[] reserved, string[] continuations, string plain)
    {
        var reservedSet = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase);

        // 普通名稱排在最後一格，與候選字一起分類。
        var words = new string[pool.Length + 1];
        Array.Copy(pool, words, pool.Length);
        words[pool.Length] = plain;
        var wordsKey = HashWords(words);

        var canBeName = new bool[pool.Length];
        var passed = new bool[pool.Length];
        var recanted = new bool[pool.Length];

        for (var index = 0; index < pool.Length; index++)
        {
            canBeName[index] = !reservedSet.Contains(pool[index]);
        }

        foreach (var continuation in continuations)
        {
            var classes = _classes.Get(wordsKey + "\u0001" + probe + "\u0001" + continuation,
                key => ComputeClasses(probe, words, continuation));
            var plainClass = ClassOf(classes, pool.Length);

            for (var index = 0; index < pool.Length; index++)
            {
                var wordClass = ClassOf(classes, index);

                // 剖析器有的地方先收下、讀完才回頭驗：DECRYPTION BY CERTIFICATE KEY 到檔案結尾都沒被拒，
                // 接上金鑰名稱才在 CERTIFICATE 報錯。回頭拒收過的字，只有整句寫得完才算接得上。
                recanted[index] |= wordClass == Recanted;

                if (passed[index])
                {
                    continue;
                }

                // 保留字當不了名字，被接受就一定是以關鍵字的身分。
                passed[index] = canBeName[index]
                    ? (plainClass <= InWord && wordClass >= InContinuation) ||
                        (plainClass <= InContinuation && wordClass == Whole)
                    : wordClass >= InContinuation;
            }
        }

        Parallel.For(0, pool.Length, index =>
        {
            if (!passed[index] || !recanted[index])
            {
                return;
            }

            var complete = false;

            foreach (var continuation in continuations)
            {
                if (IsComplete(probe, pool[index], continuation))
                {
                    complete = true;
                    break;
                }
            }

            passed[index] = complete;
        });

        EndBatch();
        var accepted = new List<string>();

        for (var index = 0; index < pool.Length; index++)
        {
            if (passed[index])
            {
                accepted.Add(pool[index]);
            }
        }

        return accepted.ToArray();
    }

    // FNV-1a 64 位元：候選字清單只隨 ScriptDom 或補充清單改變，拿來區分快取項夠了。
    private static string HashWords(string[] words)
    {
        var hash = 14695981039346656037UL;

        foreach (var word in words)
        {
            foreach (var c in word + "\n")
            {
                hash = (hash ^ c) * 1099511628211UL;
            }
        }

        return hash.ToString("x16");
    }

    public static string CacheSummary()
    {
        return string.Format("剖析 {0} 次；快取沿用 {1} 筆、新增 {2} 筆",
            Interlocked.Read(ref _parses),
            _classes.Hits + _complete.Hits + _rejection.Hits + _accepted.Hits,
            Misses());
    }

    private static void LoadCache(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using (var file = File.OpenRead(path))
            using (var zip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new BinaryReader(zip, Encoding.UTF8))
            {
                if (reader.ReadString() != CacheFormat || reader.ReadString() != _cacheKey)
                {
                    return;
                }

                _classes.Read(reader, r => r.ReadBytes(r.ReadInt32()));
                _complete.Read(reader, r => r.ReadBoolean());
                _rejection.Read(reader, r => r.ReadInt32());
                _accepted.Read(reader, r => r.ReadInt64());
            }
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException)
        {
            // 讀到一半壞掉的快取整份不用，已讀進來的也不採信。
            _classes.Loaded.Clear();
            _complete.Loaded.Clear();
            _rejection.Loaded.Clear();
            _accepted.Loaded.Clear();
        }
    }

    /// <summary>寫回快取。prune：只存這一次用到的項目，樣板或片語改掉之後舊文字的結果不會一直留著；
    /// 只有跑完全程才修剪，檢查點與中途失敗時連同還沒用到的舊項目一起存。</summary>
    public static void SaveCache(bool prune)
    {
        // 拼接比對不上時，之前抽不到的那些也可能是錯的，整份不留。
        if (_mismatch)
        {
            File.Delete(_cachePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
        var temporary = _cachePath + ".tmp";

        using (var file = File.Create(temporary))
        using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(zip, Encoding.UTF8))
        {
            writer.Write(CacheFormat);
            writer.Write(_cacheKey);
            _classes.Write(writer, prune, (w, value) => { w.Write(value.Length); w.Write(value); });
            _complete.Write(writer, prune, (w, value) => w.Write(value));
            _rejection.Write(writer, prune, (w, value) => w.Write(value));
            _accepted.Write(writer, prune, (w, value) => w.Write(value));
        }

        File.Move(temporary, _cachePath, true);
        _savedMisses = Misses();
        _sinceSave.Restart();
    }

    private sealed class Memo<T>
    {
        public readonly ConcurrentDictionary<string, T> Loaded = new ConcurrentDictionary<string, T>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, T> _used = new ConcurrentDictionary<string, T>(StringComparer.Ordinal);
        public int Hits;
        public int Misses;

        public T Get(string key, Func<string, T> compute)
        {
            T value;

            if (_used.TryGetValue(key, out value))
            {
                return value;
            }

            if (Loaded.TryGetValue(key, out value))
            {
                Interlocked.Increment(ref Hits);
            }
            else
            {
                value = compute(key);
                Interlocked.Increment(ref Misses);
            }

            _used[key] = value;
            return value;
        }

        public void Read(BinaryReader reader, Func<BinaryReader, T> read)
        {
            var count = reader.ReadInt32();

            for (var index = 0; index < count; index++)
            {
                var key = reader.ReadString();
                Loaded[key] = read(reader);
            }
        }

        public void Write(BinaryWriter writer, bool prune, Action<BinaryWriter, T> write)
        {
            var entries = new List<KeyValuePair<string, T>>(_used);

            if (!prune)
            {
                foreach (var entry in Loaded)
                {
                    if (!_used.ContainsKey(entry.Key))
                    {
                        entries.Add(entry);
                    }
                }
            }

            writer.Write(entries.Count);

            foreach (var entry in entries)
            {
                writer.Write(entry.Key);
                write(writer, entry.Value);
            }
        }
    }
}
'@

$proberFacts = [regex]::Match($proberSource, '(?s)// <cache-facts>.*// </cache-facts>').Value

if (-not $proberFacts) {
    throw '探測器原始碼裡找不到 <cache-facts> 區段；快取的作廢條件靠它。'
}

$proberFactsHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($proberFacts)))
$cacheKey = "$scriptDomVersion|$($parserType.Name)|$($RejectingErrorNumbers -join ',')|$proberFactsHash"
$resolvedCachePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CachePath)

Add-Type -ReferencedAssemblies @(
    $scriptDomPath, 'mscorlib', 'netstandard', 'System.Runtime', 'System.Collections', 'System.Collections.Concurrent',
    'System.IO.Compression', 'System.Threading', 'System.Threading.Tasks.Parallel'
) -TypeDefinition $proberSource

[SqlAssistPhraseProber]::Initialize($parserType, [int[]]$RejectingErrorNumbers, $cacheKey, $resolvedCachePath, -not $NoCache)
$proberReady = $true

# 中途失敗（驗證不過的 throw 之類）也把算過的結果存回去，下次從這裡接著算。trap 管整個腳本，
# 探測器建好之前的失敗沒有東西可存。Ctrl+C 不經過 trap，靠探測器每兩分鐘一次的檢查點。
trap {
    if ($proberReady) {
        [SqlAssistPhraseProber]::SaveCache($false)
    }

    break
}

$positionNames = @($ContextTemplates.Keys)
$templateArrays = [string[][]]::new($positionNames.Count)

for ($index = 0; $index -lt $positionNames.Count; $index++) {
    $templateArrays[$index] = [string[]]@($ContextTemplates[$positionNames[$index]])
}

$canBeNameArray = [bool[]]@($keywords | ForEach-Object { $reserved -notcontains $_ })
$allowedMatrix = [SqlAssistPhraseProber]::ClassifyPositions([string[]]@($keywords), $canBeNameArray, $templateArrays, [string[]]@($Continuations), $PlainName)
$positions = @{}
$counts = [ordered]@{}

foreach ($name in $positionNames) {
    $counts[$name] = 0
}

for ($index = 0; $index -lt $keywords.Count; $index++) {
    $allowed = [System.Collections.Generic.List[string]]::new()

    for ($position = 0; $position -lt $positionNames.Count; $position++) {
        if ($allowedMatrix[$index][$position]) {
            $allowed.Add($positionNames[$position])
            $counts[$positionNames[$position]]++
        }
    }

    $positions[$keywords[$index]] = $allowed
}

foreach ($name in $positionNames) {
    Write-Host ("  {0,-20} {1,3}" -f $name, $counts[$name])
}

$orphans = $keywords | Where-Object { $positions[$_].Count -eq 0 }

if ($orphans.Count -gt 0) {
    # None 的字只在分析器也判不出位置（Any）時出現。它真正的用法若落在分析器判得出的
    # 位置，使用者在那裡就打不出它——該補的是樣板，不是放寬過濾。
    Write-Warning "有 $($orphans.Count) 個關鍵字不屬於任何位置，將以 None 產出（只在判不出位置時出現）：$($orphans -join ', ')"
}

# ------------------------------------------------------------------ 四、子句片語

# 候選字：關鍵字清單，加上 ScriptDom 產生程式碼時用的全部字串常數。後者正是剖析器
# 用字串比對認的那些非保留字（QUOTED_IDENTIFIER、REBUILD、MATCHED…），但也混著大量
# 與文法無關的字——不必事先挑，接不接得上由下面的探測決定。
$supporterType = $assembly.GetType('Microsoft.SqlServer.TransactSql.ScriptDom.CodeGenerationSupporter')

if (-not $supporterType) {
    throw 'ScriptDom 裡找不到 CodeGenerationSupporter；子句片語的候選字只能從那裡取。'
}

$supporterWords = $supporterType.GetFields([System.Reflection.BindingFlags]'Static,Public,NonPublic') |
    Where-Object IsLiteral |
    ForEach-Object { $_.GetRawConstantValue() } |
    Where-Object { $_ -is [string] -and $_ -match '^[A-Za-z_][A-Za-z0-9_]*$' } |
    ForEach-Object { $_.ToUpperInvariant() }

$phrasePool = @($keywords) + @($supporterWords) | Sort-Object -Unique
Write-Host "子句片語候選字：$($phrasePool.Count) 個"

# 片語的尾巴。執行期由 SqlClausePhrase 以同一份文字比對游標前的詞元：
#   {name}   一個名稱單位，可以含點號與方括號；保留字（ALTER INDEX ALL、ALTER DATABASE CURRENT）與變數也算
#   {value}  一個數值、字串、變數，或一整組括號
#   ()       一整組括號；探測代入 (a)，剖析器對括號裡的內容有要求時（RAISERROR 要訊息、嚴重性、狀態）由 Group 指定
#   (*       還沒關上的左括號清單，游標在左括號或逗號之後。字是左括號之後與「第一項的每一種寫法接逗號」
#            之後的聯集（執行期分不出是哪一格）；括號裡是一個子句、逗號屬於子句的（WITHIN GROUP (ORDER BY …)）寫 Clause = $true。
#            後面還有項的話，那幾項是清單裡某一項的開頭（WITH (* TYPE =：清單裡任一項的 TYPE =），探測代入左括號；
#            清單有固定的第一項時，探測要墊的那幾項寫在 Items（FORMAT_TYPE = DELIMITEDTEXT, ）
#   ,        逗號本身，分隔同一句裡重複的一段（ADD EVENT a.b, ADD EVENT），比對同等號
#   ,*       標頭開的逗號清單，游標在逗號之後；只能是最後一項，前面那段是標頭，以字面字結尾。
#            標頭本身也立成片語，給第一項的字；逗號之後的字以「第一項的每一種寫法接逗號」探測取聯集，探到的新字再往下一項探。
#            清單由位置分析走訪（OptionItem），哪些敘述有這種清單只在這裡說。選項寫完之後還有位置要回報
#            （模組標頭的 AS）的，仍以位置為鍵
#   ...      動詞之後、下一個字面字之前的其餘標頭（EXEC p @a = 1 WITH 的 p @a = 1、BACKUP 的裝置清單）；
#            前後都要是字面字，第一個字是這一句的動詞，由位置分析找。中間第一個詞元不能是關鍵字：
#            EXECUTE AS … WITH 是別的敘述。探測時代入 Gap 那段文字
# 尾巴可以是空的：只認位置，「這個位置接得了這些字」。游標選項這種會重複的格子尾巴寫不出來。
#
# 片語前面那一格由 After 與 Lead 二選一交代：
#   After  片語第一個字前面的位置，名稱取自 $ContextTemplates。探測用那些位置的樣板，
#          執行期也只在前一格是這些位置時才算數——同一條尾巴在不同位置是不同的意思
#          （查詢之後的 FOR 接 XML，UPDATE t SET 的 SET 不是選項的 SET）。
#          兩個都不寫就是 StatementStart：沒有 Lead 的片語都從一句的開頭寫起。
#   Lead   位置分析判不出前一格、而尾巴本身就認得出意思時，探測要墊的文字；執行期不看前一格。
#          判得出來的一律寫 After：同一件事只由位置分析說一次。
# Template 是 After 位置的第幾個樣板（從 0 起），預設第一個：同一個位置的樣板接得上的字不一定相同
# （WHEN MATCHED THEN 之後寫不出 INSERT）。
# Expand 往下再探幾層：每個接得上的字接在片語後面成為新的片語，直到那個字寫完語句為止。
# Values 是剖析器分不出來、只能手寫的字，一樣要剖析得過才收：SET DATEFORMAT 的值在
# 剖析器眼中就是名稱；語句已經完整的片語扣掉了下一句的開頭，同時也是子句字的要補回來
# （更長的片語寫得出那個字時不必：片語裡的每一個字由它前面那段列出，見探測之後的那一段）。
# Closed 由人宣告那一格只有這幾個值。
# Endings 是只有這條片語用得上的續尾（VECTOR_SEARCH 的 METRIC 只收 'cosine' 這種距離名稱、ABORT_AFTER_WAIT 的值
# 之後要關兩層括號）：探這條片語與它的清單項時接在共用續尾之後。共用續尾一變，每個片語的探測都要重剖。
# 同一條尾巴、同一個位置後寫的覆蓋先寫的，所以 Expand 展開出來的片語可以在後面補 Values。
$QueryTails = @('SelectListTail', 'TableSourceTail', 'ExpressionTail', 'OrderByTail', 'GroupByTail')
$ModuleOptions = @('ProcedureOption', 'FunctionOption', 'ViewOption', 'TriggerOption')
$ScopedConfigurations = @(
    'ACCELERATED_PLAN_FORCING', 'ASYNC_STATS_UPDATE_WAIT_AT_LOW_PRIORITY', 'BATCH_MODE_ADAPTIVE_JOINS',
    'BATCH_MODE_MEMORY_GRANT_FEEDBACK', 'BATCH_MODE_ON_ROWSTORE', 'CE_FEEDBACK', 'DEFERRED_COMPILATION_TV',
    'DOP_FEEDBACK', 'ELEVATE_ONLINE', 'ELEVATE_RESUMABLE', 'EXEC_QUERY_STATS_FOR_SCALAR_FUNCTIONS',
    'GLOBAL_TEMPORARY_TABLE_AUTO_DROP', 'IDENTITY_CACHE', 'INTERLEAVED_EXECUTION_TVF', 'ISOLATE_SECURITY_POLICY_CARDINALITY',
    'LAST_QUERY_PLAN_STATS', 'LEDGER_DIGEST_STORAGE_ENDPOINT', 'LEGACY_CARDINALITY_ESTIMATION', 'LIGHTWEIGHT_QUERY_PROFILING',
    'MAXDOP', 'MEMORY_GRANT_FEEDBACK_PERCENTILE_GRANT', 'MEMORY_GRANT_FEEDBACK_PERSISTENCE', 'OPTIMIZE_FOR_AD_HOC_WORKLOADS',
    'OPTIMIZED_PLAN_FORCING', 'OPTIMIZED_SP_EXECUTESQL', 'PARAMETER_SENSITIVE_PLAN_OPTIMIZATION', 'PARAMETER_SNIFFING',
    'PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES', 'QUERY_OPTIMIZER_HOTFIXES', 'ROW_MODE_MEMORY_GRANT_FEEDBACK',
    'TSQL_SCALAR_UDF_INLINING', 'VERBOSE_TRUNCATION_WARNINGS', 'XTP_PROCEDURE_EXECUTION_STATISTICS', 'XTP_QUERY_EXECUTION_STATISTICS'
)

$ClausePhrases = @(
    @{ Pattern = 'SET'; Expand = 4 }
    @{ Pattern = 'SET IDENTITY_INSERT {name}' }
    @{ Pattern = 'SET DATEFORMAT'; Values = @('mdy', 'dmy', 'ymd', 'ydm', 'myd', 'dym'); Closed = $true }
    @{ Pattern = 'SET DEADLOCK_PRIORITY'; Values = @('LOW', 'NORMAL', 'HIGH'); Closed = $true }

    # CREATE、ALTER、DROP 之後是物件種類（Kinds）：展開到名稱為止，名稱之後只列一層（CREATE TABLE t 之後的 AS），
    # 更深的標頭由下面各敘述自己宣告。CREATE 的名稱是新名字，那一格封閉，種類輸出給執行期判新名字；
    # CREATE OR 探不出 ALTER（剖析器要看到整段才收），CREATE OR ALTER 那一條是證據。三層寫得到 UNIQUE CLUSTERED INDEX、XML SCHEMA COLLECTION。
    # DROP、UPDATE、DELETE、MERGE 之後是名稱的位置，目標過濾把關鍵字全擋掉；
    # IF EXISTS、TOP、INTO 這些字只能由片語給。名稱後面要再寫一段才完整的（UPDATE t SET、
    # MERGE t USING、DROP INDEX i ON t、DROP STATISTICS t.s）探測判成封閉，由人宣告不封閉。
    @{ Pattern = 'CREATE'; Expand = 3; Kinds = 'New' }
    @{ Pattern = 'CREATE OR ALTER'; Expand = 1; Kinds = 'New' }
    @{ Pattern = 'ALTER'; Expand = 3; Kinds = 'Existing' }
    @{ Pattern = 'DROP'; Expand = 3; Kinds = 'Existing' }
    @{ Pattern = 'DROP INDEX'; Closed = $false }
    @{ Pattern = 'DROP STATISTICS'; Closed = $false }
    @{ Pattern = 'UPDATE'; Closed = $false }
    @{ Pattern = 'DELETE' }
    @{ Pattern = 'MERGE'; Closed = $false }
    @{ Pattern = 'DROP'; After = @('AlterTableAction'); Expand = 2 }
    @{ Pattern = 'ALTER DATABASE {name}' }
    @{ Pattern = 'ALTER DATABASE {name} SET'; Expand = 1 }

    # 資料庫範圍設定：SCOPED 在剖析器眼中也可以是資料庫名稱，逐字探不出來，整段是證據。
    # SET 之後的設定名稱剖析器什麼都收（認得的幾個另外剖析），只能手寫；名單取 SQL Server 的文件，不含 Synapse 的 DW_COMPATIBILITY_LEVEL。
    @{ Pattern = 'ALTER DATABASE SCOPED CONFIGURATION'; Expand = 3 }
    @{ Pattern = 'ALTER DATABASE SCOPED CONFIGURATION SET'; Values = $ScopedConfigurations }
    @{ Pattern = 'ALTER DATABASE SCOPED CONFIGURATION FOR SECONDARY SET'; Values = $ScopedConfigurations }
    # BACKUP／RESTORE 的標頭：名稱之後是 TO／FROM 與檔案、檔案群組，TO／FROM 之後是裝置種類
    # （DISK、URL、TAPE）。HEADERONLY 這一族也走到 FROM 之後；DATABASE、LOG 之後的名稱是展開的一步。
    @{ Pattern = 'BACKUP'; Expand = 2 }
    @{ Pattern = 'RESTORE'; Expand = 2 }
    # SERVICE MASTER KEY 的 MASTER 剖析器當名稱讀，手寫。
    @{ Pattern = 'BACKUP SERVICE'; Values = @('MASTER') }
    @{ Pattern = 'RESTORE SERVICE'; Values = @('MASTER') }

    # CREATE INDEX 寫完欄位就是完整的語句；WITH 同時是 CTE 的開頭，被當成下一句扣掉了，手寫補回來。
    # WITH ( 之後的選項由位置給（IndexOption），INCLUDE、篩選的 WHERE 夾在中間也一樣。
    # INDEX 前面可以夾 UNIQUE、CLUSTERED 這些字，那一格判不出位置；尾巴本身只出現在 CREATE INDEX。
    @{ Pattern = 'ALTER INDEX {name} ON {name}' }
    @{ Pattern = 'INDEX {name} ON {name} ()'; Lead = 'CREATE '; Values = @('WITH') }
    @{ Pattern = 'INCLUDE ()'; Lead = 'CREATE INDEX t ON t (a) '; Values = @('WITH') }
    @{ Pattern = ''; After = @('IndexOption') }

    # 其餘接索引選項的 WITH (…)：ALTER INDEX 的 REBUILD、REORGANIZE、SET，ALTER TABLE 的 REBUILD、SWITCH，
    # 以及條件約束的索引鍵之後（PRIMARY KEY (a) WITH (…)）。各敘述收的選項不同，標頭固定的寫成括號清單由剖析器探；
    # 條件約束的前一格判不出位置（CONSTRAINT pk 之後），KEY ( ) WITH ( 這條尾巴本身認得出來。
    # ONLINE = ON ( 與 WAIT_AT_LOW_PRIORITY ( 是清單裡再開的清單，MAX_DURATION 是固定的第一項：
    # ABORT_AFTER_WAIT 要墊在它之後，值之後還要關兩層括號。
    @{ Pattern = 'ALTER INDEX {name} ON {name} REBUILD WITH (*' }
    @{ Pattern = 'ALTER INDEX {name} ON {name} REORGANIZE WITH (*' }
    @{ Pattern = 'ALTER INDEX {name} ON {name} SET (*' }
    @{ Pattern = 'ALTER TABLE {name} REBUILD WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} SWITCH TO {name} WITH (*' }
    @{ Pattern = 'KEY () WITH (*'; Lead = 'ALTER TABLE t ADD PRIMARY ' }
    @{ Pattern = 'UNIQUE () WITH (*'; Lead = 'ALTER TABLE t ADD ' }
    @{ Pattern = 'CLUSTERED () WITH (*'; Lead = 'ALTER TABLE t ADD PRIMARY KEY ' }
    @{ Pattern = 'NONCLUSTERED () WITH (*'; Lead = 'ALTER TABLE t ADD PRIMARY KEY ' }
    # 墊的文字與上面的清單片語相同：ONLINE、WAIT_AT_LOW_PRIORITY 已由那一份列出，不必另加到判不出位置的地方。
    @{ Pattern = 'ONLINE = ON (*'; Lead = 'ALTER INDEX t ON t REBUILD WITH (' }
    @{ Pattern = 'WAIT_AT_LOW_PRIORITY (*'; Lead = 'ALTER TABLE t SWITCH TO t WITH (' }
    @{ Pattern = 'WAIT_AT_LOW_PRIORITY (* MAX_DURATION = {value}'; Lead = 'ALTER TABLE t SWITCH TO t WITH (' }
    @{ Pattern = 'WAIT_AT_LOW_PRIORITY (* ABORT_AFTER_WAIT ='; Lead = 'ALTER TABLE t SWITCH TO t WITH ('; Items = 'MAX_DURATION = 1 MINUTES, '; Endings = @(' ))') }

    # 其餘帶 WITH (ONLINE = …) 的。DROP INDEX 一次刪得了幾個，… 從動詞跨過前面幾個；ALTER TABLE 裡的 DROP、ALTER
    # 位置分析當成動詞，DROP CONSTRAINT、DROP COLUMN、ALTER COLUMN 之後長度不定的一段同樣以 … 跨過。
    # DROP COLUMN 本身不帶 WITH：那是同一句後面 CONSTRAINT 的選項。
    # ALTER COLUMN 的名稱之後是型別或 ADD、DROP（PERSISTED、NOT FOR REPLICATION）。名稱之後不展開：型別那一格的
    # NATIONAL 之後接得上名稱，再往下每一個字都是一整輪探測（展開三層探了三百多萬次）；ADD、DROP 各展開一層。
    # ADD、DROP 也是語句開頭字，… 找動詞會停在它們上，之後的 WITH ( 從語句開頭寫起；以 Lead 認的話，探測文字也比對得到
    # 展開出來的 ALTER TABLE … ADD NOT，兩條搶同一格。
    @{ Pattern = 'DROP INDEX ... WITH (*'; Gap = 'i ON t' }
    @{ Pattern = 'DROP INDEX ... WITH (* MOVE TO'; Gap = 'i ON t' }
    @{ Pattern = 'DROP CONSTRAINT ... WITH (*'; Lead = 'ALTER TABLE t '; Gap = 'k' }
    @{ Pattern = 'DROP COLUMN ... WITH (*'; Lead = 'ALTER TABLE t '; Gap = 'a, CONSTRAINT k' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name}' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} ADD'; Expand = 1 }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} DROP'; Expand = 1 }
    @{ Pattern = 'ALTER COLUMN ... WITH (*'; Lead = 'ALTER TABLE t '; Gap = 'a int' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} ADD PERSISTED WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} DROP PERSISTED WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} ADD SPARSE WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} DROP SPARSE WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} ADD ROWGUIDCOL WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} DROP ROWGUIDCOL WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} ADD NOT FOR REPLICATION WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} ALTER COLUMN {name} DROP NOT FOR REPLICATION WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} REBUILD PARTITION = {value} WITH (*' }
    @{ Pattern = 'ALTER INDEX {name} ON {name} REBUILD PARTITION = {value} WITH (*' }
    @{ Pattern = 'ALTER TABLE {name} REBUILD PARTITION = ALL WITH (*' }
    @{ Pattern = 'ALTER INDEX {name} ON {name} REBUILD PARTITION = ALL WITH (*' }
    @{ Pattern = 'CREATE CLUSTERED COLUMNSTORE INDEX {name} ON {name} WITH (*' }
    @{ Pattern = 'CREATE NONCLUSTERED COLUMNSTORE INDEX {name} ON {name} () WITH (*' }
    @{ Pattern = 'CREATE COLUMNSTORE INDEX {name} ON {name} () WITH (*' }

    # 向量索引：METRIC、TYPE 的值是字串，METRIC 只收距離的名稱。
    @{ Pattern = 'CREATE VECTOR INDEX {name} ON {name} () WITH (*'; Endings = @(" = 'cosine'") }

    # 只認位置的格子。觸發程序標頭之後是 AFTER、FOR、INSTEAD、WITH，再下一層是 OF 與 EXECUTE；
    # 事件清單與游標選項每一格都是同一個位置，第二項之後也一樣。
    @{ Pattern = ''; After = @('TriggerHeader'); Expand = 1 }
    @{ Pattern = ''; After = @('TriggerEvent', 'CursorOption') }

    # BACKUP／RESTORE 的 WITH 選項清單：兩者的選項不同，由標頭分開。BACKUP CERTIFICATE 接的是別的選項，不在這裡。
    @{ Pattern = 'BACKUP DATABASE ... WITH ,*'; Gap = "d TO DISK = 'x'" }
    @{ Pattern = 'BACKUP LOG ... WITH ,*'; Gap = "d TO DISK = 'x'" }
    @{ Pattern = 'RESTORE DATABASE ... WITH ,*'; Gap = "d FROM DISK = 'x'" }
    @{ Pattern = 'RESTORE LOG ... WITH ,*'; Gap = "d FROM DISK = 'x'" }

    # DDL 與登入觸發程序：ON 之後是資料表、DATABASE 或 ALL SERVER。資料表之後還要寫事件才完整，
    # 探測判成封閉會把資料表名稱藏起來，由人宣告不封閉。DDL 事件（CREATE_TABLE、LOGON）依標頭而不同，
    # 寫成清單片語；資料表的 INSERT、UPDATE、DELETE 是位置 TriggerEvent。
    @{ Pattern = 'TRIGGER {name} ON'; After = @('DdlObject'); Closed = $false }
    @{ Pattern = 'TRIGGER {name} ON ALL'; After = @('DdlObject') }
    @{ Pattern = 'TRIGGER {name} ON DATABASE FOR ,*'; After = @('DdlObject') }
    @{ Pattern = 'TRIGGER {name} ON DATABASE AFTER ,*'; After = @('DdlObject') }
    @{ Pattern = 'TRIGGER {name} ON ALL SERVER FOR ,*'; After = @('DdlObject') }
    @{ Pattern = 'TRIGGER {name} ON ALL SERVER AFTER ,*'; After = @('DdlObject') }

    # 模組的 WITH 選項：四種模組的選項不同，EXECUTE AS 之後的 CALLER、SELF、OWNER 除了檢視都共用；
    # 函式的兩個多字選項寫全，中間每一格由它們補出來。
    @{ Pattern = ''; After = $ModuleOptions }
    @{ Pattern = 'EXECUTE AS'; After = @('ProcedureOption', 'FunctionOption', 'TriggerOption') }
    @{ Pattern = 'EXEC AS'; After = @('ProcedureOption', 'FunctionOption', 'TriggerOption') }
    @{ Pattern = 'RETURNS NULL ON NULL INPUT'; After = @('FunctionOption') }
    @{ Pattern = 'CALLED ON NULL INPUT'; After = @('FunctionOption') }

    # MERGE 的 WHEN 之後是 MATCHED 與 NOT MATCHED，這兩者之後各再一層；NOT MATCHED 由下一條補出來。
    # NOT MATCHED BY TARGET／SOURCE 之後各再一層（THEN、AND）。
    # THEN 之後的動作：WHEN MATCHED 接 UPDATE、DELETE，WHEN NOT MATCHED 接 INSERT，INSERT 用第二個樣板探測；
    # INSERT 的資料行清單之後是 VALUES（沒有清單的 INSERT VALUES、INSERT DEFAULT VALUES 由 INSERT 那條給）。
    @{ Pattern = ''; After = @('MergeWhen'); Expand = 1 }
    @{ Pattern = 'NOT MATCHED BY'; After = @('MergeWhen'); Expand = 1 }
    @{ Pattern = 'UPDATE'; After = @('MergeAction') }
    @{ Pattern = 'INSERT'; After = @('MergeAction'); Template = 1 }
    @{ Pattern = 'INSERT ()'; After = @('MergeAction'); Template = 1 }

    @{ Pattern = 'EXECUTE AS' }
    @{ Pattern = 'EXEC AS' }
    @{ Pattern = 'ENABLE TRIGGER'; Closed = $false }
    @{ Pattern = 'DISABLE TRIGGER'; Closed = $false }

    # 外部索引鍵的參考動作；資料行型別之後判不出位置（CREATE TABLE t (a int IDENTITY |）。
    @{ Pattern = 'ON DELETE'; After = @('ReferencesTail'); Expand = 1 }
    @{ Pattern = 'ON UPDATE'; After = @('ReferencesTail'); Expand = 1 }
    @{ Pattern = 'NOT FOR'; Lead = 'CREATE TABLE t (a int IDENTITY ' }

    # 時態表：期間資料行（GENERATED ALWAYS AS ROW START）寫在型別之後，同樣判不出位置；PERIOD FOR SYSTEM_TIME
    # 是資料表層級的一項。資料表選項 WITH (…)、ALTER TABLE SET (…) 與 SYSTEM_VERSIONING = ON (…) 是括號清單。
    @{ Pattern = 'GENERATED ALWAYS AS ROW START HIDDEN'; Lead = 'CREATE TABLE t (a datetime2 ' }
    @{ Pattern = 'GENERATED ALWAYS AS ROW END HIDDEN'; Lead = 'CREATE TABLE t (a datetime2 ' }
    # AS 之後的 SUSER_SID、TRANSACTION_ID 這些字剖析器要看到 START／END 才收，逐字探不出來，整段是證據；
    # 墊的文字要與上面相同，才補得進同一個 GENERATED ALWAYS AS。
    @{ Pattern = 'GENERATED ALWAYS AS SUSER_SID START HIDDEN'; Lead = 'CREATE TABLE t (a datetime2 ' }
    @{ Pattern = 'GENERATED ALWAYS AS SUSER_SNAME END HIDDEN'; Lead = 'CREATE TABLE t (a datetime2 ' }
    @{ Pattern = 'GENERATED ALWAYS AS TRANSACTION_ID START HIDDEN'; Lead = 'CREATE TABLE t (a datetime2 ' }
    @{ Pattern = 'GENERATED ALWAYS AS SEQUENCE_NUMBER END HIDDEN'; Lead = 'CREATE TABLE t (a datetime2 ' }
    @{ Pattern = 'PERIOD FOR SYSTEM_TIME ()'; After = @('ColumnDefinition', 'AlterTableAdd'); Group = '(a, b)' }
    @{ Pattern = 'CREATE TABLE {name} () WITH (*'; Group = '(a int)' }
    @{ Pattern = 'ALTER TABLE {name} SET (*' }
    @{ Pattern = 'SYSTEM_VERSIONING = ON (*'; Lead = 'CREATE TABLE t (a int) WITH (' }
    @{ Pattern = 'BULK INSERT {name} FROM {value} WITH (*' }
    @{ Pattern = 'CREATE EXTERNAL TABLE {name} () WITH (*'; Group = '(a int)' }
    @{ Pattern = 'CREATE EXTERNAL TABLE {name} WITH (*' }
    @{ Pattern = 'OPENROWSET (*'; After = @('DataSource') }
    # VECTOR_SEARCH 的具名引數順序固定（TABLE、COLUMN、SIMILAR_TO、METRIC、TOP_N），清單一項一項往下探。
    # METRIC 只收距離的名稱，共用續尾寫不完那一項就探不到後面的 TOP_N。
    @{ Pattern = 'VECTOR_SEARCH (*'; After = @('DataSource'); Endings = @(" = 'cosine'") }

    # PolyBase：外部資料來源與檔案格式的 WITH (…)。資料來源的 TYPE = 之後剖析器什麼名稱都收，值取 SQL Server 的文件
    # （RDBMS、SHARD_MAP_MANAGER 是 Azure SQL Database 的）；檔案格式的 FORMAT_TYPE 剖析器會驗，而且要寫在第一項。
    @{ Pattern = 'CREATE EXTERNAL DATA SOURCE {name} WITH (*' }
    @{ Pattern = 'CREATE EXTERNAL DATA SOURCE {name} WITH (* TYPE ='; Values = @('HADOOP', 'BLOB_STORAGE'); Closed = $true }
    @{ Pattern = 'ALTER EXTERNAL DATA SOURCE {name} SET ,*' }
    @{ Pattern = 'CREATE EXTERNAL FILE FORMAT {name} WITH (*' }
    @{ Pattern = 'CREATE EXTERNAL FILE FORMAT {name} WITH (* FORMAT_TYPE =' }
    @{ Pattern = 'CREATE EXTERNAL FILE FORMAT {name} WITH (* FORMAT_OPTIONS (*'; Items = 'FORMAT_TYPE = DELIMITEDTEXT, ' }

    # 稽核：ALTER SERVER AUDIT 的名稱剖析器要看到 TO、WITH 這些字才收，CREATE、ALTER 的展開到不了名稱之後。
    @{ Pattern = 'ALTER SERVER AUDIT {name} WITH (*' }
    @{ Pattern = 'ALTER SERVER AUDIT {name} TO FILE (*' }
    @{ Pattern = 'CREATE SERVER AUDIT {name} TO FILE (*' }
    @{ Pattern = 'CREATE SERVER AUDIT {name} TO {name} WITH (*' }
    @{ Pattern = 'CREATE SERVER AUDIT {name} TO FILE () WITH (*'; Group = "(FILEPATH = 'x')" }

    # 稽核規格：FOR SERVER AUDIT 之後是 ADD、DROP 動作群組，以逗號分隔，最後是 WITH (STATE = …)。ADD、DROP 能開始一句，
    # 位置分析把它們當成動詞、判不出前一格，以尾巴認。稽核名稱與規格名稱之後的語句已經完整，ADD、DROP、WITH 被當成
    # 下一句的開頭扣掉，手寫補回。伺服器稽核規格收的動作群組涵蓋資料庫層級的，取它探；資料庫稽核規格括號裡的
    # 動作 ON 物件 BY 主體沒有收。
    @{ Pattern = 'CREATE SERVER AUDIT SPECIFICATION {name}'; Expand = 3 }
    @{ Pattern = 'CREATE DATABASE AUDIT SPECIFICATION {name}'; Expand = 3 }
    @{ Pattern = 'CREATE SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}'; Values = @('ADD', 'WITH') }
    @{ Pattern = 'CREATE DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}'; Values = @('ADD', 'WITH') }
    @{ Pattern = 'ALTER SERVER AUDIT SPECIFICATION {name}'; Expand = 1; Values = @('ADD', 'DROP', 'WITH') }
    @{ Pattern = 'ALTER DATABASE AUDIT SPECIFICATION {name}'; Expand = 1; Values = @('ADD', 'DROP', 'WITH') }
    @{ Pattern = 'ALTER SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}'; Values = @('ADD', 'DROP', 'WITH') }
    @{ Pattern = 'ALTER DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name}'; Values = @('ADD', 'DROP', 'WITH') }
    @{ Pattern = 'CREATE SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*' }
    @{ Pattern = 'CREATE DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*' }
    @{ Pattern = 'ALTER SERVER AUDIT SPECIFICATION {name} WITH (*' }
    @{ Pattern = 'ALTER DATABASE AUDIT SPECIFICATION {name} WITH (*' }
    @{ Pattern = 'ALTER SERVER AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*' }
    @{ Pattern = 'ALTER DATABASE AUDIT SPECIFICATION {name} FOR SERVER AUDIT {name} WITH (*' }
    @{ Pattern = 'ADD (*'; Lead = 'ALTER SERVER AUDIT SPECIFICATION s ' }
    @{ Pattern = 'DROP (*'; Lead = 'ALTER SERVER AUDIT SPECIFICATION s ' }
    @{ Pattern = 'ADD () ,'; Lead = 'ALTER SERVER AUDIT SPECIFICATION s '; Group = '(SCHEMA_OBJECT_ACCESS_GROUP)' }
    @{ Pattern = 'DROP () ,'; Lead = 'ALTER SERVER AUDIT SPECIFICATION s '; Group = '(SCHEMA_OBJECT_ACCESS_GROUP)' }
    @{ Pattern = 'ADD () WITH (*'; Lead = 'ALTER SERVER AUDIT SPECIFICATION s '; Group = '(SCHEMA_OBJECT_ACCESS_GROUP)' }
    @{ Pattern = 'DROP () WITH (*'; Lead = 'ALTER SERVER AUDIT SPECIFICATION s '; Group = '(SCHEMA_OBJECT_ACCESS_GROUP)' }

    # 安全性原則：ADD FILTER、BLOCK PREDICATE 函式 ON 資料表，BLOCK 之後可以帶 AFTER、BEFORE 的作業，述詞以逗號分隔，
    # 最後是 WITH (STATE = …)。CREATE 的名稱之後語句已經完整，ADD 被當成下一句的開頭扣掉，手寫補回。
    # 函式呼叫寫成名稱加括號，整段從語句開頭寫起：ADD 與作業的 INSERT、UPDATE、DELETE 都是語句開頭字，以 … 跨過的話
    # 找動詞會停在它們上。逗號之後的述詞從逗號寫起，前面墊一個述詞。
    @{ Pattern = 'CREATE SECURITY POLICY {name}'; Expand = 3; Values = @('ADD') }
    @{ Pattern = 'ALTER SECURITY POLICY {name}'; Expand = 3 }
    @{ Pattern = 'CREATE SECURITY POLICY {name} WITH (*' }
    @{ Pattern = 'ALTER SECURITY POLICY {name} WITH (*' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD FILTER PREDICATE {name} () ON {name}' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD FILTER PREDICATE {name} () ON {name} ,' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD FILTER PREDICATE {name} () ON {name} WITH (*' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name}' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} ,' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} WITH (*' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} AFTER INSERT WITH (*' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} AFTER UPDATE WITH (*' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} BEFORE UPDATE WITH (*' }
    @{ Pattern = 'CREATE SECURITY POLICY {name} ADD BLOCK PREDICATE {name} () ON {name} BEFORE DELETE WITH (*' }
    @{ Pattern = ', ADD FILTER PREDICATE {name} () ON {name}'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD FILTER PREDICATE {name} () ON {name} ,'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD FILTER PREDICATE {name} () ON {name} WITH (*'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD BLOCK PREDICATE {name} () ON {name}'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD BLOCK PREDICATE {name} () ON {name} ,'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD BLOCK PREDICATE {name} () ON {name} WITH (*'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD BLOCK PREDICATE {name} () ON {name} AFTER INSERT WITH (*'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD BLOCK PREDICATE {name} () ON {name} AFTER UPDATE WITH (*'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD BLOCK PREDICATE {name} () ON {name} BEFORE UPDATE WITH (*'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }
    @{ Pattern = ', ADD BLOCK PREDICATE {name} () ON {name} BEFORE DELETE WITH (*'; Lead = 'CREATE SECURITY POLICY p ADD FILTER PREDICATE f(a) ON t' }

    # 外部模型（SQL Server 2025）：選項清單順序固定，MODEL_TYPE 的值剖析器會驗。
    @{ Pattern = 'CREATE EXTERNAL MODEL {name} WITH (*' }
    @{ Pattern = 'CREATE EXTERNAL MODEL {name} AUTHORIZATION {name} WITH (*' }
    @{ Pattern = 'CREATE EXTERNAL MODEL {name} WITH (* MODEL_TYPE ='; Items = "LOCATION = 'x', API_FORMAT = 'x', " }
    @{ Pattern = 'CREATE EXTERNAL MODEL {name} AUTHORIZATION {name} WITH (* MODEL_TYPE ='; Items = "LOCATION = 'x', API_FORMAT = 'x', " }

    # 事件通知：ON SERVER、DATABASE、QUEUE，FOR 之後的事件與 DDL 觸發程序同一份，事件之後 TO SERVICE。
    # ON 只展開兩層：第三層是每一個事件各立一個只接 TO 的片語（上千個），事件由 FOR 那一格的片語列。
    # DROP 一次刪得了幾個，名稱以 … 跨過。
    @{ Pattern = 'CREATE EVENT NOTIFICATION {name} ON'; Expand = 2 }
    @{ Pattern = 'CREATE EVENT NOTIFICATION {name} ON SERVER FOR' }
    @{ Pattern = 'CREATE EVENT NOTIFICATION {name} ON DATABASE FOR' }
    @{ Pattern = 'CREATE EVENT NOTIFICATION {name} ON SERVER FOR {name}'; Expand = 2 }
    @{ Pattern = 'CREATE EVENT NOTIFICATION {name} ON DATABASE FOR {name}'; Expand = 2 }
    @{ Pattern = 'DROP EVENT NOTIFICATION ... ON'; Gap = 'a, b' }

    # 擴充事件：名稱之後的 ON SERVER，之後是 ADD EVENT、ADD TARGET。ALTER 的 ADD、DROP 剖析器要看到 EVENT、TARGET
    # 才收，整段是證據。ADD 能開始一句（ADD SIGNATURE），位置分析把每一段 ADD 當成動詞，後面的 ADD 判不出前一格：
    # 以尾巴認，事件之後接逗號是下一個事件、不接逗號是目標。
    @{ Pattern = 'CREATE EVENT SESSION {name} ON'; Expand = 3 }
    @{ Pattern = 'ALTER EVENT SESSION {name} ON'; Expand = 2 }
    @{ Pattern = 'DROP EVENT SESSION {name} ON' }
    @{ Pattern = 'EVENT {name} ,'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD ' }
    @{ Pattern = 'EVENT {name} , ADD'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD ' }
    @{ Pattern = 'EVENT {name} ADD'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD ' }
    @{ Pattern = 'TARGET {name} ,'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD ' }
    @{ Pattern = 'TARGET {name} , ADD'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD ' }
    @{ Pattern = 'ALTER EVENT SESSION {name} ON SERVER ADD EVENT' }
    @{ Pattern = 'ALTER EVENT SESSION {name} ON SERVER ADD TARGET' }
    @{ Pattern = 'ALTER EVENT SESSION {name} ON SERVER DROP EVENT' }
    @{ Pattern = 'ALTER EVENT SESSION {name} ON SERVER DROP TARGET' }
    @{ Pattern = 'EVENT {name} WITH (*'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD ' }
    @{ Pattern = 'TARGET {name} WITH (*'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD ' }
    # 事件與目標可以帶一組括號（事件的 SET、ACTION、WHERE，目標的 SET），括號之後同樣接逗號、ADD、WITH。
    @{ Pattern = 'EVENT {name} (*'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD ' }
    @{ Pattern = 'EVENT {name} () ,'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD '; Group = '(ACTION (t.t))' }
    @{ Pattern = 'EVENT {name} () , ADD'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD '; Group = '(ACTION (t.t))' }
    @{ Pattern = 'EVENT {name} () ADD'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD '; Group = '(ACTION (t.t))' }
    @{ Pattern = 'EVENT {name} () WITH (*'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD '; Group = '(ACTION (t.t))' }
    @{ Pattern = 'TARGET {name} (*'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD ' }
    @{ Pattern = 'TARGET {name} () ,'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD '; Group = '(SET t = 1)' }
    @{ Pattern = 'TARGET {name} () , ADD'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD '; Group = '(SET t = 1)' }
    @{ Pattern = 'TARGET {name} () WITH (*'; Lead = 'CREATE EVENT SESSION t ON SERVER ADD EVENT t.t ADD '; Group = '(SET t = 1)' }

    # 序列的選項不以逗號分隔、順序不限，會重複的格子寫成位置；NO 之後的 CYCLE 往下一層。
    # START 後面非接 WITH 值不可，逐字探測接不上續尾，整段是證據。
    @{ Pattern = ''; After = @('SequenceOption'); Expand = 1 }
    @{ Pattern = 'START WITH {value}'; After = @('SequenceOption') }

    @{ Pattern = 'WAITFOR' }

    # 資料指標語句：名稱前可以夾 GLOBAL，FETCH 的方向之後是 FROM。OPEN、CLOSE 另接對稱金鑰與
    # 資料庫主要金鑰，金鑰名稱之後的 DECRYPTION BY 接憑證、密碼或另一把金鑰，金鑰之後還可以 WITH PASSWORD。
    # OPEN SYMMETRIC KEY 的名稱後面還要寫 DECRYPTION 才完整，探測判成封閉會把名稱藏起來，由人宣告不封閉。
    @{ Pattern = 'OPEN'; Expand = 4 }
    @{ Pattern = 'OPEN SYMMETRIC KEY'; Closed = $false }
    @{ Pattern = 'OPEN SYMMETRIC KEY {name}'; Expand = 6 }
    @{ Pattern = 'CLOSE'; Expand = 2 }

    # 金鑰與憑證的標頭：種類與名稱由 CREATE、ALTER 的展開給，這裡往下寫加密方式（ENCRYPTION BY 憑證、密碼或
    # 另一把金鑰）與 WITH 之後的演算法、主旨。等號之後的值（AES_256、RSA_2048）也由展開列，逗號之後由清單片語。
    @{ Pattern = 'CREATE MASTER KEY'; Expand = 3 }
    @{ Pattern = 'ALTER MASTER KEY'; Expand = 5 }
    # ALTER SYMMETRIC KEY 的 ADD、DROP 之後剖析器把 ENCRYPTION 當名稱讀，逐字探不出來，整段是證據。
    @{ Pattern = 'ALTER SYMMETRIC KEY {name} ADD ENCRYPTION BY'; Expand = 2 }
    @{ Pattern = 'ALTER SYMMETRIC KEY {name} DROP ENCRYPTION BY'; Expand = 2 }

    # 主金鑰的備份與還原、憑證與非對稱金鑰的私密金鑰：檔案路徑、演算法之後的 ENCRYPTION BY、DECRYPTION BY。
    # 剖析器要看到 PASSWORD = 才回頭驗 ENCRYPTION，逐字探不出來，整段寫到 PASSWORD 的片語是證據；
    # 檔案路徑之後的語句已經完整，WITH 也由證據補回。SERVICE 之後剖析器什麼名稱都收，MASTER 寫成名稱，與展開出來的同一格。
    # WITH PRIVATE KEY ( 的項是多字的（ENCRYPTION BY PASSWORD = …），各敘述收的項相近，以尾巴認、取 ALTER CERTIFICATE 探，不封閉。
    # 對稱金鑰的選項清單裡，逗號之後的 ALGORITHM = 也以尾巴認；演算法之後的 ENCRYPTION 以名稱結尾、Lead 立不了，
    # 名稱與前面的選項以 … 跨過。
    @{ Pattern = 'BACKUP MASTER KEY TO FILE = {value}'; Expand = 3 }
    @{ Pattern = 'BACKUP SERVICE {name} KEY TO FILE = {value}'; Expand = 3 }
    @{ Pattern = 'RESTORE MASTER KEY FROM FILE = {value}'; Expand = 3 }
    @{ Pattern = 'RESTORE MASTER KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value}'; Expand = 3 }
    @{ Pattern = 'RESTORE SERVICE {name} KEY FROM FILE = {value}'; Expand = 3 }
    @{ Pattern = 'BACKUP MASTER KEY TO FILE = {value} ENCRYPTION BY PASSWORD' }
    @{ Pattern = 'BACKUP SERVICE {name} KEY TO FILE = {value} ENCRYPTION BY PASSWORD' }
    @{ Pattern = 'RESTORE MASTER KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value} ENCRYPTION BY PASSWORD' }
    @{ Pattern = 'RESTORE SERVICE {name} KEY FROM FILE = {value} DECRYPTION BY PASSWORD' }
    @{ Pattern = 'BACKUP CERTIFICATE {name} TO FILE = {value}'; Expand = 2 }
    @{ Pattern = 'CREATE CERTIFICATE {name} FROM FILE = {value}'; Expand = 2 }
    @{ Pattern = 'ALTER CERTIFICATE {name}'; Expand = 2 }
    @{ Pattern = 'ALTER ASYMMETRIC KEY {name}'; Expand = 2 }
    @{ Pattern = 'BACKUP CERTIFICATE {name} TO FILE = {value} WITH PRIVATE KEY' }
    @{ Pattern = 'CREATE CERTIFICATE {name} FROM FILE = {value} WITH PRIVATE KEY' }
    @{ Pattern = 'ALTER CERTIFICATE {name} WITH PRIVATE KEY' }
    @{ Pattern = 'PRIVATE KEY (*'; Lead = 'ALTER CERTIFICATE t WITH '; Closed = $false }
    @{ Pattern = 'PRIVATE KEY (* ENCRYPTION'; Lead = 'ALTER CERTIFICATE t WITH '; Items = "FILE = 'x', " }
    @{ Pattern = 'PRIVATE KEY (* ENCRYPTION BY'; Lead = 'ALTER CERTIFICATE t WITH '; Items = "FILE = 'x', " }
    @{ Pattern = 'PRIVATE KEY (* ENCRYPTION BY PASSWORD'; Lead = 'ALTER CERTIFICATE t WITH '; Items = "FILE = 'x', " }
    @{ Pattern = 'PRIVATE KEY (* DECRYPTION'; Lead = 'ALTER CERTIFICATE t WITH '; Items = "FILE = 'x', " }
    @{ Pattern = 'PRIVATE KEY (* DECRYPTION BY'; Lead = 'ALTER CERTIFICATE t WITH '; Items = "FILE = 'x', " }
    @{ Pattern = 'PRIVATE KEY (* DECRYPTION BY PASSWORD'; Lead = 'ALTER CERTIFICATE t WITH '; Items = "FILE = 'x', " }
    @{ Pattern = 'CREATE ASYMMETRIC KEY {name} FROM ASSEMBLY {name}'; Expand = 3 }
    @{ Pattern = 'CREATE ASYMMETRIC KEY {name} FROM FILE = {value}'; Expand = 3 }
    @{ Pattern = 'CREATE ASYMMETRIC KEY {name} FROM PROVIDER {name} WITH ALGORITHM = {name}'; Expand = 3 }
    @{ Pattern = 'CREATE ASYMMETRIC KEY {name} FROM ASSEMBLY {name} ENCRYPTION BY PASSWORD' }
    @{ Pattern = 'CREATE ASYMMETRIC KEY {name} FROM PROVIDER {name} WITH ALGORITHM = {name} ENCRYPTION BY PASSWORD' }
    @{ Pattern = ', ALGORITHM ='; Lead = "CREATE SYMMETRIC KEY t WITH KEY_SOURCE = 'x' "; Expand = 3 }
    @{ Pattern = 'CREATE SYMMETRIC KEY ... ALGORITHM = {name} ENCRYPTION BY PASSWORD'; Gap = "t WITH KEY_SOURCE = 'x'," }
    @{ Pattern = 'CREATE SYMMETRIC KEY ... ENCRYPTION BY'; Gap = "t WITH KEY_SOURCE = 'x', IDENTITY_VALUE = 'x'" }
    @{ Pattern = 'CREATE SYMMETRIC KEY ... IDENTITY_VALUE = {value}'; Gap = "t WITH KEY_SOURCE = 'x'," }
    @{ Pattern = 'CREATE SYMMETRIC KEY ... IDENTITY_VALUE = {value} ENCRYPTION BY PASSWORD'; Gap = "t WITH KEY_SOURCE = 'x'," }
    @{ Pattern = 'CREATE SYMMETRIC KEY ... KEY_SOURCE = {value}'; Gap = "t WITH IDENTITY_VALUE = 'x'," }
    @{ Pattern = 'CREATE SYMMETRIC KEY ... KEY_SOURCE = {value} ENCRYPTION BY PASSWORD'; Gap = "t WITH IDENTITY_VALUE = 'x'," }
    @{ Pattern = 'CREATE CERTIFICATE {name}'; Expand = 4 }
    @{ Pattern = 'CREATE ASYMMETRIC KEY {name}'; Expand = 5 }
    @{ Pattern = 'CREATE SYMMETRIC KEY {name}'; Expand = 6 }
    # ALGORITHM = 之後的值寫完，剖析器把下一個字當名稱讀（ENCRYPTION BY 在它眼中是「名稱 BY」），探不出
    # ENCRYPTION；整段剖析得過就是證據，由片語裡的每一個字補進前面那段。金鑰、憑證之後的 WITH 也是 CTE 的開頭，被扣掉了。
    @{ Pattern = 'CREATE SYMMETRIC KEY {name} WITH ALGORITHM = {name} ENCRYPTION BY'; Expand = 2 }
    @{ Pattern = 'CREATE ASYMMETRIC KEY {name} WITH ALGORITHM = {name} ENCRYPTION BY'; Expand = 1 }
    # REGENERATE、FORCE 剖析器也當名稱讀，同樣只有整段是證據。
    @{ Pattern = 'ALTER MASTER KEY REGENERATE WITH ENCRYPTION BY PASSWORD = {value}' }
    @{ Pattern = 'ALTER MASTER KEY FORCE REGENERATE WITH ENCRYPTION BY PASSWORD = {value}' }
    # 資料庫加密金鑰的 WITH 選項、演算法與 SERVER 之後的種類，剖析器一律當名稱收：演算法手寫，其餘整段是證據。
    @{ Pattern = 'CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM ='; Values = @('AES_128', 'AES_192', 'AES_256', 'TRIPLE_DES_3KEY'); Closed = $true }
    @{ Pattern = 'CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {name} ENCRYPTION BY SERVER CERTIFICATE {name}' }
    @{ Pattern = 'CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {name} ENCRYPTION BY SERVER ASYMMETRIC KEY {name}' }
    @{ Pattern = 'OPEN SYMMETRIC KEY {name} DECRYPTION BY ASYMMETRIC KEY {name} WITH PASSWORD' }
    @{ Pattern = 'OPEN SYMMETRIC KEY {name} DECRYPTION BY CERTIFICATE {name} WITH PASSWORD' }
    @{ Pattern = 'CREATE CERTIFICATE {name} WITH ,*' }
    @{ Pattern = 'CREATE CERTIFICATE {name} ENCRYPTION BY PASSWORD = {value} WITH ,*' }
    @{ Pattern = 'CREATE SYMMETRIC KEY {name} WITH ,*' }
    @{ Pattern = 'CREATE CREDENTIAL {name} WITH ,*' }
    @{ Pattern = 'CREATE DATABASE SCOPED CREDENTIAL {name} WITH ,*' }
    @{ Pattern = 'DEALLOCATE' }
    @{ Pattern = 'FETCH'; Expand = 2 }

    # DBCC 之後的命令剖析器什麼名稱都收（未公開的命令、DBCC dllname (FREE)），探不出字；
    # 字來自語句說明登錄的命令（見探測之後那一段），由人宣告封閉：那一格不是任何物件的名稱。
    # 命令寫完已經是完整的一句，WITH 同時是 CTE 的開頭被扣掉了，由下面的清單片語補回。
    # 選項不分命令：剖析器對任何命令都收同一份，命令名稱探測時是 t。
    # 命令括號裡的關鍵字（CHECKIDENT 的 RESEED）同樣探不出來，由語句說明的語法給（見探測之後那一段）。
    @{ Pattern = 'DBCC'; Closed = $true }
    @{ Pattern = 'DBCC {name}' }
    @{ Pattern = 'DBCC {name} ()' }
    @{ Pattern = 'DBCC {name} WITH ,*' }
    @{ Pattern = 'DBCC {name} () WITH ,*' }
    @{ Pattern = 'RAISERROR () WITH ,*'; Group = "('x', 16, 1)" }

    # FOR 有好幾種意思，由前一格的位置分開：查詢寫完之後是 XML、JSON、BROWSE、UPDATE、READ，
    # 資料表之後多一個 SYSTEM_TIME，游標選項之後是查詢，觸發程序標頭之後是 INSERT 這些事件。
    # 查詢寫到 FOR UPDATE 已經完整，展開停在那裡；游標要的 OF 另外探。
    @{ Pattern = 'FOR'; After = $QueryTails; Expand = 1 }
    @{ Pattern = 'FOR UPDATE'; After = $QueryTails }
    @{ Pattern = 'FOR SYSTEM_TIME'; After = @('TableSourceTail'); Expand = 1 }
    @{ Pattern = 'FOR'; After = @('CursorOption') }
    @{ Pattern = 'SYNONYM {name} FOR'; After = @('DdlObject') }

    # FOR XML、FOR JSON 的模式是清單的第一項，由上面 FOR 往下展開的片語給；逗號之後是指示詞。
    @{ Pattern = 'FOR XML ,*'; After = $QueryTails }
    @{ Pattern = 'FOR JSON ,*'; After = $QueryTails }

    # 運算式寫在哪裡都行，函式引數裡判不出位置；CONSTRAINT df 之後也判不出來。
    # 選取清單裡判得出來，另立帶位置的一條：NEXT、AT 這種第一個字也要列得出下一個字。
    @{ Pattern = 'NEXT VALUE FOR'; Lead = 'SELECT ' }
    @{ Pattern = 'NEXT VALUE FOR'; After = @('SelectList') }
    @{ Pattern = 'DEFAULT {value} FOR'; Lead = 'ALTER TABLE t ADD ' }
    @{ Pattern = 'AT TIME'; Lead = 'SELECT a ' }
    @{ Pattern = 'AT TIME'; After = @('SelectListTail') }

    # 有序集合彙總：STRING_AGG、PERCENTILE_CONT 的呼叫之後是 WITHIN GROUP (ORDER BY …)。剖析器要看到 GROUP
    # 才收 WITHIN，WITHIN 由整段證據補到函式呼叫之後；WITHIN GROUP 之後只接左括號。
    @{ Pattern = 'WITHIN GROUP'; After = @('FunctionCallTail') }
    @{ Pattern = 'WITHIN GROUP (*'; After = @('FunctionCallTail'); Clause = $true }

    # UPDATE、DELETE 的 WHERE CURRENT OF 資料指標：CURRENT 之後只有 OF，OF 之後是資料指標名稱或 GLOBAL。
    # SELECT 的 WHERE 寫不出來，所以用 DELETE 的樣板。
    @{ Pattern = 'CURRENT'; After = @('Predicate'); Template = 1; Expand = 1 }

    # IS 之後是 NULL、NOT、DISTINCT FROM。述詞尾端的代表樣板寫完了比較，接不上 IS，用第二個。
    @{ Pattern = 'IS'; After = @('ExpressionTail'); Template = 1; Expand = 2 }
    @{ Pattern = 'IS'; After = @('CaseArm'); Expand = 2 }

    @{ Pattern = 'GROUP BY'; After = @('SelectListTail', 'TableSourceTail', 'ExpressionTail'); Values = @('ROLLUP', 'CUBE', 'GROUPING SETS') }

    # TOP 子句寫完（TOP 10、TOP (10)、TOP 10 PERCENT）之後的 WITH 只接 TIES。
    @{ Pattern = 'WITH'; After = @('TopClauseTail') }

    # OFFSET … FETCH：每一格只有一兩個字，但沒有它們就得整句背下來。
    # OFFSET 10 ROWS 已經是完整的語句，FETCH 同時是游標語句的開頭，被當成下一句扣掉了。
    @{ Pattern = ''; After = @('OffsetTail') }
    @{ Pattern = ''; After = @('FunctionReturns') }

    # EXEC 的 WITH 選項清單：程序與 WITH 之間夾著長度不定的參數清單。
    @{ Pattern = 'EXEC ... WITH ,*'; Gap = 'p' }
    @{ Pattern = 'EXECUTE ... WITH ,*'; Gap = 'p' }

    # RESULT SETS 是 EXEC 選項清單裡的一項，之後是 NONE、UNDEFINED 或結果集清單，RESULT 之後的 SETS 由這一條補出來；
    # 清單裡 AS 之後是 OBJECT、TYPE、FOR（XML），資料行型別之後的 NOT 只接 NULL。
    @{ Pattern = 'RESULT SETS'; After = @('OptionItem'); Template = 2 }
    @{ Pattern = 'AS'; After = @('ResultSetList'); Expand = 1 }
    @{ Pattern = 'NOT'; After = @('ResultSetColumnTail') }
    @{ Pattern = ''; After = @('TableSampleTail') }

    # 權限 ON 之後的類別多半不是關鍵字（OBJECT、TYPE）；那一格也可以直接寫目標名稱，由人宣告不封閉。
    @{ Pattern = ''; After = @('PermissionOn'); Closed = $false }

    # CREATE USER 寫完名稱已經是完整的一句，之後的 FOR、WITHOUT 各自接 LOGIN；CREATE LOGIN 之後是 WITH PASSWORD 或 FROM。
    # 只認 CREATE：ALTER USER、ALTER LOGIN 接的是別的字（ENABLE、WITH NAME）。
    @{ Pattern = 'CREATE USER {name}'; Expand = 1 }
    @{ Pattern = 'CREATE LOGIN {name}'; Expand = 1 }

    # 登入與使用者的 WITH 選項清單：四種敘述接的選項各不相同（CREATE LOGIN 第一項只能是 PASSWORD、
    # ALTER LOGIN 另有 NAME、NO CREDENTIAL，USER 才有 DEFAULT_SCHEMA），由標頭分開；應用程式角色同理。
    @{ Pattern = 'CREATE LOGIN {name} WITH ,*' }
    @{ Pattern = 'CREATE LOGIN {name} FROM WINDOWS WITH ,*' }
    @{ Pattern = 'ALTER LOGIN {name} WITH ,*' }
    @{ Pattern = 'CREATE USER {name} WITH ,*' }
    @{ Pattern = 'CREATE USER {name} FOR LOGIN {name} WITH ,*' }
    @{ Pattern = 'CREATE USER {name} FROM LOGIN {name} WITH ,*' }
    @{ Pattern = 'CREATE USER {name} WITHOUT LOGIN WITH ,*' }
    @{ Pattern = 'ALTER USER {name} WITH ,*' }
    @{ Pattern = 'CREATE APPLICATION ROLE {name} WITH ,*' }
    @{ Pattern = 'ALTER APPLICATION ROLE {name} WITH ,*' }

    # 資料表層級的條件約束：CONSTRAINT 名稱之後是 PRIMARY KEY、UNIQUE、CHECK、FOREIGN KEY。
    @{ Pattern = 'CONSTRAINT {name}'; After = @('ColumnDefinition', 'AlterTableAdd') }
    @{ Pattern = 'ROWS'; After = @('OffsetTail'); Values = @('FETCH') }
    @{ Pattern = 'ROW'; After = @('OffsetTail'); Values = @('FETCH') }
    @{ Pattern = 'ROWS FETCH'; After = @('OffsetTail') }
    @{ Pattern = 'ROW FETCH'; After = @('OffsetTail') }
    @{ Pattern = 'FETCH NEXT {value}'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH FIRST {value}'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH NEXT {value} ROWS'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH NEXT {value} ROW'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH FIRST {value} ROWS'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH FIRST {value} ROW'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }

    # 視窗框架：ORDER BY 的排序項之後是 ROWS、RANGE，框架的每一段由片語往下補。
    # 框架中段（AND 之後）的 UNBOUNDED、CURRENT 前一格判不出位置，仍由 Lead 片語給。
    # CURRENT 之後剖析器收任何識別字（留到語意檢查才擋），ROW 只能手寫。
    @{ Pattern = ''; After = @('WindowOrderTail') }
    @{ Pattern = 'ROWS BETWEEN UNBOUNDED PRECEDING'; After = @('WindowOrderTail') }
    @{ Pattern = 'RANGE BETWEEN UNBOUNDED PRECEDING'; After = @('WindowOrderTail') }
    @{ Pattern = 'ROWS UNBOUNDED'; After = @('WindowOrderTail') }
    @{ Pattern = 'RANGE UNBOUNDED'; After = @('WindowOrderTail') }
    @{ Pattern = 'UNBOUNDED'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND ' }
    @{ Pattern = 'PRECEDING AND'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED ' }
    @{ Pattern = 'ROWS CURRENT'; After = @('WindowOrderTail'); Values = @('ROW'); Closed = $true }
    @{ Pattern = 'RANGE CURRENT'; After = @('WindowOrderTail'); Values = @('ROW'); Closed = $true }
    @{ Pattern = 'BETWEEN CURRENT'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS '; Values = @('ROW'); Closed = $true }
    @{ Pattern = 'AND CURRENT'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING '; Values = @('ROW'); Closed = $true }
)

# 片語的續尾在第三階段那一組之外多幾條：SET 選項值、選項清單的 = ON、字串與括號的結尾，
# 以及幾個要多看一個詞元才分得出來的地方（AFTER 後面沒有 INSERT 就是語法錯誤）。
# 模組選項的名稱要看到本體才驗（寫到檔案結尾為止任何名稱都過），所以函式的兩種本體也在。
# CREATE LOGIN 的 WITH PASSWORD 只收字串，DBCC 的 WITH 只收它自己的選項。
# DECRYPTION BY 之後的 ASYMMETRIC、SYMMETRIC 要看到金鑰名稱才驗。
# 登入與使用者的選項收名稱值（DEFAULT_DATABASE = x）與二進位值（SID = 0x01），而 WITH 之後寫不完時剖析器回頭在 WITH 報錯，要整句寫得完才算。
$PhraseContinuations = @($Continuations) + @(
    ' ON', " 'x'", ' = ON', ' = ON)', ' = 1', ' ON)', ' ROWS ONLY', ' NO_INFOMSGS',
    ' PRECEDING)', " ZONE 'UTC'", ' IN (1)', ' FOR SELECT 1', ' ACTION)',
    ' (a)', ' TIES a FROM t ORDER BY a', ' FROM x', ' INSERT AS SELECT 1', ' OF INSERT AS SELECT 1',
    ' LEVEL READ COMMITTED', ' READ COMMITTED', ' COMMITTED', ' READ', ' TRIGGER ALL',
    ' AS BEGIN RETURN 1 END', ' AS RETURN SELECT 1 AS a', " = 'x'", ' KEY x', ' = x', ' = 0x01'
)

function Get-PhraseProbe {
    param([string]$Lead, [string]$Pattern, [string]$Group, [string]$Gap, [string]$Next, [string]$Items)

    # 只認位置的片語：樣板本身就是探測文字。
    if (-not $Pattern) {
        return $Lead
    }

    # 清單片語的探測另外組（Add-ListPhrase）；這裡給的是標頭。
    # 值與名稱的代表寫法由剖析器挑：FETCH ABSOLUTE 之後要數字，PASSWORD = 之後要字串；收不了普通名稱的格子
    # 代入那一格列得出的第一個字。等號之後一律代入列得出的值（剖析器列的或手寫的）：資料庫加密金鑰的
    # ALGORITHM = 什麼名稱都先收，整句寫完才驗，普通名稱探得過一半、整段卻剖析不過。
    # Next 是這段之後片語的下一項：只取前一段探測時（片語裡的每一個字），名稱那一格照樣看得到它後面寫什麼。
    # 後面還有項的 (* 代入左括號與 Items：清單有固定的第一項時（FORMAT_TYPE = …），之後的項才寫得出來。
    $text = $Lead
    $parts = @(($Pattern -replace ' ,\*$', '') -split ' ' | Where-Object { $_ })

    for ($index = 0; $index -lt $parts.Count; $index++) {
        $item = $parts[$index]
        $following = $index + 1 -lt $parts.Count ? $parts[$index + 1] : $Next
        $text += switch ($item) {
            '{name}' { (Select-PhraseName -Probe $text -Next $following) + ' ' }
            '{value}' { ((Select-PhraseValue -Probe $text) ?? '1') + ' ' }
            '()' { ($Group ? $Group : '(a)') + ' ' }
            '(*' { $index -lt $parts.Count - 1 ? "($Items" : '(' }
            '...' { "$Gap " }
            default { "$item " }
        }
    }

    return $text
}

# 名稱的代表寫法，理由見 Get-PhraseProbe。剖析器有的地方要看到名稱之後的字才收名稱（ALTER SERVER AUDIT a
# 之後非 TO、WITH 不可，接別的字在名稱就報錯）：片語寫了下一個字面字，名稱接上它撐得過就是名稱格。
# 只收兩段名稱的格子（擴充事件的 package.event）一段的名稱接下一個字面字就報錯，兩段的撐得過：代入兩段。
function Select-PhraseName {
    param([string]$Probe, [string]$Next)

    if (-not $Probe.EndsWith('= ') -and (Test-TakesName -Probe $Probe -Next $Next)) {
        # 下一項是括號的，看名稱撐不撐得過左括號（EVENT a.b (ACTION …)）。
        $literal = $Next -in '()', '(*' ? '(' : $Next

        if ($literal -match '^([A-Za-z_]\w*|,|\()$' -and -not (Test-NameReaches -Text "${Probe}t" -Next $literal) -and
            (Test-NameReaches -Text "${Probe}t.t" -Next $literal)) {
            return 't.t'
        }

        return 't'
    }

    $listed = @(Get-PhraseWords -Probe $Probe) + @($script:phrases.Values | Where-Object { $_.Probe -eq $Probe } | ForEach-Object { $_.Words })

    return $listed[0] ?? 't'
}

# 名稱寫成 Text 之後撐得過下一個字面字：至少一組續尾在那個字之後才報錯。只寫到那個字為止不算，
# 剖析器在檔案結尾才報（ADD EVENT t WITH 看不出 t 不夠）。
function Test-NameReaches {
    param([string]$Text, [string]$Next)

    return @($PhraseContinuations | Where-Object {
        [SqlAssistPhraseProber]::FirstRejection("$Text $Next$_") -gt "$Text $Next".Length
    }).Count -gt 0
}

# 值的代表寫法：數值或字串，取剖析器在那一格收的第一種；兩種都不收的回傳 null。
function Select-PhraseValue {
    param([string]$Probe)

    return @('1', "'x'") | Where-Object { [SqlAssistPhraseProber]::FirstRejection("$Probe$_") -gt $Probe.Length } |
        Select-Object -First 1
}

$poolArray = [string[]]@($phrasePool)
$reservedArray = [string[]]@($reserved)
$continuationArray = [string[]]@($PhraseContinuations)

function Get-PhraseWords {
    param([string]$Probe, [string[]]$Extra)

    $continuations = $Extra ? [string[]](@($continuationArray) + @($Extra)) : $continuationArray
    return [SqlAssistPhraseProber]::Probe($Probe, $poolArray, $reservedArray, $continuations, $PlainName)
}

# 語句已經完整的片語（CREATE INDEX i ON t (a) 之後）接得上的字也包括下一句的開頭；
# 那一份在這裡探一次，從那些片語裡扣掉。
$statementStarters = [System.Collections.Generic.HashSet[string]]::new(
    [string[]](Get-PhraseWords -Probe 'SELECT 1; '),
    [System.StringComparer]::OrdinalIgnoreCase)

$phrases = [ordered]@{}

# 只認位置的片語探測用的文字：每個位置的第一個樣板。
$positionPhraseProbes = @($ClausePhrases | Where-Object { -not $_['Pattern'] } |
    ForEach-Object { $_['After'] } | ForEach-Object { @($ContextTemplates[$_])[0] })

# 物件種類之後的新名字：CREATE 之後寫到哪幾個字，下一格就是新物件的名稱。執行期拿它判斷新名字的格子。
$createdKinds = [System.Collections.Generic.List[string]]::new()

# 名稱寫得出結構描述當限定字的種類（CREATE PROCEDURE dbo.p）；登入、索引、資料庫這些不行。
$schemaQualifiedKinds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# 每個片語展開過幾層。展開到已探過的一格時，只在這次的層數比較多才再往下：OPEN 的展開先走到
# OPEN SYMMETRIC KEY {name}，之後宣告的那一條要走得更深。
$phraseBudgets = @{}

function Test-Explored {
    param([string]$Key, [int]$Expand)

    return $script:phraseBudgets.Contains($Key) -and $script:phraseBudgets[$Key] -ge $Expand
}

# 這一格接得了名稱：普通名稱之後再接一個字，剖析器也不在名稱本身報錯。整句寫不寫得完不論——金鑰名稱之後
# 還要寫一長段才完整，照「寫得完」判的話 CREATE SYMMETRIC KEY 之後就不是名稱。名稱寫到檔案結尾也不夠：
# CREATE SECURITY 之後要 POLICY，剖析器要看到下一個字才在名稱報錯。
function Test-TakesName {
    param([string]$Probe, [string]$Next)

    if ([SqlAssistPhraseProber]::FirstRejection("$Probe$PlainName x") -gt $Probe.Length) {
        return $true
    }

    if ($Next -notmatch '^[A-Za-z_]') {
        return $false
    }

    return @($PhraseContinuations | Where-Object {
        [SqlAssistPhraseProber]::FirstRejection("$Probe$PlainName $Next$_") -gt $Probe.Length
    }).Count -gt 0
}

function Add-ClausePhrase {
    param(
        [string]$Pattern, [string]$Probe, [string]$After, [int]$Expand, [object[]]$Values, [object]$Closed,
        [string[]]$Borrowed, [switch]$Child, [switch]$Step, [string]$Kinds, [string[]]$ExtraEndings)

    Write-Progress -Activity '探測子句片語' -Status "$Pattern（$After）"
    $script:phraseBudgets["$After`t$Pattern"] = [Math]::Max($Expand, $script:phraseBudgets["$After`t$Pattern"] ?? -1)
    $endsStatement = [SqlAssistPhraseProber]::IsComplete($Probe.TrimEnd())
    $found = @(Get-PhraseWords -Probe $Probe -Extra $ExtraEndings)

    if ($endsStatement) {
        $found = @($found | Where-Object { -not $statementStarters.Contains($_) })
    }

    # Borrowed 是前一格寫成名稱時接得上的字：FETCH NEXT 之後的 INTO 屬於名叫 NEXT 的資料指標，
    # 不是 NEXT 帶出來的。
    if ($null -ne $Borrowed) {
        $found = @($found | Where-Object { $Borrowed -notcontains $_ })
    }

    # 展開到的一格寫到這裡已經完整、扣掉下一句的開頭又不剩字的，這一格沒有片語可說，但展開照走：
    # FETCH ABSOLUTE 本身是名叫 ABSOLUTE 的資料指標，FETCH ABSOLUTE 1 FROM 卻是另一個讀法。
    # 值、名稱與等號那一步也一樣：之後列不出字（PASSWORD = 'x' 之後），立了只是多一條空的片語。
    $silent = ($Child -and $endsStatement -or $Step) -and $found.Count -eq 0
    $words =[System.Collections.Generic.List[string]]::new([string[]]$found)

    # 手寫值還可以開一組清單（索引鍵之後的 WITH 只接 `(`）：清單項本身由那一格的位置片語列。
    foreach ($value in @($Values | Where-Object { $_ })) {
        $valueAccepted = (@($PhraseContinuations) + ' (') | Where-Object {
            [SqlAssistPhraseProber]::FirstRejection($Probe + $value + $_) -gt $Probe.Length + $value.Length + $_.Length
        } | Select-Object -First 1

        if ($null -eq $valueAccepted) {
            throw "片語「$Pattern」的手寫值 $value 剖析不過，這份清單過時了。"
        }

        if (-not $words.Contains($value)) {
            $words.Add($value)
        }
    }

    # 名稱格：接得了名稱、接不了值。接得了值的是運算式（IS NOT DISTINCT FROM 之後），名稱只是欄位的一種寫法。
    $value = Select-PhraseValue -Probe $Probe
    $takesName = $null -eq $value -and (Test-TakesName -Probe $Probe)

    # 建立的物件種類寫完了，下一格是物件的名稱。ON、AUTHORIZATION 之後是既有的物件（CREATE FULLTEXT INDEX ON t、
    # CREATE SCHEMA AUTHORIZATION u），不是這一句建立的名字。
    if ($Kinds -eq 'New' -and $takesName -and $Pattern -notmatch ' (ON|AUTHORIZATION)$') {
        $kind = $Pattern.Substring('CREATE '.Length)
        $script:createdKinds.Add($kind)

        # 兩段式名稱撐過點號之後那一段：CREATE INDEX s.ix 在點號就報錯。
        if ([SqlAssistPhraseProber]::FirstRejection("$Probe$PlainName.$PlainName x") -gt "$Probe$PlainName.".Length) {
            $null = $script:schemaQualifiedKinds.Add($kind)
        }
    }

    if (-not $silent) {
        $script:phrases["$After`t$Pattern"] = @{
            Pattern       = $Pattern
            After         = $After
            Probe         = $Probe
            # 物件種類之後的名稱要再寫一長段標頭才完整（CREATE SYMMETRIC KEY k WITH …），照寫不寫得完判的話
            # 名稱那一格被判成封閉；種類的片語改問名稱在那裡收不收。
            Closed        = $null -ne $Closed ? [bool]$Closed :
                $Kinds ? -not $takesName : -not [SqlAssistPhraseProber]::AcceptsName($Probe, $PlainName, $continuationArray)
            TakesVariable = [SqlAssistPhraseProber]::AcceptsName($Probe, $PlainVariable, $continuationArray)
            EndsStatement = $endsStatement
            Words         = @($words)
            TakesOperand  = $takesName -or $null -ne $value
        }
    }

    # 物件種類的名稱之後只列一層（CREATE TABLE t 之後的 AS、ALTER INDEX i 之後的 ON）：
    # 一路展開的話 CREATE PROCEDURE p AS 之後就是整份語句開頭。更深的標頭由各敘述自己宣告。
    # 名稱之後那一格已由只認位置的片語說了（CREATE SEQUENCE t 之後是 SequenceOption）就不立，理由同下面的展開。
    if ($Kinds -and $takesName -and $positionPhraseProbes -notcontains "${Probe}t " -and
        -not (Test-Explored -Key "$After`t$Pattern {name}" -Expand 0)) {
        Add-ClausePhrase -Pattern "$Pattern {name}" -Probe "${Probe}t " -After $After -Child
    }

    if ($Expand -le 0) {
        return
    }

    # 值與名稱也是展開的一步：字列不出它們，它們之後的字卻只有從這條路探得到
    # （FETCH ABSOLUTE 1 之後的 FROM、DECRYPTION BY ASYMMETRIC KEY k 之後的 WITH）。
    # 不算一層，也不連著展開兩個；物件種類的名稱上面已經處理過。
    if (-not $Kinds -and $Pattern -and $Pattern -notmatch '(\{value\}|\{name\}|=)$') {
        if ($null -ne $value -and -not (Test-Explored -Key "$After`t$Pattern {value}" -Expand $Expand)) {
            Add-ClausePhrase -Pattern "$Pattern {value}" -Probe "$Probe$value " -After $After -Expand $Expand -Child -Step
        }

        if ($takesName -and -not (Test-Explored -Key "$After`t$Pattern {name}" -Expand $Expand)) {
            Add-ClausePhrase -Pattern "$Pattern {name}" -Probe "${Probe}t " -After $After -Expand $Expand -Child -Step
        }
    }

    # 這一格寫普通名稱就完整的話（OPEN c），名稱之後接得上的字（FETCH c INTO）另探一次，展開時扣掉。
    $nameReading = [SqlAssistPhraseProber]::IsComplete("$Probe$PlainName") ?
        [string[]]@(Get-PhraseWords -Probe "$Probe$PlainName ") : $null

    # 等號之後列得出的字是值（AES_128、RSA_2048），值之後接的與是哪一個值無關：不逐一展開，
    # 以名稱代表往下，探測代入第一個字。
    if ($Pattern -match ' =$') {
        if ($words.Count -gt 0 -and -not (Test-Explored -Key "$After`t$Pattern {name}" -Expand ($Expand - 1))) {
            Add-ClausePhrase -Pattern "$Pattern {name}" -Probe "$Probe$($words[0]) " -After $After -Expand ($Expand - 1) -Child -Step
        }

        return
    }

    # 手寫的值也往下：剖析器把它們當名稱看，之後的字同樣只有從這條路探得到。
    foreach ($word in $words) {
        $childPattern = $Pattern ? "$Pattern $word" : $word
        $childProbe = "$Probe$word "

        # 已經探到這麼深的不再探；展開到的那一格已由只認位置的片語說了（觸發程序標頭的 WITH 之後是
        # TriggerOption）也不再立：同一件事說兩次。
        if ((Test-Explored -Key "$After`t$childPattern" -Expand ($Expand - 1)) -or $positionPhraseProbes -contains $childProbe) {
            continue
        }

        # 語句的標頭寫完了也照樣往下探：扣掉下一句的開頭還剩字的（CREATE MASTER KEY 之後的 ENCRYPTION）
        # 是這一句的下一段。子句裡的不探：WHERE a IS NOT NULL 寫完之後接什麼由位置分析說，片語只看一個樣板，
        # 立了反而藏掉那個位置其餘的字（索引篩選之後的 WITH）。普通名稱放在同一格也完整時，完整的可能只是
        # 名稱那種讀法——OPEN SYMMETRIC 也是名叫 SYMMETRIC 的資料指標，後面照樣接 KEY——扣掉名稱讀法接得上的字。
        $completes = [SqlAssistPhraseProber]::IsComplete($childProbe.TrimEnd())

        if ($completes -and $After -ne 'StatementStart' -and $null -eq $nameReading) {
            continue
        }

        $childBorrowed = $completes ? $nameReading : $null
        Add-ClausePhrase -Pattern $childPattern -Probe $childProbe -After $After -Expand ($Expand - 1) -Borrowed $childBorrowed -Child -Kinds $Kinds

        # 選項名稱之後的等號與字算同一層：ALGORITHM = 之後的 AES_256、RSA_2048 由剖析器列。
        # 接得了值的格子是運算式，那裡的等號是比較（WHERE CURRENT = 1），不是選項。
        if (-not $Kinds -and $null -eq $value -and
            [SqlAssistPhraseProber]::FirstRejection("$childProbe=") -gt $childProbe.Length -and
            -not (Test-Explored -Key "$After`t$childPattern =" -Expand ($Expand - 1))) {
            Add-ClausePhrase -Pattern "$childPattern =" -Probe "$childProbe= " -After $After -Expand ($Expand - 1) -Child -Step
        }
    }
}

# 清單裡逗號之後的字：標頭本身那個片語給第一項的字，逗號之後的字另探。第一項的寫法不只一種，
# 用過的選項剖析器不收第二次（ALTER LOGIN l WITH NAME = n, 之後沒有 NAME），所以每一種第一項各接一個逗號探一次，
# 取聯集；第一項受限的（CREATE LOGIN 只能先寫 PASSWORD）也因此只探那一種，之後的字不含它。
# 逗號之後探到新字時，取第一個新字再接一個逗號往下探，直到沒有新字：順序固定的清單（VECTOR_SEARCH 的 TABLE、
# COLUMN、SIMILAR_TO）一項只接得了下一項，只探第一項之後的話第三項以後都列不出來。順序不限的清單第二次就探不到新字；
# 每個新字都探的話，DDL 觸發程序幾百個事件各要探一次。
# 第一項沒有一種寫得完時 Probe 是 null。
function Get-ListItemWords {
    param([string]$Head, [object[]]$Firsts, [string[]]$ExtraEndings)

    $words = [System.Collections.Generic.List[string]]::new()
    $probe = $null
    $closed = $true
    $takesVariable = $false
    $followed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $pending = [System.Collections.Generic.Queue[object]]::new()

    foreach ($first in $Firsts) {
        $pending.Enqueue(@($Head, $first))
    }

    while ($pending.Count -gt 0) {
        $prefix, $item = $pending.Dequeue()

        if (-not $followed.Add($item)) {
            continue
        }

        # 這一項寫完、接得了逗號就好，整句寫不寫得完不論：對稱金鑰的 WITH 清單之後還要寫 ENCRYPTION BY。
        # 剖析器會驗的值（FORMAT_TYPE = DELIMITEDTEXT）續尾寫不出來：等號之後代入那一格列得出的第一個字，同 Get-PhraseProbe。
        # 只有那一份清單才有的寫法（VECTOR_SEARCH 的 METRIC = 'cosine'）由片語的 Endings 給。
        $endings = [System.Collections.Generic.List[string]]::new([string[]](@($PhraseContinuations) + @($ExtraEndings | Where-Object { $_ })))

        if ([SqlAssistPhraseProber]::FirstRejection("$prefix$item = ") -gt "$prefix$item ".Length) {
            $value = @(Get-PhraseWords -Probe "$prefix$item = ")[0]

            if ($value) {
                $endings.Add(" = $value")
            }
        }

        $ending = $endings | Where-Object {
            [SqlAssistPhraseProber]::FirstRejection("$prefix$item$_, ") -gt "$prefix$item$_".Length
        } | Select-Object -First 1

        # 寫不完的一項（NO 之後要 CREDENTIAL）探不出逗號之後，由別的項補。
        if ($null -eq $ending) {
            continue
        }

        $itemProbe = "$prefix$item$ending, "

        # 這一項本身已經讓剖析器報了別的錯（OPENROWSET(BULK …) 不收 BATCHSIZE），之後它不再檢查：逗號接逗號也不被拒，
        # 每個保留字都「接得上」。這種探測說明不了什麼。
        if ([SqlAssistPhraseProber]::FirstRejection("$itemProbe,") -gt $itemProbe.Length) {
            continue
        }

        $probe ??= $itemProbe
        $closed = $closed -and -not [SqlAssistPhraseProber]::AcceptsName($itemProbe, $PlainName, $continuationArray)
        $takesVariable = $takesVariable -or [SqlAssistPhraseProber]::AcceptsName($itemProbe, $PlainVariable, $continuationArray)

        $fresh = @(Get-PhraseWords -Probe $itemProbe | Where-Object { -not $words.Contains($_) })

        foreach ($word in $fresh) {
            $words.Add($word)
        }

        if ($fresh.Count -gt 0) {
            $pending.Enqueue(@($itemProbe, $fresh[0]))
        }
    }

    return @{ Probe = $probe; Closed = $closed; TakesVariable = $takesVariable; Words = $words }
}

# 清單片語（,*）：第一項由標頭的片語給，這一條只說逗號之後。
function Add-ListPhrase {
    param([string]$Pattern, [string]$Head, [string]$After)

    $headKey = "$After`t$($Pattern -replace ' ,\*$', '')"

    if (-not $script:phrases.Contains($headKey)) {
        Add-ClausePhrase -Pattern ($Pattern -replace ' ,\*$', '') -Probe $Head -After $After
    }

    $items = Get-ListItemWords -Head $Head -Firsts @($script:phrases[$headKey].Words)

    if ($null -eq $items.Probe) {
        throw "清單片語「$Pattern」的第一項沒有一種寫得完，探不出逗號之後的字（第一項：$(@($script:phrases[$headKey].Words) -join ', ')）。"
    }

    $script:phrases["$After`t$Pattern"] = @{
        Pattern       = $Pattern
        After         = $After
        Probe         = $items.Probe
        Closed        = $items.Closed
        TakesVariable = $items.TakesVariable
        EndsStatement = $false
        Words         = @($items.Words)
    }
}

# 括號清單（(*）在左括號與逗號之後都比對得上，執行期分不出是哪一個，所以字是兩者的聯集：
# OPENROWSET( 之後是 BULK，OPENROWSET(BULK 'x', 之後是 FORMAT、DATA_SOURCE。選項清單的兩份本來就相同。
# 括號裡是一個子句的（WITHIN GROUP (ORDER BY a, b)）逗號屬於子句，由 Clause 宣告不探：聯集會讓左括號之後也列出運算式的字。
function Add-OpenListItems {
    param([string]$Key, [string[]]$Endings)

    $phrase = $script:phrases[$Key]
    $items = Get-ListItemWords -Head $phrase.Probe -Firsts @($phrase.Words) -ExtraEndings $Endings

    if ($null -eq $items.Probe) {
        return
    }

    $phrase.Words = @($phrase.Words) + @($items.Words | Where-Object { @($phrase.Words) -notcontains $_ })
    $phrase.Closed = $phrase.Closed -and $items.Closed
    $phrase.TakesVariable = $phrase.TakesVariable -or $items.TakesVariable
}

# 帶 After 的片語以那個位置的第一個樣板探測（Template 另外指定的除外）：它是那個位置的代表寫法，而且是完整的語句，
# 「寫到這裡語句已經完整」的判斷才有意義。其餘樣板是第三階段為了撈齊關鍵字而加的旁支
# （FROM t JOIN y 還缺 ON），拿來探片語只會長出那條旁支才有的字，還要多花幾倍的時間。
foreach ($entry in $ClausePhrases) {
    $pattern = $entry['Pattern']
    $common = @{ Pattern = $pattern; Expand = [int]$entry['Expand']; Values = $entry['Values']; Closed = $entry['Closed']; Kinds = $entry['Kinds']; ExtraEndings = $entry['Endings'] }

    # ... 的寫法由執行期的 SqlClausePhrase 驗；探測只要有一段代入的文字。
    if (($pattern -match '\.\.\.') -ne [bool]$entry['Gap']) {
        throw "片語「$pattern」的 ... 與 Gap 要一起寫：Gap 是探測時代入 ... 的文字。"
    }

    if ($pattern -match '\.\.\.' -and $entry['Expand']) {
        throw "片語「$pattern」有 ... 就不收 Expand：展開出來的片語照樣要 Gap，逐條寫清楚。"
    }

    if ($null -ne $entry['Lead']) {
        if ($null -ne $entry['After']) {
            throw "片語「$pattern」的 Lead 與 After 只能寫一個。"
        }

        if (-not $pattern) {
            throw '沒有尾巴的片語只能以 After 交代位置：執行期不看前一格的話，它哪裡都成立。'
        }

        if ($pattern -match ',\*') {
            throw "清單片語「$pattern」要以 After 交代位置：位置分析拿標頭認清單，得判得出標頭前一格。"
        }

        Add-ClausePhrase @common -Probe (Get-PhraseProbe -Lead $entry['Lead'] -Pattern $pattern -Group $entry['Group'] -Gap $entry['Gap'] -Items $entry['Items']) -After 'Any'

        if ($pattern -match ' \(\*$' -and -not $entry['Clause'] -and $phrases.Contains("Any`t$pattern")) {
            Add-OpenListItems -Key "Any`t$pattern" -Endings $entry['Endings']
        }

        continue
    }

    foreach ($position in @($entry['After'] ?? 'StatementStart')) {
        if (-not $ContextTemplates.Contains($position)) {
            throw "片語「$pattern」的 After 寫了不存在的位置 $position。"
        }

        $probe = Get-PhraseProbe -Lead @($ContextTemplates[$position])[[int]$entry['Template']] -Pattern $pattern -Group $entry['Group'] -Gap $entry['Gap'] -Items $entry['Items']

        if ($pattern -match ' ,\*$') {
            if ($entry['Expand'] -or $entry['Values'] -or $null -ne $entry['Closed']) {
                throw "清單片語「$pattern」的字全由探測決定，不收 Expand、Values、Closed。"
            }

            Add-ListPhrase -Pattern $pattern -Head $probe -After $position
            continue
        }

        Add-ClausePhrase @common -Probe $probe -After $position

        if ($pattern -match ' \(\*$' -and -not $entry['Clause'] -and $phrases.Contains("$position`t$pattern")) {
            Add-OpenListItems -Key "$position`t$pattern" -Endings $entry['Endings']
        }
    }
}

# 片語裡的每一個字，由它前面那段列出：寫得出 CREATE OR ALTER，CREATE 之後就要有 OR、
# CREATE OR 之後就要有 ALTER。逐字探測問不出這種字——剖析器要看到整段才收，CREATE OR
# 接任何續尾都在 CREATE 就報錯——但整條片語剖析得過本身就是證據。
#
# 前面那段已經是片語就把字補進去。還不是的另立一個，條件是那段尾巴認得出來：Lead 片語以字面字或等號結尾
# （以名稱或值結尾的一段，前一個字之後什麼都可能接，立了會封閉掉不相干的清單），而且至少兩項——
# 執行期不看 Lead 的前一格，單獨一個 ON、NEXT 到處都比對得上。單獨一個不是關鍵字的（GENERATED）立得起來但不封閉：
# 它也可能是名稱，比對到只把字加進那一格的目錄。帶位置的片語從那個位置寫起，已經釘住了，
# 以名稱結尾的一段也立得起來：ALGORITHM = AES_128 之後的 ENCRYPTION 剖析器當名稱讀，只有整段是證據。
# 以 ...、括號或清單結尾的一段不立：那些元素要夾在字中間才比對得了。
# 帶 After 的片語，第一個字前面那段是位置本身：那個位置有只認位置的片語就補進去
# （函式 WITH 之後的 RETURNS、CALLED），沒有的由關鍵字目錄給。目錄也不給的（AT、ENABLE 不是
# 關鍵字）收進那個位置的附加片語：只加字、比對永遠是「可能」，那一格其餘的字照樣由目錄給——
# 立成一般的只認位置片語的話，比對確定時整份目錄讓給它，選取清單尾端只剩 AT。
$additivePhrases = [ordered]@{}
function Test-PatternAccepted {
    param([string]$Probe)

    foreach ($continuation in $PhraseContinuations) {
        if ([SqlAssistPhraseProber]::FirstRejection($Probe + $continuation) -ge $Probe.Length) {
            return $true
        }
    }

    return $false
}

foreach ($entry in $ClausePhrases) {
    $items = @($entry['Pattern'] -split ' ' | Where-Object { $_ })
    $lead = $entry['Lead']

    if ($items.Count -lt ($null -ne $lead ? 2 : 1)) {
        continue
    }

    foreach ($position in ($null -ne $lead ? @('Any') : @($entry['After'] ?? 'StatementStart'))) {
        $leadText = $lead ?? @($ContextTemplates[$position])[[int]$entry['Template']]

        $wholeProbe = Get-PhraseProbe -Lead $leadText -Pattern $entry['Pattern'] -Group $entry['Group'] -Gap $entry['Gap'] -Items $entry['Items']

        if (-not (Test-PatternAccepted -Probe $wholeProbe)) {
            throw "片語「$($entry['Pattern'])」整段剖析不過（$wholeProbe），拿它補前面那段的字沒有根據。"
        }

        for ($index = 0; $index -lt $items.Count; $index++) {
            $word = $items[$index]

            if ($word -notmatch '^[A-Za-z_]') {
                continue
            }

            # Lead 片語的第一個字前面那一格判不出位置。關鍵字在那裡本來就全部進場，其餘的字（GENERATED）收進
            # 只在判不出位置時出現的附加片語：與產生器判不出位置的關鍵字（None）同一條規則。
            # Lead 那一段已經有片語列得出的（索引鍵之後的 INCLUDE）不必。
            if ($null -ne $lead -and $index -eq 0) {
                $listed = $phrases.Values | Where-Object { $_.Probe -eq $leadText -and $_.Words -contains $word }

                if ($keywords -notcontains $word -and -not $listed) {
                    if (-not $additivePhrases.Contains('None')) {
                        $additivePhrases['None'] = @{ Probe = $leadText; Words = [System.Collections.Generic.List[string]]::new() }
                    }

                    if (-not $additivePhrases['None'].Words.Contains($word)) {
                        $additivePhrases['None'].Words.Add($word)
                    }
                }

                continue
            }

            $prefix = $index -eq 0 ? '' : $items[0..($index - 1)] -join ' '
            $key = "$position`t$prefix"
            $prefixProbe = Get-PhraseProbe -Lead $leadText -Pattern $prefix -Group $entry['Group'] -Gap $entry['Gap'] -Items $entry['Items'] -Next $word

            # Lead 片語的鍵不含 Lead：視窗框架的 ROWS 與 OFFSET 之後的 ROWS 同一個鍵，墊的文字不同就是別的片語。
            if ($phrases.Contains($key) -and $phrases[$key].Probe -ne $prefixProbe) {
                continue
            }

            # 前面那段已經有片語列得出這個字（CREATE 之後的物件種類），就不必另外附加。
            if ($index -eq 0 -and -not $phrases.Contains($key)) {
                $listed = $phrases.Values | Where-Object { $_.Probe -eq $prefixProbe -and $_.Words -contains $word }

                if (@($positions[$word]) -notcontains $position -and -not $listed) {
                    if (-not $additivePhrases.Contains($position)) {
                        $additivePhrases[$position] = @{ Probe = $prefixProbe; Words = [System.Collections.Generic.List[string]]::new() }
                    }

                    if (-not $additivePhrases[$position].Words.Contains($word)) {
                        $additivePhrases[$position].Words.Add($word)
                    }
                }

                continue
            }

            # 探測文字相同就是同一格：DdlObject 的 TRIGGER {name} 與 CREATE TRIGGER {name} 都是 CREATE TRIGGER t，
            # 字補進已經有的那一個，不另立一個互相搶比對。
            if (-not $phrases.Contains($key)) {
                $key = @($phrases.Keys | Where-Object { $phrases[$_].Probe -eq $prefixProbe })[0] ?? $key
            }

            if (-not $phrases.Contains($key)) {
                $ending = $null -ne $lead ? '^([A-Za-z_]|=$)' : '^([A-Za-z_]|=$|\{name\}$)'

                $single = $null -ne $lead -and $index -lt 2

                if ($items[$index - 1] -notmatch $ending -or ($single -and $keywords -contains $prefix)) {
                    continue
                }

                Add-ClausePhrase -Pattern $prefix -Probe $prefixProbe -After $position -Closed ($single ? $false : $null)
            }

            if ($phrases[$key].Words -notcontains $word) {
                $phrases[$key].Words = @($phrases[$key].Words) + $word
            }
        }
    }
}

# 語句說明登錄的名稱與別名也是證據：寫得出 DBCC CHECKDB，DBCC 之後就要有 CHECKDB。
# 剖析器在那一格什麼名稱都收時（DBCC 的命令）探測問不出字，說明是唯一的名單；清單列得出的字
# 也就一定對得到說明。只補進已經有的片語：前面那段沒有片語的字由關鍵字目錄給（BEGIN TRY 的 TRY），
# 為它另立一個會把那一格其餘的字封閉掉。整段在語句開頭剖析不過的（END TRY 要在區塊裡）不算證據。
$statementDocs = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\SqlAssist.Core\Keywords\BuiltInDocs\statements.json') -Raw -Encoding utf8 |
    ConvertFrom-Json).docs
$statementNames = $statementDocs |
    Where-Object kind -eq 'statement' |
    ForEach-Object { @($_.name) + @($_.aliases) } |
    Where-Object { $_ -match ' ' }

foreach ($name in $statementNames) {
    $items = @($name -split ' ')

    if (-not (Test-PatternAccepted -Probe (Get-PhraseProbe -Lead '' -Pattern $name))) {
        Write-Host "語句說明的名稱在語句開頭剖析不過，不當證據：$name"
        continue
    }

    for ($index = 1; $index -lt $items.Count; $index++) {
        $key = "StatementStart`t$($items[0..($index - 1)] -join ' ')"

        if ($phrases.Contains($key) -and $phrases[$key].Words -notcontains $items[$index]) {
            $phrases[$key].Words = @($phrases[$key].Words) + $items[$index]
        }
    }
}

# DBCC 命令括號裡的關鍵字（CHECKIDENT 的 RESEED、CHECKDB 的 REPAIR_REBUILD）也只有說明列得出來：剖析器在括號裡
# 什麼名稱都收。語法照 T-SQL 的語法慣例，關鍵字大寫、要填的值小寫；說明裡以「DBCC 命令」開頭的每一格
# （簽章的每一行、對照表每一列的每一格）都是一種寫法，第一組括號裡的大寫字就是名單，引號裡的字不算。
# 預覽的「命令」對照表一列寫一個命令，所以每個命令都要有一種寫法：少了就是預覽漏了那個命令。
# 片語「DBCC 命令 (*」給這些字；括號裡照樣可以寫名稱與數值，不封閉。
# 字不分是第幾個引數：每一格都不封閉，多出來的只是幾個字，分格的話每個命令的引數順序都要另外寫一份。
$dbccDoc = $statementDocs | Where-Object { $_.kind -eq 'statement' -and $_.name -eq 'DBCC' }
$dbccSyntax = @(@($dbccDoc.signature -split "`n") + @($dbccDoc.references | ForEach-Object { $_.rows } | ForEach-Object { $_ }) |
    Where-Object { $_ -cmatch '^DBCC [A-Z]' })
$dbccArguments = [ordered]@{}

foreach ($line in $dbccSyntax) {
    $null = $line -cmatch '^DBCC (?<command>[A-Z][A-Z0-9_]*)'
    $command = $Matches['command']

    if (@($dbccDoc.aliases) -notcontains "DBCC $command") {
        throw "DBCC 說明寫了 $command，別名卻沒有 DBCC $command：寫法與命令名單要一致。"
    }

    $dbccArguments[$command] ??= [System.Collections.Generic.List[string]]::new()
    $open = $line.IndexOf('(')

    if ($line -cnotmatch '^DBCC [A-Z0-9_]+ [\[ ]*\(') {
        continue
    }

    # 從第一個左括號走到配對的右括號。
    $depth = 0
    $close = -1

    for ($index = $open; $index -lt $line.Length -and $close -lt 0; $index++) {
        switch ($line[$index]) {
            '(' { $depth++ }
            ')' { if (--$depth -eq 0) { $close = $index } }
        }
    }

    if ($close -lt 0) {
        throw "DBCC $command 的語法括號沒有關上：$line"
    }

    $inside = $line.Substring($open + 1, $close - $open - 1) -replace "'[^']*'", ''

    foreach ($argument in [regex]::Matches($inside, '\b[A-Z][A-Z0-9_]*\b') | ForEach-Object Value) {
        if (-not $dbccArguments[$command].Contains($argument)) {
            $dbccArguments[$command].Add($argument)
        }
    }
}

$undocumented = @($dbccDoc.aliases | Where-Object { -not $dbccArguments.Contains(($_ -replace '^DBCC ', '')) })

if ($undocumented.Count -gt 0) {
    throw "DBCC 說明沒有寫出這些命令的語法，預覽的命令對照表漏了它們：$($undocumented -join ', ')"
}

foreach ($command in $dbccArguments.Keys) {
    if ($dbccArguments[$command].Count -gt 0) {
        $pattern = "DBCC $command (*"
        Add-ClausePhrase -Pattern $pattern -Probe (Get-PhraseProbe -Lead '' -Pattern $pattern) -After 'StatementStart' -Values @($dbccArguments[$command]) -Closed $false
    }
}

Write-Progress -Activity '探測子句片語' -Completed

# 唯一接得下去的字併成一項：ASYMMETRIC 之後只有 KEY、ENCRYPTION 之後只有 BY，清單列的就是
# ASYMMETRIC KEY、ENCRYPTION BY PASSWORD，選一次寫完。條件是那個字寫到這裡還沒完整、封閉、接不了名稱或值，
# 而它之後正好一個字；那個字照同一條規則再往下併。中間每一段的片語照舊：一個字一個字打的人看到的是同一條路。
function Get-PhraseChain {
    param([string]$After, [string]$Pattern, [string]$Word)

    $key = "$After`t$($Pattern ? "$Pattern $Word" : $Word)"

    if (-not $phrases.Contains($key)) {
        return $Word
    }

    $next = $phrases[$key]

    if ($next.EndsStatement -or -not $next.Closed -or $next.TakesOperand -or @($next.Words).Count -ne 1) {
        return $Word
    }

    return "$Word $(Get-PhraseChain -After $After -Pattern $next.Pattern -Word @($next.Words)[0])"
}

$chained = [ordered]@{}

foreach ($key in $phrases.Keys) {
    $phrase = $phrases[$key]
    $chained[$key] = @($phrase.Words | ForEach-Object { Get-PhraseChain -After $phrase.After -Pattern $phrase.Pattern -Word $_ })
}

foreach ($key in $chained.Keys) {
    $phrases[$key].Words = $chained[$key]
}

# 同一條尾巴在幾個位置上探到一模一樣的結果時併成一個片語，位置取聯集；結果不同的
# （資料表之後的 FOR 多一個 SYSTEM_TIME）各自一個，執行期由前一格的位置分開。
$merged = [ordered]@{}

foreach ($phrase in $phrases.Values) {
    $key = "$($phrase.Pattern)`t$($phrase.Closed)`t$($phrase.TakesVariable)`t$($phrase.EndsStatement)`t$($phrase.Words -join ' ')"

    if ($merged.Contains($key)) {
        $merged[$key].After.Add($phrase.After)
        continue
    }

    $merged[$key] = @{
        Pattern       = $phrase.Pattern
        After         = [System.Collections.Generic.List[string]]::new([string[]]@($phrase.After))
        Probe         = $phrase.Probe
        Closed        = $phrase.Closed
        TakesVariable = $phrase.TakesVariable
        EndsStatement = $phrase.EndsStatement
        Words         = $phrase.Words
    }
}

$phrases = $merged
$phraseWords = $phrases.Values | ForEach-Object { $_.Words } | Where-Object { $keywords -notcontains $_ } | Sort-Object -Unique
Write-Host "子句片語：$($phrases.Count) 個，其中關鍵字清單以外的字 $(@($phraseWords).Count) 個"
Write-Host "附加片語：$(($additivePhrases.Keys | ForEach-Object { "$_($($additivePhrases[$_].Words -join ', '))" }) -join ', ')"

# ------------------------------------------------------------ 五、寫完一項的字

# NULL、CURRENT_USER 本身就是完整的運算元，DESC 寫完 ORDER BY 的一項：這些字之後的位置
# 與識別字之後相同，由往回找到的子句決定。判法是任一個樣板接上它就是完整的一句、
# 而以它結尾的是語句裡的一項；非保留字照第三階段的規則，普通名稱接上去也成立的不算。
$itemEndings = @($keywords | Where-Object {
    $keyword = $_
    $canBeName = $reserved -notcontains $keyword

    foreach ($name in $positionNames) {
        foreach ($prefix in $ContextTemplates[$name]) {
            if ([SqlAssistPhraseProber]::EndsItem($prefix, $keyword) -and
                -not ($canBeName -and [SqlAssistPhraseProber]::EndsItem($prefix, $PlainName))) {
                return $true
            }
        }
    }

    return $false
})

Write-Host "寫完一項的字：$($itemEndings -join ', ')"

# ------------------------------------------------------------ 六、寫完一句的字

# 第五階段排除的那一半：BREAK、COMMIT、BEGIN TRAN 的 TRAN 寫完的是語句本身。分析器拿它們
# 認語句的界線，前一格落在「收得乾淨」的位置時，之後直接是下一句的開頭。
# 收得乾淨是指那一句再也接不了語句開頭以外的東西：COMMIT 還接 TRAN、RETURN 還接運算式、
# BEGIN TRAN 還接交易名稱的變數，把它們判成語句開頭就把這些字藏起來了。
# 每個位置以第一個接得上的樣板為準；非保留字照第三階段的規則，普通名稱也寫得完一句的不算。
$keywordArray = [string[]]@($keywords)
$continuationArrayForKeywords = [string[]]@($Continuations)

$statementEndings = [ordered]@{}

foreach ($keyword in $keywords) {
    $canBeName = $reserved -notcontains $keyword
    $closes = [System.Collections.Generic.List[string]]::new()
    $ends = $false

    foreach ($name in $positionNames) {
        $prefix = @($ContextTemplates[$name]) | Where-Object {
            [SqlAssistPhraseProber]::EndsStatement($_, $keyword) -and
                -not ($canBeName -and [SqlAssistPhraseProber]::EndsStatement($_, $PlainName))
        } | Select-Object -First 1

        if ($null -eq $prefix) {
            continue
        }

        $ends = $true
        $probe = "$prefix$keyword "
        $followers = [SqlAssistPhraseProber]::Probe($probe, $keywordArray, $reservedArray, $continuationArrayForKeywords, $PlainName)
        $hidden = @($followers | Where-Object { $positions[$_] -notcontains 'StatementStart' })
        $takesVariable = [SqlAssistPhraseProber]::FirstRejection("$probe@v") -gt $probe.Length + 2

        if ($hidden.Count -eq 0 -and -not $takesVariable) {
            $closes.Add($name)
        }
    }

    if ($ends) {
        $statementEndings[$keyword] = $closes
    }
}

Write-Host "寫完一句的字：$(($statementEndings.Keys | ForEach-Object { "$_($($statementEndings[$_] -join '|'))" }) -join ', ')"

# ---------------------------------------------------------------------- 產出

$builder = [System.Text.StringBuilder]::new()
$null = $builder.AppendLine('// <auto-generated />')
$null = $builder.AppendLine('//')
$null = $builder.AppendLine('// 由 tools/Generate-Keywords.ps1 產生，請勿手動編輯。')
$null = $builder.AppendLine("// 來源：Microsoft.SqlServer.TransactSql.ScriptDom $scriptDomVersion（$($parserType.Name)）")
$null = $builder.AppendLine('//')
$null = $builder.AppendLine('// 關鍵字取自 TSqlTokenType 的成員名稱並以 tokenizer 回驗，')
$null = $builder.AppendLine("// 另加腳本裡 `$NonReservedSupplement 的 $($NonReservedSupplement.Count) 個非保留字；")
$null = $builder.AppendLine('// 位置則是把每個關鍵字塞進樣板剖析、依錯誤碼判定得到的。')
$null = $builder.AppendLine('//')
$null = $builder.AppendLine('// 保留字是另外探測的一份：把字塞進識別字的洞裡，剖析器拒收的才算，')
$null = $builder.AppendLine('// 因此它與上面的關鍵字清單互有出入——兩邊都有對方沒有的字。')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('using System.Collections.Generic;')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('namespace SqlAssist.Core.Keywords;')
$null = $builder.AppendLine('')
# 刻意不做成 SqlKeywordCatalog 的 partial：同一個類別的靜態欄位若分散在兩個檔案，
# 初始化順序由編譯順序決定，SqlKeywordCatalog 的衍生字典就可能在資料還是 null 時先跑。
# 拆成獨立類別之後，跨類別的靜態初始化由「第一次存取」觸發，順序才有保證。
$null = $builder.AppendLine('/// <summary>產生出來的關鍵字資料；請由 <see cref="SqlKeywordCatalog"/> 取用。</summary>')
$null = $builder.AppendLine('internal static class SqlKeywordCatalogData')
$null = $builder.AppendLine('{')
$null = $builder.AppendLine("    /// <summary>產生這份目錄所用的 ScriptDom 版本。</summary>")
$null = $builder.AppendLine("    internal const string SourceVersion = `"$scriptDomVersion`";")
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>全部關鍵字，以及各自可以出現的位置。</summary>')
$null = $builder.AppendLine('    internal static readonly KeyValuePair<string, SqlKeywordPosition>[] Keywords =')
$null = $builder.AppendLine('    {')

foreach ($keyword in $keywords) {
    $allowed = $positions[$keyword]
    $flags = if ($allowed.Count -eq 0) {
        'SqlKeywordPosition.None'
    }
    else {
        ($allowed | ForEach-Object { "SqlKeywordPosition.$_" }) -join ' | '
    }

    $null = $builder.AppendLine("        new(`"$keyword`", $flags),")
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>定位置所用的樣板：每個位置與它的樣板文字。</summary>')
$null = $builder.AppendLine('    /// <remarks>')
$null = $builder.AppendLine('    /// 執行期用不到，輸出來是為了讓測試逐條回驗：分析器對樣板文字回報的位置')
$null = $builder.AppendLine('    /// 必須含那個位置，兩邊說的才是同一個位置。')
$null = $builder.AppendLine('    /// </remarks>')
$null = $builder.AppendLine('    internal static readonly KeyValuePair<SqlKeywordPosition, string>[] Templates =')
$null = $builder.AppendLine('    {')

foreach ($name in $positionNames) {
    foreach ($prefix in $ContextTemplates[$name]) {
        $literal = $prefix.Replace('\', '\\').Replace('"', '\"')
        $null = $builder.AppendLine("        new(SqlKeywordPosition.$name, `"$literal`"),")
    }
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>不能直接當識別字書寫、插入時一定要加方括號的字。</summary>')
$null = $builder.AppendLine('    internal static readonly string[] ReservedIdentifiers =')
$null = $builder.AppendLine('    {')

$line = '       '

foreach ($keyword in $reserved) {
    $entry = " `"$keyword`","

    if ($line.Length + $entry.Length -gt 96) {
        $null = $builder.AppendLine($line)
        $line = '       '
    }

    $line += $entry
}

if ($line.Trim().Length -gt 0) {
    $null = $builder.AppendLine($line)
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>本身就把前一格開的那一項寫完的字（NULL、DESC）。</summary>')
$null = $builder.AppendLine('    internal static readonly string[] ItemEndings =')
$null = $builder.AppendLine('    {')

$line = '       '

foreach ($keyword in $itemEndings) {
    $entry = " `"$keyword`","

    if ($line.Length + $entry.Length -gt 96) {
        $null = $builder.AppendLine($line)
        $line = '       '
    }

    $line += $entry
}

if ($line.Trim().Length -gt 0) {
    $null = $builder.AppendLine($line)
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>寫完一整句的字，以及前一格在哪些位置時寫完那一句就只剩下一句可接。</summary>')
$null = $builder.AppendLine('    internal static readonly KeyValuePair<string, SqlKeywordPosition>[] StatementEndings =')
$null = $builder.AppendLine('    {')

foreach ($keyword in $statementEndings.Keys) {
    $closes = $statementEndings[$keyword]
    $flags = if ($closes.Count -eq 0) {
        'SqlKeywordPosition.None'
    }
    else {
        ($closes | ForEach-Object { "SqlKeywordPosition.$_" }) -join ' | '
    }

    $null = $builder.AppendLine("        new(`"$keyword`", $flags),")
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>子句片語：游標前的尾巴、探測文字、是否封閉、是否收變數、語句到那裡是否已經完整，以及那裡接得上的字。</summary>')
$null = $builder.AppendLine('    /// <remarks>')
$null = $builder.AppendLine('    /// 探測文字執行期用不到，輸出來是為了讓測試逐條回驗：片語比對對那段文字')
$null = $builder.AppendLine('    /// 必須認出同一個片語，兩邊說的才是同一個位置。')
$null = $builder.AppendLine('    /// </remarks>')
$null = $builder.AppendLine('    internal static readonly (string Pattern, SqlKeywordPosition After, string Probe, bool Closed, bool TakesVariable, bool EndsStatement, string[] Words)[] ClausePhrases =')
$null = $builder.AppendLine('    {')

foreach ($phrase in $phrases.Values) {
    $afterLiteral = ($phrase.After | ForEach-Object { "SqlKeywordPosition.$_" }) -join ' | '
    $probeLiteral = $phrase.Probe.Replace('\', '\\').Replace('"', '\"')
    $closedLiteral = $phrase.Closed ? 'true' : 'false'
    $variableLiteral = $phrase.TakesVariable ? 'true' : 'false'
    $endsLiteral = $phrase.EndsStatement ? 'true' : 'false'
    $null = $builder.AppendLine("        (`"$($phrase.Pattern)`", $afterLiteral, `"$probeLiteral`", $closedLiteral, $variableLiteral, $endsLiteral, new string[]")
    $null = $builder.AppendLine('        {')

    $line = '           '

    foreach ($word in $phrase.Words) {
        $entry = " `"$word`","

        if ($line.Length + $entry.Length -gt 96) {
            $null = $builder.AppendLine($line)
            $line = '           '
        }

        $line += $entry
    }

    if ($line.Trim().Length -gt 0) {
        $null = $builder.AppendLine($line)
    }

    $null = $builder.AppendLine('        }),')
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>附加片語：只認位置、比對永遠是「可能」，把關鍵字目錄給不了的片語開頭加進那個位置。</summary>')
$null = $builder.AppendLine('    internal static readonly (SqlKeywordPosition After, string Probe, string[] Words)[] AdditivePhrases =')
$null = $builder.AppendLine('    {')

foreach ($position in $additivePhrases.Keys) {
    $additive = $additivePhrases[$position]
    $probeLiteral = $additive.Probe.Replace('\', '\\').Replace('"', '\"')
    $wordsLiteral = ($additive.Words | ForEach-Object { "`"$_`"" }) -join ', '
    $null = $builder.AppendLine("        (SqlKeywordPosition.$position, `"$probeLiteral`", new string[] { $wordsLiteral }),")
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('')
$null = $builder.AppendLine('    /// <summary>')
$null = $builder.AppendLine('    /// CREATE 之後寫到這幾個字，下一格是物件的名稱；AlsoWords 是那一格除了名稱還接不接得上片語的字')
$null = $builder.AppendLine('    /// （CREATE DATABASE 之後還有 SCOPED），SchemaQualified 是名稱寫不寫得出結構描述（CREATE PROCEDURE dbo.p）。')
$null = $builder.AppendLine('    /// </summary>')
$null = $builder.AppendLine('    internal static readonly (string Kind, bool AlsoWords, bool SchemaQualified)[] CreatedKinds =')
$null = $builder.AppendLine('    {')

# 那一格除了名稱還接不接得上別的字，要等片語全部補完才知道：CREATE DATABASE 之後的 ENCRYPTION 是
# CREATE DATABASE ENCRYPTION KEY 那一條補的。
foreach ($kind in $createdKinds) {
    $alsoWords = @($phrases.Values | Where-Object { $_.Pattern -eq "CREATE $kind" -and $_.After -contains 'StatementStart' -and @($_.Words).Count -gt 0 }).Count -gt 0
    $schemaQualified = $schemaQualifiedKinds.Contains($kind)
    $null = $builder.AppendLine("        (`"$kind`", $($alsoWords ? 'true' : 'false'), $($schemaQualified ? 'true' : 'false')),")
}

$null = $builder.AppendLine('    };')
$null = $builder.AppendLine('}')

$resolved = [System.IO.Path]::GetFullPath($OutputPath)
$output = $builder.ToString().Replace("`r`n", "`n").Replace("`r", "`n")
[System.IO.File]::WriteAllText($resolved, $output, [System.Text.UTF8Encoding]::new($false))

Write-Host "已寫出 $resolved"

[SqlAssistPhraseProber]::SaveCache($true)
Write-Host "$([SqlAssistPhraseProber]::CacheSummary())，快取：$resolvedCachePath"
