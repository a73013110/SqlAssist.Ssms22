#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Deployment.psm1') -Force
if (-not $OutputPath) {
    $OutputPath = Join-Path (Get-SqlAssistRoot) 'src/SqlAssist.Ssms22/bin/x64/Debug/net48'
}
$files = @(Get-SqlAssistDeploymentFile)
$testRoot = Join-Path (Get-SqlAssistRoot) "artifacts/debug-deployment-tests/$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $testRoot
$script:passed = 0

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-Fixture([string]$Name, [switch]$RealFiles) {
    $root = Join-Path $testRoot $Name
    $source = Join-Path $root 'output'
    $target = Join-Path $root 'installed'
    $null = New-Item -ItemType Directory -Path $source, $target
    foreach ($file in $files) {
        $path = Join-Path $OutputPath $file.Name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            # 成功案例驗真正產物；負向矩陣只需小型內容，避免重複複製數百 MB。
            if ($RealFiles -or $file.Name -in @('extension.vsixmanifest', 'SqlAssist.Ssms22.pkgdef')) {
                Copy-Item -LiteralPath $path -Destination $source
                Copy-Item -LiteralPath $path -Destination $target
            }
            else {
                [IO.File]::WriteAllText((Join-Path $source $file.Name), "fixture：$($file.Name)")
                [IO.File]::WriteAllText((Join-Path $target $file.Name), "fixture：$($file.Name)")
            }
        }
        elseif ($file.Required) { throw "請先建置 Debug，缺少 fixture 來源：$path" }
    }
    # 新舊內容刻意不同，否則「失敗前沒有寫入」的測試可能是假陽性。
    foreach ($file in $files | Where-Object Policy -EQ 'Replace') {
        [IO.File]::WriteAllText((Join-Path $target $file.Name), '舊版 fixture')
    }
    return [pscustomobject]@{ Source = $source; Target = $target }
}

function Get-Snapshot([string]$Path) {
    return (@(Get-ChildItem -LiteralPath $Path -File | Sort-Object Name | ForEach-Object {
        "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }) -join "`n")
}

function Assert-Rejected($Fixture, [string]$Expected) {
    $before = Get-Snapshot $Fixture.Target
    $message = ''
    try {
        $null = Invoke-SqlAssistDebugFileDeployment -OutputPath $Fixture.Source -InstallationPath $Fixture.Target
    }
    catch { $message = $_.Exception.Message }
    Assert-Condition ($message.Contains($Expected)) "未依預期拒絕：$Expected；實際：$message"
    Assert-Condition ((Get-Snapshot $Fixture.Target) -ceq $before) '預檢失敗卻改寫了安裝檔案。'
    $script:passed++
}

$fixture = New-Fixture 'success' -RealFiles
[IO.File]::WriteAllText((Join-Path $fixture.Source 'System.Memory.dll'), '不應部署')
[IO.File]::WriteAllText((Join-Path $fixture.Source 'SqlAssist.SqlMemory.Probe.exe'), '不應部署')
[IO.File]::WriteAllText((Join-Path $fixture.Target 'unrelated.txt'), '保留其他檔案')
$result = @(Invoke-SqlAssistDebugFileDeployment -OutputPath $fixture.Source -InstallationPath $fixture.Target)
foreach ($file in $files | Where-Object Required) {
    Assert-Condition ((Get-FileHash (Join-Path $fixture.Source $file.Name)).Hash -eq
        (Get-FileHash (Join-Path $fixture.Target $file.Name)).Hash) "成功部署後內容不同：$($file.Name)"
}
Assert-Condition ('SqlAssist.SqlMemory.Isolation.dll' -in $result.Name -and
    'SqlAssist.SqlMemory.Sqlite.dll' -in $result.Name) 'SQL Memory 不在部署結果。'
Assert-Condition (-not (Test-Path (Join-Path $fixture.Target 'System.Memory.dll')) -and
    -not (Test-Path (Join-Path $fixture.Target 'SqlAssist.SqlMemory.Probe.exe'))) '複製了白名單外的輸出。'
Assert-Condition ([IO.File]::ReadAllText((Join-Path $fixture.Target 'unrelated.txt')) -eq '保留其他檔案') '更動了其他檔案。'
$script:passed++

foreach ($file in $files | Where-Object Required) {
    $fixture = New-Fixture "missing-source-$($file.Name)"
    Remove-Item -LiteralPath (Join-Path $fixture.Source $file.Name)
    Assert-Rejected $fixture "缺少必要來源檔案或不是檔案：$($file.Name)"

    $fixture = New-Fixture "missing-installed-$($file.Name)"
    Remove-Item -LiteralPath (Join-Path $fixture.Target $file.Name)
    Assert-Rejected $fixture 'Install-Extension.ps1'
}

foreach ($file in $files | Where-Object Policy -EQ 'Install') {
    $fixture = New-Fixture "upgrade-$($file.Name)"
    [IO.File]::WriteAllText((Join-Path $fixture.Source $file.Name), '新版安裝資產')
    Assert-Rejected $fixture "安裝資產已變更：$($file.Name)"
}

$fixture = New-Fixture 'pkgdef-cachetag'
$pkgdefPath = Join-Path $fixture.Source 'SqlAssist.Ssms22.pkgdef'
$pkgdef = [IO.File]::ReadAllText($pkgdefPath) -replace '(?m)^("CacheTag"=qword:)[0-9A-Fa-f]+', '${1}00000000000000001'
[IO.File]::WriteAllText($pkgdefPath, $pkgdef)
$null = Invoke-SqlAssistDebugFileDeployment -OutputPath $fixture.Source -InstallationPath $fixture.Target
Assert-Condition ([IO.File]::ReadAllText((Join-Path $fixture.Target 'SqlAssist.Ssms22.pkgdef')) -ne $pkgdef) 'CacheTag 測試意外覆寫 pkgdef。'
$script:passed++

foreach ($file in $files | Where-Object { -not $_.Required }) {
    $fixture = New-Fixture "optional-$($file.Name)"
    $path = Join-Path $fixture.Source $file.Name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    $null = Invoke-SqlAssistDebugFileDeployment -OutputPath $fixture.Source -InstallationPath $fixture.Target
    Assert-Condition (-not (Test-Path (Join-Path $fixture.Target $file.Name))) '遺留不匹配的舊 PDB。'
    $script:passed++
}

foreach ($scenario in @('patch', 'minor', 'asset', 'invalid', 'menu')) {
    $fixture = New-Fixture "manifest-$scenario"
    $path = Join-Path $fixture.Source 'extension.vsixmanifest'
    [xml]$manifest = [IO.File]::ReadAllText($path)
    $identity = $manifest.PackageManifest.Metadata.Identity
    $version = [version]$identity.Version
    switch ($scenario) {
        'patch' { $identity.Version = "$($version.Major).$($version.Minor).999.1" }
        'minor' { $identity.Version = "$($version.Major).$($version.Minor + 1).0.0" }
        'asset' { $manifest.PackageManifest.Assets.Asset[0].Path = 'changed.pkgdef' }
        'invalid' { $identity.Version = 'GetBuildVersion' }
        'menu' {
            $pkgdef = Join-Path $fixture.Source 'SqlAssist.Ssms22.pkgdef'
            [IO.File]::WriteAllText($pkgdef, ([IO.File]::ReadAllText($pkgdef) -replace 'Menus\.ctmenu,\s*\d+', 'Menus.ctmenu, 999'))
        }
    }
    $manifest.Save($path)
    if ($scenario -eq 'patch') {
        $installedHash = (Get-FileHash (Join-Path $fixture.Target 'extension.vsixmanifest')).Hash
        $null = Invoke-SqlAssistDebugFileDeployment -OutputPath $fixture.Source -InstallationPath $fixture.Target
        Assert-Condition ((Get-FileHash (Join-Path $fixture.Target 'extension.vsixmanifest')).Hash -eq $installedHash) '部署改寫了 Manifest。'
        $script:passed++
    }
    else { Assert-Rejected $fixture ($scenario -eq 'invalid' ? '無法解析' : 'Install-Extension.ps1') }
}

$fixture = New-Fixture 'source-directory'
$path = Join-Path $fixture.Source 'SqlAssist.SqlMemory.Sqlite.dll'
Remove-Item -LiteralPath $path
$null = New-Item -ItemType Directory -Path $path
Assert-Rejected $fixture '缺少必要來源檔案或不是檔案'

$fixture = New-Fixture 'same-directory'
$fixture.Target = $fixture.Source
Assert-Rejected $fixture '建置與安裝目錄不可相同'

# 在模組作用域攔截複製以注入損毀；不碰真實安裝，仍走正式的部署後驗證。
$fixture = New-Fixture 'hash-mismatch'
$module = Get-Module SqlAssist.Deployment
try {
    & $module {
        function script:Copy-Item {
            param($LiteralPath, $Destination, [switch]$Force)
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
            [IO.File]::WriteAllText($Destination, '注入複製後損毀')
        }
    }
    $message = ''
    try { $null = Invoke-SqlAssistDebugFileDeployment -OutputPath $fixture.Source -InstallationPath $fixture.Target }
    catch { $message = $_.Exception.Message }
    Assert-Condition ($message.Contains('部署後 SHA-256 驗證失敗')) "未發現部署後損毀：$message"
    $script:passed++
}
finally { & $module { Remove-Item Function:script:Copy-Item } }

Write-Host "Debug 部署隔離測試通過：$script:passed 項。"
Write-Host "Fixture 與失敗證據：$testRoot"
