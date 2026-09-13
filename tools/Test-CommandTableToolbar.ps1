#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output
$root = Split-Path -Parent $PSScriptRoot
$checker = Join-Path $PSScriptRoot 'Test-CommandTable.ps1'
$source = Join-Path $root 'src/SqlAssist.Ssms22/Menus.vsct'
$directory = Join-Path $root ('artifacts/command-table-toolbar/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null

# 真正產品命令表包含無 Parent 的 toolbar，不能為了測試把它移成普通選單。
& $checker -VsctPath $source

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Mutate, [string]$Expected)
    [xml]$document = Get-Content -LiteralPath $source -Raw -Encoding utf8
    $ns = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('ct', 'http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable')
    & $Mutate $document $ns
    $path = Join-Path $directory ($Name + '.vsct')
    $document.Save($path)
    try {
        & $checker -VsctPath $path
        throw "未拒絕錯誤命令表：$Name"
    }
    catch {
        if ($_.Exception.Message -notmatch $Expected) { throw }
    }
}

Assert-Rejected 'ordinary-menu-without-parent' {
    param($document, $ns)
    $menu = $document.SelectSingleNode('//ct:Menu[@id="SqlAssistMenu"]', $ns)
    [void]$menu.RemoveChild($menu.SelectSingleNode('ct:Parent', $ns))
} 'SqlAssistMenu.*沒有 <Parent>'

Assert-Rejected 'toolbar-invalid-parent' {
    param($document, $ns)
    $menu = $document.SelectSingleNode('//ct:Menu[@id="SqlAssistToolbar"]', $ns)
    $parent = $document.CreateElement('Parent', $menu.NamespaceURI)
    $parent.SetAttribute('guid', 'guidSqlAssistCommandSet')
    $parent.SetAttribute('id', 'SqlAssistMenu')
    [void]$menu.PrependChild($parent)
} 'SqlAssistToolbar.*不是本命令表宣告的 Group'

Assert-Rejected 'query-memory-missing-localized-name' {
    param($document, $ns)
    $strings = $document.SelectSingleNode('//ct:Button[@id="cmdidShowQueryMemory"]/ct:Strings', $ns)
    [void]$strings.RemoveChild($strings.SelectSingleNode('ct:LocCanonicalName', $ns))
} 'cmdidShowQueryMemory 沒有 LocCanonicalName'

Write-Host 'Toolbar 命令表 fixture 通過：1 個正向、3 個負向；一般選單護欄未放寬。'
