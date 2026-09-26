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

.PARAMETER SsmsInstallDir
    SSMS 22 安裝路徑。ScriptDom 隨 SSMS 附帶，不必另外安裝。

.PARAMETER OutputPath
    產出的 .cs 檔路徑。

.NOTES
    產物要進版控。SqlAssist.Core 是 netstandard2.0 且刻意零相依，建置時不會、
    也不該去碰 SSMS 的組件，所以這支腳本是手動執行、結果 commit 進去。
#>
[CmdletBinding()]
param(
    [string]$SsmsInstallDir,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\SqlAssist.Core\Keywords\SqlKeywordCatalog.Generated.cs')
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
        'INSERT INTO t ', 'GRANT SELECT ON t ', 'MERGE INTO t ')

    # WHERE CURRENT OF 只有 UPDATE 與 DELETE 寫得出來。
    Predicate        = @('SELECT * FROM t WHERE ', 'DELETE FROM t WHERE ')

    # 兩個都要：WHERE a 之後是 IN、IS、LIKE、BETWEEN，
    # WHERE a = 1 之後才是 AND、OR 與後續子句。分析器一樣分不出來。
    # LIKE 的樣式寫完之後同樣回報這個位置，ESCAPE 只在那裡。
    # 第一個是代表寫法，子句片語拿它探測。
    ExpressionTail   = @(
        'SELECT * FROM t WHERE a = 1 ', 'SELECT * FROM t WHERE a ',
        "SELECT * FROM t WHERE a LIKE 'x' ")
    # OFFSET 10 之後的 ROWS、視窗函式 OVER (ORDER BY a 之後的 ROWS／RANGE 也在這裡。
    OrderByTail      = @(
        'SELECT * FROM t ORDER BY a ', 'SELECT * FROM t ORDER BY a OFFSET 10 ',
        'SELECT SUM(a) OVER (ORDER BY a ')

    # GROUP BY 的欄位之後：HAVING、ORDER 與 WITH ROLLUP，不接 ASC、DESC。
    GroupByTail      = @('SELECT * FROM t GROUP BY a ')

    # 欄位本身的位置。兩個都要：ORDER BY 接得了 ASC／DESC 以外的運算式關鍵字
    # （CASE、CONVERT、IIF），GROUP BY 接得了 ROLLUP、CUBE、GROUPING SETS。
    OrderByColumn    = @('SELECT * FROM t ORDER BY ', 'SELECT * FROM t GROUP BY ')

    ByAnchor         = @('SELECT * FROM t ORDER ', 'SELECT * FROM t GROUP ')

    # ALTER TABLE 的三個位置。少了它們，這三處一律回 Any，於是整份關鍵字目錄
    # 與所有片段全部進場——而成熟的補全工具在 ADD 之後只給九個字。
    AlterTableAction = @('ALTER TABLE t ')
    AlterTableAdd    = @('ALTER TABLE t ADD ')
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
    ' VALUE FOR s'
)

# 46010 = "'X' 附近的語法不正確"。出現在關鍵字結尾之前代表剖析器根本吃不下它。
# 46005 = "必須是 X，但卻發現 Y"。ORDER BY a Lib_Reader 1 報的是這一條而不是 46010，
#         不算進來的話任何名稱都「接受」，非保留字的 OFFSET 就分不出來。
# 46014 = "Default 條件約束只可存在於資料行層級"。剖析器吃得下 CREATE TABLE t (DEFAULT
#         卻另外報這一條，不算進來的話 DEFAULT 會被分到資料行定義的開頭。
# 46029 = "出現未預期的檔案結尾"，代表吃下去了、只是語句沒寫完，那是合法的。
$RejectingErrorNumbers = @(46005, 46010, 46014)

# 非保留字的對照名稱：不是任何關鍵字的普通識別字。
$PlainName = 'Lib_Reader'

# $Whole：整段都要過，不只到這個字為止。
function Test-Accepted {
    param([string]$Prefix, [string]$Word, [string]$Continuation, [bool]$Whole)

    $limit = $Prefix.Length + $Word.Length

    if ($Whole) {
        $limit += $Continuation.Length
    }

    $reader = [System.IO.StringReader]::new($Prefix + $Word + $Continuation)
    $errors = $null
    $null = $parser.Parse($reader, [ref]$errors)

    foreach ($error in $errors) {
        if ($RejectingErrorNumbers -contains $error.Number -and $error.Offset -le $limit) {
            return $false
        }
    }

    return $true
}

# 非保留字（APPLY、NOLOCK、GO…）當名字寫也合法，所以任何接受名稱的位置都「接受」它們：
# CREATE TABLE t ( 之後的 NOLOCK 只是一個叫 NOLOCK 的資料行。一條規則分開兩種情形，
# 不分位置：同一組續尾換成普通名稱也過的話，那一次只證明它能當名字，不算數；
# 普通名稱過不了而它過得了，才是它以關鍵字的身分屬於這個位置（BEGIN TRY）。
# 這一比看的是整段而不只到字為止：SELECT Lib_Reader VALUE FOR s 在名稱之後才出錯，
# 只看到名稱為止的話它也「過」，NEXT VALUE FOR 就分不出來。
# 保留字不必比：它們當不了名字，被接受就一定是以關鍵字的身分。
function Test-KeywordAllowed {
    param([string]$Prefix, [string]$Keyword, [bool]$CanBeName)

    foreach ($continuation in $Continuations) {
        $accepted = if ($CanBeName) {
            (Test-Accepted -Prefix $Prefix -Word $Keyword -Continuation $continuation -Whole $true) -and
                -not (Test-Accepted -Prefix $Prefix -Word $PlainName -Continuation $continuation -Whole $true)
        }
        else {
            Test-Accepted -Prefix $Prefix -Word $Keyword -Continuation $continuation -Whole $false
        }

        if ($accepted) {
            return $true
        }
    }

    return $false
}

$positionNames = @($ContextTemplates.Keys)
$positions = @{}
$counts = [ordered]@{}

foreach ($name in $positionNames) {
    $counts[$name] = 0
}

$index = 0

foreach ($keyword in $keywords) {
    $index++
    Write-Progress -Activity '分類關鍵字位置' -Status $keyword -PercentComplete (100 * $index / $keywords.Count)

    $allowed = [System.Collections.Generic.List[string]]::new()
    $canBeName = $reserved -notcontains $keyword

    foreach ($name in $positionNames) {
        foreach ($prefix in $ContextTemplates[$name]) {
            if (Test-KeywordAllowed -Prefix $prefix -Keyword $keyword -CanBeName $canBeName) {
                $allowed.Add($name)
                $counts[$name]++
                break
            }
        }
    }

    $positions[$keyword] = $allowed
}

Write-Progress -Activity '分類關鍵字位置' -Completed

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
#   {name}   一個名稱單位，可以含點號與方括號；保留字也算（ALTER INDEX ALL、ALTER DATABASE CURRENT）
#   {value}  一個數值、字串、變數，或一整組括號
#   ()       一整組括號
#   (*       還沒關上的左括號清單，游標在左括號或逗號之後；只能是最後一項
#
# 片語前面那一格由 After 與 Lead 二選一交代：
#   After  片語第一個字前面的位置，名稱取自 $ContextTemplates。探測用那些位置的樣板，
#          執行期也只在前一格是這些位置時才算數——同一條尾巴在不同位置是不同的意思
#          （查詢之後的 FOR 接 XML，UPDATE t SET 的 SET 不是選項的 SET）。
#          兩個都不寫就是 StatementStart：沒有 Lead 的片語都從一句的開頭寫起。
#   Lead   尾巴本身就認得出意思、只是探測時要墊的文字；執行期不看前一格。
# Expand 往下再探幾層：每個接得上的字接在片語後面成為新的片語，直到語句完整為止。
# Values 是剖析器分不出來、只能手寫的字，一樣要剖析得過才收：SET DATEFORMAT 的值在
# 剖析器眼中就是名稱；語句已經完整的片語扣掉了下一句的開頭，同時也是子句字的要補回來。
# Closed 由人宣告那一格只有這幾個值。
# 同一條尾巴、同一個位置後寫的覆蓋先寫的，所以 Expand 展開出來的片語可以在後面補 Values。
$ClausePhrases = @(
    @{ Pattern = 'SET'; Expand = 4 }
    @{ Pattern = 'SET IDENTITY_INSERT {name}' }
    @{ Pattern = 'SET DATEFORMAT'; Values = @('mdy', 'dmy', 'ymd', 'ydm', 'myd', 'dym'); Closed = $true }
    @{ Pattern = 'SET DEADLOCK_PRIORITY'; Values = @('LOW', 'NORMAL', 'HIGH'); Closed = $true }

    @{ Pattern = 'CREATE' }
    @{ Pattern = 'ALTER' }
    @{ Pattern = 'DROP' }
    @{ Pattern = 'CREATE OR ALTER' }
    @{ Pattern = 'ALTER TABLE {name}' }
    @{ Pattern = 'ALTER DATABASE {name}' }
    @{ Pattern = 'ALTER DATABASE {name} SET'; Expand = 1 }
    @{ Pattern = 'BACKUP' }
    @{ Pattern = 'RESTORE' }

    # CREATE INDEX 寫完欄位就是完整的語句；WITH 同時是 CTE 的開頭，被當成下一句扣掉了。
    @{ Pattern = 'ALTER INDEX {name} ON {name}' }
    @{ Pattern = 'INDEX {name} ON {name} ()'; Lead = 'CREATE '; Values = @('WITH') }
    @{ Pattern = 'INCLUDE ()'; Lead = 'CREATE INDEX i ON t (a) '; Values = @('WITH') }
    @{ Pattern = 'INDEX {name} ON {name} () WITH (*'; Lead = 'CREATE ' }
    @{ Pattern = 'INCLUDE () WITH (*'; Lead = 'CREATE INDEX i ON t (a) ' }

    # AFTER、FOR、INSTEAD 之後是 INSERT／UPDATE／DELETE 與 OF，WITH 之後是 ENCRYPTION 這些選項。
    @{ Pattern = 'TRIGGER {name} ON {name}'; Lead = 'CREATE '; Expand = 1 }
    @{ Pattern = 'EXECUTE AS' }
    @{ Pattern = 'EXEC AS' }
    @{ Pattern = 'WITH EXECUTE AS'; Lead = 'CREATE PROCEDURE p ' }
    @{ Pattern = 'WITH EXEC AS'; Lead = 'CREATE PROCEDURE p ' }
    @{ Pattern = 'ON DELETE'; Lead = 'CREATE TABLE t (a int REFERENCES u (a) '; Expand = 1 }
    @{ Pattern = 'ON UPDATE'; Lead = 'CREATE TABLE t (a int REFERENCES u (a) '; Expand = 1 }

    @{ Pattern = 'WAITFOR' }
    @{ Pattern = 'DECLARE {name} CURSOR' }
    @{ Pattern = 'FETCH' }

    # FOR 有好幾種意思。查詢寫完之後是 XML、JSON、BROWSE，資料表之後多一個 SYSTEM_TIME——
    # 這兩種由前一格的位置分開。觸發程序、游標、序列、預設值條件約束、使用者與同義字的
    # FOR 前一格判不出位置，各寫一條更長的尾巴；同時比對得上時取項數多的。
    @{ Pattern = 'FOR'; After = @('SelectListTail', 'TableSourceTail', 'ExpressionTail', 'OrderByTail', 'GroupByTail'); Expand = 1 }
    @{ Pattern = 'FOR SYSTEM_TIME'; After = @('TableSourceTail'); Expand = 1 }
    @{ Pattern = 'CURSOR FOR'; Lead = 'DECLARE c ' }
    @{ Pattern = 'NEXT VALUE FOR'; Lead = 'SELECT ' }
    @{ Pattern = 'DEFAULT {value} FOR'; Lead = 'ALTER TABLE t ADD ' }
    @{ Pattern = 'USER {name} FOR'; Lead = 'CREATE ' }
    @{ Pattern = 'SYNONYM {name} FOR'; Lead = 'CREATE ' }
    @{ Pattern = 'NOT FOR'; Lead = 'CREATE TABLE t (a int IDENTITY ' }

    @{ Pattern = 'GROUP BY'; Lead = 'SELECT a FROM t '; Values = @('ROLLUP', 'CUBE', 'GROUPING SETS') }
    @{ Pattern = 'TOP {value} WITH'; Lead = 'SELECT ' }
    @{ Pattern = 'PERCENT WITH'; Lead = 'SELECT TOP 10 ' }
    @{ Pattern = 'AT TIME'; Lead = 'SELECT a ' }
    @{ Pattern = 'NOT MATCHED'; Lead = 'MERGE t USING s ON 1 = 1 WHEN ' }
    @{ Pattern = 'MATCHED BY'; Lead = 'MERGE t USING s ON 1 = 1 WHEN NOT ' }

    # OFFSET … FETCH：每一格只有一兩個字，但沒有它們就得整句背下來。
    # OFFSET 10 ROWS 已經是完整的語句，FETCH 同時是游標語句的開頭，被當成下一句扣掉了。
    @{ Pattern = 'OFFSET {value} ROWS'; Lead = 'SELECT a FROM t ORDER BY a '; Values = @('FETCH') }
    @{ Pattern = 'OFFSET {value} ROW'; Lead = 'SELECT a FROM t ORDER BY a '; Values = @('FETCH') }
    @{ Pattern = 'ROWS FETCH'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ' }
    @{ Pattern = 'ROW FETCH'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ' }
    @{ Pattern = 'FETCH NEXT {value}'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH FIRST {value}'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH NEXT {value} ROWS'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH NEXT {value} ROW'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH FIRST {value} ROWS'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }
    @{ Pattern = 'FETCH FIRST {value} ROW'; Lead = 'SELECT a FROM t ORDER BY a OFFSET 0 ROWS ' }

    # 視窗框架。CURRENT 單獨一個字不收：WHERE CURRENT OF 也是它。
    # CURRENT 之後剖析器收任何識別字（留到語意檢查才擋），ROW 只能手寫。
    @{ Pattern = 'ROWS'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ' }
    @{ Pattern = 'RANGE'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ' }
    @{ Pattern = 'ROWS BETWEEN'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ' }
    @{ Pattern = 'RANGE BETWEEN'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ' }
    @{ Pattern = 'UNBOUNDED'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN ' }
    @{ Pattern = 'PRECEDING AND'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED ' }
    @{ Pattern = 'ROWS CURRENT'; Lead = 'SELECT SUM(a) OVER (ORDER BY a '; Values = @('ROW'); Closed = $true }
    @{ Pattern = 'RANGE CURRENT'; Lead = 'SELECT SUM(a) OVER (ORDER BY a '; Values = @('ROW'); Closed = $true }
    @{ Pattern = 'BETWEEN CURRENT'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS '; Values = @('ROW'); Closed = $true }
    @{ Pattern = 'AND CURRENT'; Lead = 'SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING '; Values = @('ROW'); Closed = $true }
)

# 片語的續尾在第三階段那一組之外多幾條：SET 選項值、選項清單的 = ON、字串與括號的結尾，
# 以及幾個要多看一個詞元才分得出來的地方（AFTER 後面沒有 INSERT 就是語法錯誤）。
$PhraseContinuations = @($Continuations) + @(
    ' ON', " 'x'", ' = ON', ' = ON)', ' = 1', ' ON)', ' ROWS ONLY', ' ROW', ' ONLY',
    ' PRECEDING)', ' ROW)', " ZONE 'UTC'", ' OF x', ' IN (1)', ' FOR SELECT 1', ' ACTION)',
    ' (a)', ' TIES a FROM t ORDER BY a', ' FROM x', ' INSERT AS SELECT 1', ' OF INSERT AS SELECT 1',
    ' LEVEL READ COMMITTED', ' READ COMMITTED', ' COMMITTED', ' READ', ' TRIGGER ALL'
)

# 片語要把一千九百個候選字逐一配上幾十條續尾剖析，單執行緒要半小時，所以這一段交給
# C# 平行跑，每條執行緒一個剖析器。判定規則與第三階段相同，只多了一條：普通名稱在
# 字本身就被拒、而候選字撐過了字本身，也算——ROWS BETWEEN UNBOUNDED 後面要接
# PRECEDING 才完整，整段比對的話它與普通名稱一起被拒，永遠分不出來。
# ScriptDom 是 .NET Framework 組件，編譯時要 mscorlib 的轉送組件。
Add-Type -ReferencedAssemblies @($scriptDomPath, 'mscorlib', 'netstandard', 'System.Runtime', 'System.Collections', 'System.Threading', 'System.Threading.Tasks.Parallel') -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

public static class SqlAssistPhraseProber
{
    private static ThreadLocal<TSqlParser> _parser;
    private static HashSet<int> _rejecting;

    public static void Initialize(Type parserType, int[] rejecting)
    {
        // 建構參數是 initialQuotedIdentifiers；只傳 true 會落到 nonPublic 那個多載。
        _parser = new ThreadLocal<TSqlParser>(() => (TSqlParser)Activator.CreateInstance(parserType, new object[] { true }));
        _rejecting = new HashSet<int>(rejecting);
    }

    /// <summary>最早一個拒收錯誤的位置；沒有就是 int.MaxValue。</summary>
    public static int FirstRejection(string text)
    {
        IList<ParseError> errors;
        _parser.Value.Parse(new StringReader(text), out errors);
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

    public static bool IsComplete(string text)
    {
        IList<ParseError> errors;
        _parser.Value.Parse(new StringReader(text), out errors);
        return errors.Count == 0;
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
            if (IsComplete(probe + plain + continuation))
            {
                return true;
            }
        }

        return false;
    }

    public static string[] Probe(string probe, string[] pool, string[] reserved, string[] continuations, string plain)
    {
        var reservedSet = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase);
        var plainRejection = new int[continuations.Length];

        for (var index = 0; index < continuations.Length; index++)
        {
            plainRejection[index] = FirstRejection(probe + plain + continuations[index]);
        }

        var plainEnd = probe.Length + plain.Length;
        var accepted = new bool[pool.Length];

        Parallel.For(0, pool.Length, index =>
        {
            var word = pool[index];
            var wordEnd = probe.Length + word.Length;
            var canBeName = !reservedSet.Contains(word);

            for (var c = 0; c < continuations.Length && !accepted[index]; c++)
            {
                var continuation = continuations[c];
                var rejection = FirstRejection(probe + word + continuation);

                if (!canBeName)
                {
                    // 保留字當不了名字，被接受就一定是以關鍵字的身分。
                    accepted[index] = rejection > wordEnd;
                }
                else
                {
                    accepted[index] =
                        (plainRejection[c] <= plainEnd && rejection > wordEnd) ||
                        (plainRejection[c] <= plainEnd + continuation.Length &&
                            rejection > wordEnd + continuation.Length);
                }
            }
        });

        var words = new List<string>();

        for (var index = 0; index < pool.Length; index++)
        {
            if (accepted[index])
            {
                words.Add(pool[index]);
            }
        }

        return words.ToArray();
    }
}
'@

[SqlAssistPhraseProber]::Initialize($parserType, [int[]]$RejectingErrorNumbers)

function Get-PhraseProbe {
    param([string]$Lead, [string]$Pattern)

    $text = $Pattern.Replace('{name}', 't').Replace('{value}', '1').Replace('()', '(a)')
    $text = $Lead + $text.Replace('(*', '(')

    return $text.EndsWith('(') ? $text : $text + ' '
}

$poolArray = [string[]]@($phrasePool)
$reservedArray = [string[]]@($reserved)
$continuationArray = [string[]]@($PhraseContinuations)

function Get-PhraseWords {
    param([string]$Probe)

    return [SqlAssistPhraseProber]::Probe($Probe, $poolArray, $reservedArray, $continuationArray, $PlainName)
}

# 語句已經完整的片語（CREATE INDEX i ON t (a) 之後）接得上的字也包括下一句的開頭；
# 那一份在這裡探一次，從那些片語裡扣掉。
$statementStarters = [System.Collections.Generic.HashSet[string]]::new(
    [string[]](Get-PhraseWords -Probe 'SELECT 1; '),
    [System.StringComparer]::OrdinalIgnoreCase)

$phrases = [ordered]@{}

function Add-ClausePhrase {
    param([string]$Pattern, [string]$Probe, [string]$After, [int]$Expand, [object[]]$Values, [object]$Closed)

    Write-Progress -Activity '探測子句片語' -Status "$Pattern（$After）"
    $endsStatement = [SqlAssistPhraseProber]::IsComplete($Probe.TrimEnd())
    $found = @(Get-PhraseWords -Probe $Probe)

    if ($endsStatement) {
        $found = @($found | Where-Object { -not $statementStarters.Contains($_) })
    }

    $words = [System.Collections.Generic.List[string]]::new([string[]]$found)

    foreach ($value in @($Values | Where-Object { $_ })) {
        $valueAccepted = $PhraseContinuations | Where-Object {
            [SqlAssistPhraseProber]::FirstRejection($Probe + $value + $_) -gt $Probe.Length + $value.Length + $_.Length
        } | Select-Object -First 1

        if ($null -eq $valueAccepted) {
            throw "片語「$Pattern」的手寫值 $value 剖析不過，這份清單過時了。"
        }

        if (-not $words.Contains($value)) {
            $words.Add($value)
        }
    }

    $script:phrases["$After`t$Pattern"] = @{
        Pattern       = $Pattern
        After         = $After
        Probe         = $Probe
        Closed        = $null -ne $Closed ? [bool]$Closed : -not [SqlAssistPhraseProber]::AcceptsName($Probe, $PlainName, $continuationArray)
        EndsStatement = $endsStatement
        Words         = @($words)
    }

    if ($Expand -le 0) {
        return
    }

    foreach ($word in $found) {
        $child = "$Pattern $word"
        $childProbe = "$Probe$word "

        # 語句在這裡已經完整（SET NOCOUNT ON）就不再往下：後面接的是下一句。
        if ($script:phrases.Contains("$After`t$child") -or [SqlAssistPhraseProber]::IsComplete($childProbe.TrimEnd())) {
            continue
        }

        Add-ClausePhrase -Pattern $child -Probe $childProbe -After $After -Expand ($Expand - 1)
    }
}

# 帶 After 的片語以那個位置的第一個樣板探測：它是那個位置的代表寫法，而且是完整的語句，
# 「寫到這裡語句已經完整」的判斷才有意義。其餘樣板是第三階段為了撈齊關鍵字而加的旁支
# （FROM t JOIN y 還缺 ON），拿來探片語只會長出那條旁支才有的字，還要多花幾倍的時間。
foreach ($entry in $ClausePhrases) {
    $pattern = $entry['Pattern']
    $common = @{ Pattern = $pattern; Expand = [int]$entry['Expand']; Values = $entry['Values']; Closed = $entry['Closed'] }

    if ($null -ne $entry['Lead']) {
        if ($null -ne $entry['After']) {
            throw "片語「$pattern」的 Lead 與 After 只能寫一個。"
        }

        Add-ClausePhrase @common -Probe (Get-PhraseProbe -Lead $entry['Lead'] -Pattern $pattern) -After 'Any'
        continue
    }

    foreach ($position in @($entry['After'] ?? 'StatementStart')) {
        if (-not $ContextTemplates.Contains($position)) {
            throw "片語「$pattern」的 After 寫了不存在的位置 $position。"
        }

        $probe = Get-PhraseProbe -Lead @($ContextTemplates[$position])[0] -Pattern $pattern
        Add-ClausePhrase @common -Probe $probe -After $position
    }
}

Write-Progress -Activity '探測子句片語' -Completed

# 同一條尾巴在幾個位置上探到一模一樣的結果時併成一個片語，位置取聯集；結果不同的
# （資料表之後的 FOR 多一個 SYSTEM_TIME）各自一個，執行期由前一格的位置分開。
$merged = [ordered]@{}

foreach ($phrase in $phrases.Values) {
    $key = "$($phrase.Pattern)`t$($phrase.Closed)`t$($phrase.EndsStatement)`t$($phrase.Words -join ' ')"

    if ($merged.Contains($key)) {
        $merged[$key].After.Add($phrase.After)
        continue
    }

    $merged[$key] = @{
        Pattern       = $phrase.Pattern
        After         = [System.Collections.Generic.List[string]]::new([string[]]@($phrase.After))
        Probe         = $phrase.Probe
        Closed        = $phrase.Closed
        EndsStatement = $phrase.EndsStatement
        Words         = $phrase.Words
    }
}

$phrases = $merged
$phraseWords = $phrases.Values | ForEach-Object { $_.Words } | Where-Object { $keywords -notcontains $_ } | Sort-Object -Unique
Write-Host "子句片語：$($phrases.Count) 個，其中關鍵字清單以外的字 $(@($phraseWords).Count) 個"

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
$null = $builder.AppendLine('    /// <summary>子句片語：游標前的尾巴、探測文字、是否封閉、語句到那裡是否已經完整，以及那裡接得上的字。</summary>')
$null = $builder.AppendLine('    /// <remarks>')
$null = $builder.AppendLine('    /// 探測文字執行期用不到，輸出來是為了讓測試逐條回驗：片語比對對那段文字')
$null = $builder.AppendLine('    /// 必須認出同一個片語，兩邊說的才是同一個位置。')
$null = $builder.AppendLine('    /// </remarks>')
$null = $builder.AppendLine('    internal static readonly (string Pattern, SqlKeywordPosition After, string Probe, bool Closed, bool EndsStatement, string[] Words)[] ClausePhrases =')
$null = $builder.AppendLine('    {')

foreach ($phrase in $phrases.Values) {
    $afterLiteral = ($phrase.After | ForEach-Object { "SqlKeywordPosition.$_" }) -join ' | '
    $probeLiteral = $phrase.Probe.Replace('\', '\\').Replace('"', '\"')
    $closedLiteral = $phrase.Closed ? 'true' : 'false'
    $endsLiteral = $phrase.EndsStatement ? 'true' : 'false'
    $null = $builder.AppendLine("        (`"$($phrase.Pattern)`", $afterLiteral, `"$probeLiteral`", $closedLiteral, $endsLiteral, new string[]")
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
$null = $builder.AppendLine('}')

$resolved = [System.IO.Path]::GetFullPath($OutputPath)
$output = $builder.ToString().Replace("`r`n", "`n").Replace("`r", "`n")
[System.IO.File]::WriteAllText($resolved, $output, [System.Text.UTF8Encoding]::new($false))

Write-Host "已寫出 $resolved"
