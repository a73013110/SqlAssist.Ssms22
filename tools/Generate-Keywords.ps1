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
    ExpressionTail   = @(
        'SELECT * FROM t WHERE a ', 'SELECT * FROM t WHERE a = 1 ',
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

    # SET 的選項名稱寫完之後：工作階段選項的 ON／OFF、隔離等級的 READ。
    SetOptionValue   = @('SET NOCOUNT ', 'SET IDENTITY_INSERT t ', 'SET TRANSACTION ISOLATION LEVEL ')
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
$null = $builder.AppendLine('}')

$resolved = [System.IO.Path]::GetFullPath($OutputPath)
$output = $builder.ToString().Replace("`r`n", "`n").Replace("`r", "`n")
[System.IO.File]::WriteAllText($resolved, $output, [System.Text.UTF8Encoding]::new($false))

Write-Host "已寫出 $resolved"
