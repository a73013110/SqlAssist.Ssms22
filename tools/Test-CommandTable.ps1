[CmdletBinding()]
param(
    [string]$VsctPath
)

# VSCT 的階層是 Menu → Group → Menu／Button，中間不能跳層。
# 掛錯層不會產生編譯錯誤，也不會有執行期例外，選單就只是安靜地不出現，
# 而 pkgdef、自動載入與命令處理器全都正常，非常難從症狀反推。
# 因此在建置前直接對來源檔驗證。

$ErrorActionPreference = 'Stop'

if (-not $VsctPath) {
    $root = Split-Path -Parent $PSScriptRoot
    $VsctPath = Join-Path $root 'src\SqlAssist.Ssms22\Menus.vsct'
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

    if ($parentGuid -eq 'guidSHLMainMenu') {
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

foreach ($group in $vsct.SelectNodes('//ct:Groups/ct:Group', $ns)) {
    Test-Placement -Node $group -Kind 'Group' -ExpectedParentKind 'Menu' -OwnIdsOfExpectedKind $ownMenuIds
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

if ($problems.Count -gt 0) {
    throw "命令表檢查失敗：`n  " + ($problems -join "`n  ")
}

Write-Host "命令表檢查通過：$($ownMenuIds.Count) 個選單、$($ownGroupIds.Count) 個群組。"
