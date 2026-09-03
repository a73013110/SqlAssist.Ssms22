#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$VsctPath,
    [string]$CommandIdsPath,
    [string]$CommandsPath,
    [string]$RegistrationPath,
    [string]$SsmsInstallDir
)

# 同一組命令的識別碼寫在三個地方：VSCT 的 IDSymbol、C# 的 CommandIds 常數，
# 以及 Unified Settings 註冊檔裡以十進位定址的按鈕。三者不一致不會編譯失敗，
# 也不會有執行期例外——按鈕就是按不到，快捷鍵就是沒反應。
# 因此除了命令表自己的結構，這裡也一併交叉驗證那三份來源。
#
# VSCT 的階層是 Menu → Group → Menu／Button，中間不能跳層。
# 掛錯層不會產生編譯錯誤，也不會有執行期例外，選單就只是安靜地不出現，
# 而 pkgdef、自動載入與命令處理器全都正常，非常難從症狀反推。
# 因此在建置前直接對來源檔驗證。

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$root = Split-Path -Parent $PSScriptRoot

if (-not $VsctPath) {
    $VsctPath = Join-Path $root 'src\SqlAssist.Ssms22\Menus.vsct'
}

if (-not $CommandIdsPath) {
    $CommandIdsPath = Join-Path $root 'src\SqlAssist.Ssms22\Commands\CommandIds.cs'
}

if (-not $CommandsPath) {
    $CommandsPath = Join-Path $root 'src\SqlAssist.Ssms22\Commands\SqlAssistCommands.cs'
}

if (-not $RegistrationPath) {
    $RegistrationPath = Join-Path $root 'src\SqlAssist.Ssms22\SqlAssist.registration.json'
}

if (-not (Test-Path -LiteralPath $VsctPath)) {
    throw "找不到命令表：$VsctPath"
}

[xml]$vsct = Get-Content -LiteralPath $VsctPath -Raw -Encoding UTF8
$ns = [System.Xml.XmlNamespaceManager]::new($vsct.NameTable)
$ns.AddNamespace('ct', 'http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable')

$ownGroupIds = @($vsct.SelectNodes('//ct:Groups/ct:Group', $ns) | ForEach-Object { $_.id })
$ownMenuIds = @($vsct.SelectNodes('//ct:Menus/ct:Menu', $ns) | ForEach-Object { $_.id })
$problems = [System.Collections.Generic.List[string]]::new()

# guidSHLMainMenu 的命名慣例可靠：IDM_ 是 Menu，IDG_ 是 Group。
function Test-Placement {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$Kind,
        [ValidateSet('Group', 'Menu')][string]$ExpectedParentKind,
        [string[]]$OwnIdsOfExpectedKind
    )

    $parent = $Node.SelectSingleNode('ct:Parent', $ns)

    if ($null -eq $parent) {
        $problems.Add("$Kind '$($Node.id)' 沒有 <Parent>。")
        return
    }

    $parentGuid = $parent.guid
    $parentId = $parent.id
    $expectedPrefix = if ($ExpectedParentKind -eq 'Group') { 'IDG_' } else { 'IDM_' }

    # 慣例對殼層與 SSMS 的命令表都成立，所以只要識別碼帶前綴就一律檢查——
    # 原本只認 guidSHLMainMenu，掛到別人的選單上會安靜地跳過，
    # 而那正是這個檢查要擋的那一種失敗。
    if ($parentGuid -ne 'guidSqlAssistCommandSet' -and
        ($parentId.StartsWith('IDM_') -or $parentId.StartsWith('IDG_'))) {
        if (-not $parentId.StartsWith($expectedPrefix)) {
            $problems.Add(
                "$Kind '$($Node.id)' 掛在 '$parentId' 底下，但它必須掛在一個 $ExpectedParentKind（$expectedPrefix*）底下。")
        }
        return
    }

    if ($parentGuid -eq 'guidSqlAssistCommandSet' -and $parentId -notin $OwnIdsOfExpectedKind) {
        $problems.Add(
            "$Kind '$($Node.id)' 掛在 '$parentId' 底下，但 '$parentId' 不是本命令表宣告的 $ExpectedParentKind。")
    }
}

foreach ($menu in $vsct.SelectNodes('//ct:Menus/ct:Menu', $ns)) {
    Test-Placement -Node $menu -Kind 'Menu' -ExpectedParentKind 'Group' -OwnIdsOfExpectedKind $ownGroupIds
}

foreach ($button in $vsct.SelectNodes('//ct:Buttons/ct:Button', $ns)) {
    Test-Placement -Node $button -Kind 'Button' -ExpectedParentKind 'Group' -OwnIdsOfExpectedKind $ownGroupIds
}


# 沒有 <Parent> 的群組是刻意的：Unified Settings 的設定頁按鈕必須存在於命令表，
# 但不該出現在任何選單。這只有在底下每一顆按鈕都標了 CommandWellOnly 時才成立——
# 少標一個，那顆命令就是永遠按不到，而且不會有任何錯誤訊息。
$unparentedGroupIds = @(
    $vsct.SelectNodes('//ct:Groups/ct:Group', $ns) |
        Where-Object { $null -eq $_.SelectSingleNode('ct:Parent', $ns) } |
        ForEach-Object { $_.id })

foreach ($group in $vsct.SelectNodes('//ct:Groups/ct:Group', $ns)) {
    if ($group.id -in $unparentedGroupIds) {
        continue
    }

    Test-Placement -Node $group -Kind 'Group' -ExpectedParentKind 'Menu' -OwnIdsOfExpectedKind $ownMenuIds
}

foreach ($button in $vsct.SelectNodes('//ct:Buttons/ct:Button', $ns)) {
    $parent = $button.SelectSingleNode('ct:Parent', $ns)

    if ($null -eq $parent -or $parent.id -notin $unparentedGroupIds) {
        continue
    }

    $flags = @($button.SelectNodes('ct:CommandFlag', $ns) | ForEach-Object { $_.InnerText })

    if ('CommandWellOnly' -notin $flags) {
        $problems.Add(
            "Button '$($button.id)' 掛在沒有父選單的群組 '$($parent.id)' 底下，卻沒有標記 CommandWellOnly，它永遠不會出現。")
    }
}

# 每個宣告的 IDSymbol 都必須有對應的 Menu／Group／Button，反之亦然。
$declaredIds = @($vsct.SelectNodes('//ct:Symbols/ct:GuidSymbol[@name="guidSqlAssistCommandSet"]/ct:IDSymbol', $ns) |
    ForEach-Object { $_.name })
$usedIds = @($ownGroupIds) + @($ownMenuIds) +
    @($vsct.SelectNodes('//ct:Buttons/ct:Button', $ns) | ForEach-Object { $_.id })

foreach ($id in $usedIds) {
    if ($id -notin $declaredIds) {
        $problems.Add("命令 '$id' 沒有對應的 <IDSymbol> 宣告。")
    }
}

# 命令集 GUID 與每一個命令識別碼在 VSCT、C# 常數與註冊檔之間必須一致。
# 三份來源的形式各不相同（symbol 名稱、十六進位常數、十進位整數），
# 沒有任何一種編譯或結構驗證看得出它們指的不是同一個命令。
$commandSetSymbol = $vsct.SelectSingleNode(
    '//ct:Symbols/ct:GuidSymbol[@name="guidSqlAssistCommandSet"]', $ns)

if ($null -eq $commandSetSymbol) {
    throw "命令表沒有宣告 guidSqlAssistCommandSet。"
}

$commandSetGuid = $commandSetSymbol.value.Trim('{', '}')
$vsctCommandIds = @{}

foreach ($idSymbol in $commandSetSymbol.SelectNodes('ct:IDSymbol', $ns)) {
    # 只有 cmdid* 是命令；選單與群組的識別碼不會出現在 C# 或註冊檔裡。
    if ($idSymbol.name.StartsWith('cmdid')) {
        $vsctCommandIds[$idSymbol.name.Substring('cmdid'.Length)] = [Convert]::ToInt32($idSymbol.value, 16)
    }
}

$commandIdsText = Get-Content -LiteralPath $CommandIdsPath -Raw -Encoding UTF8
$csharpGuidMatch = [regex]::Match($commandIdsText, 'CommandSetString\s*=\s*"([^"]+)"')

if (-not $csharpGuidMatch.Success) {
    throw "在 $CommandIdsPath 找不到 CommandSetString。"
}

if ($csharpGuidMatch.Groups[1].Value -ne $commandSetGuid) {
    $problems.Add(
        "命令集 GUID 不一致：VSCT 是 $commandSetGuid，CommandIds.cs 是 $($csharpGuidMatch.Groups[1].Value)。")
}

$csharpCommandIds = @{}

foreach ($match in [regex]::Matches($commandIdsText, 'public const int (\w+)\s*=\s*(0x[0-9A-Fa-f]+)\s*;')) {
    $csharpCommandIds[$match.Groups[1].Value] = [Convert]::ToInt32($match.Groups[2].Value, 16)
}

foreach ($name in $csharpCommandIds.Keys) {
    if (-not $vsctCommandIds.ContainsKey($name)) {
        $problems.Add("CommandIds.$name 在命令表裡沒有對應的 cmdid$name。")
        continue
    }

    if ($csharpCommandIds[$name] -ne $vsctCommandIds[$name]) {
        $problems.Add(
            "CommandIds.$name 是 $('0x{0:X4}' -f $csharpCommandIds[$name])，" +
            "命令表的 cmdid$name 卻是 $('0x{0:X4}' -f $vsctCommandIds[$name])。")
    }
}

foreach ($name in $vsctCommandIds.Keys) {
    if (-not $csharpCommandIds.ContainsKey($name)) {
        $problems.Add("命令表宣告了 cmdid$name，但 CommandIds.cs 沒有對應的常數。")
    }
}

# 會自己隱藏的命令必須在命令表標上 DynamicVisibility。殼層只有看到那個旗標時才
# 理會 QueryStatus 回報的「隱藏」，否則 BeforeQueryStatus 把 Visible 設成 false
# 也沒有用，項目照樣出現在選單上——沒有例外、沒有紀錄，而且從程式碼上看完全正確。
# DefaultInvisible 管的是套件還沒載入的那一段，殼層那時候照命令表的預設值畫。
$commandsText = Get-Content -LiteralPath $CommandsPath -Raw -Encoding UTF8
$buttonFlags = @{}

foreach ($button in $vsct.SelectNodes('//ct:Buttons/ct:Button', $ns)) {
    $buttonFlags[$button.id] = @($button.SelectNodes('ct:CommandFlag', $ns) | ForEach-Object { $_.InnerText })
}

foreach ($match in [regex]::Matches($commandsText, 'AddCommand\(\s*CommandIds\.(\w+)[^;]*?isVisible:')) {
    $id = 'cmdid' + $match.Groups[1].Value

    if (-not $buttonFlags.ContainsKey($id)) {
        continue
    }

    foreach ($flag in 'DefaultInvisible', 'DynamicVisibility') {
        if ($flag -notin $buttonFlags[$id]) {
            $problems.Add(
                "$id 在 SqlAssistCommands.cs 有 isVisible 判斷，命令表卻沒有標記 $flag，" +
                "殼層會忽略它回報的隱藏狀態。")
        }
    }
}

# 註冊檔帶註解（Unified Settings 的載入器接受 JSONC），因此不用 ConvertFrom-Json。
$jsonOptions = [System.Text.Json.JsonDocumentOptions]::new()
$jsonOptions.CommentHandling = [System.Text.Json.JsonCommentHandling]::Skip
$registration = [System.Text.Json.JsonDocument]::Parse(
    (Get-Content -LiteralPath $RegistrationPath -Raw -Encoding UTF8),
    $jsonOptions)

function Get-VsctReference {
    param([System.Text.Json.JsonElement]$Element)

    switch ($Element.ValueKind) {
        'Object' {
            foreach ($property in $Element.EnumerateObject()) {
                if ($property.Name -eq 'vsct') {
                    $set = $property.Value.GetProperty('set').GetString()
                    $id = $property.Value.GetProperty('id').GetInt32()
                    [pscustomobject]@{ Set = $set; Id = $id }
                    continue
                }

                Get-VsctReference -Element $property.Value
            }
        }
        'Array' {
            foreach ($item in $Element.EnumerateArray()) {
                Get-VsctReference -Element $item
            }
        }
    }
}

$declaredIdValues = $vsctCommandIds.Values

foreach ($reference in Get-VsctReference -Element $registration.RootElement) {
    if ($reference.Set -ne $commandSetGuid) {
        $problems.Add("註冊檔的按鈕指向命令集 $($reference.Set)，但本擴充的命令集是 $commandSetGuid。")
    }

    if ($reference.Id -notin $declaredIdValues) {
        $problems.Add(
            "註冊檔的按鈕指向命令 $($reference.Id)（$('0x{0:X4}' -f $reference.Id)），命令表裡沒有這個命令。")
    }
}

$registration.Dispose()

# 掛進 SSMS 自己選單的那些群組，父選單的 GUID 與 ID 是 SSMS 的內部值，不是公開的
# 擴充契約。SSMS 改版換掉其中一個的症狀是那一段選單安靜地不出現——沒有例外、
# 沒有紀錄，要等使用者按不到才發現。這些值全都來自公開類別 SQLWorkbenchCommands，
# 所以在這裡直接跟安裝好的 SSMS 對一次，把執行期的沉默失敗提前成建置失敗。
$sqlEditorsPath = Join-Path (Get-SsmsInstallPath -InstallDir $SsmsInstallDir) `
    'Common7\IDE\Extensions\Application\SQLEditors.dll'

if (-not (Test-Path -LiteralPath $sqlEditorsPath)) {
    Write-Warning "找不到 $sqlEditorsPath，略過 SSMS 選單識別碼比對。"
}
else {
    $workbenchCommands = [System.Reflection.Assembly]::LoadFrom($sqlEditorsPath).GetType(
        'Microsoft.SqlServer.Management.UI.VSIntegration.Editors.SQLWorkbenchCommands')

    if ($null -eq $workbenchCommands) {
        $problems.Add('SQLEditors.dll 裡找不到 SQLWorkbenchCommands，無法比對 SSMS 的選單識別碼。')
    }
    else {
        # 每一項是「VSCT 的 GuidSymbol 名稱 → SSMS 欄位名稱」與其底下的
        # 「IDSymbol 名稱 → SSMS 常數名稱」。名稱一致是刻意的，好一眼看出出處。
        $ssmsMenus = @{
            guidSqlWorkbenchEditorGroup = @{
                Field = 'GUID_SQLEditorGroup'
                Ids   = @('IDM_SQLWB_SQLRESGRID_CONTEXT')
            }
        }

        foreach ($symbolName in $ssmsMenus.Keys) {
            $expected = $ssmsMenus[$symbolName]
            $symbol = $vsct.SelectSingleNode(
                "//ct:Symbols/ct:GuidSymbol[@name='$symbolName']", $ns)

            if ($null -eq $symbol) {
                $problems.Add("命令表沒有宣告 $symbolName。")
                continue
            }

            $actualGuid = [string]$workbenchCommands.GetField($expected.Field).GetValue($null)

            if ($symbol.value.Trim('{', '}') -ne $actualGuid) {
                $problems.Add(
                    "$symbolName 是 $($symbol.value)，但 SSMS 的 " +
                    "SQLWorkbenchCommands.$($expected.Field) 是 {$actualGuid}。")
            }

            foreach ($idName in $expected.Ids) {
                $idSymbol = $symbol.SelectSingleNode("ct:IDSymbol[@name='$idName']", $ns)

                if ($null -eq $idSymbol) {
                    $problems.Add("$symbolName 底下沒有宣告 $idName。")
                    continue
                }

                $actualId = $workbenchCommands.GetField($idName).GetRawConstantValue()

                if ([Convert]::ToInt32($idSymbol.value, 16) -ne $actualId) {
                    $problems.Add(
                        "$idName 是 $($idSymbol.value)，但 SSMS 的 " +
                        "SQLWorkbenchCommands.$idName 是 $('0x{0:X4}' -f $actualId)。")
                }
            }
        }
    }
}

if ($problems.Count -gt 0) {
    throw "命令表檢查失敗：`n  " + ($problems -join "`n  ")
}

Write-Host (
    "命令表檢查通過：$($ownMenuIds.Count) 個選單、$($ownGroupIds.Count) 個群組、" +
    "$($vsctCommandIds.Count) 個命令，識別碼與 CommandIds.cs 及註冊檔一致。")
