[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$extensionId = 'SqlAssist.Ssms22.7f693af0-846a-4ee8-ab70-a174a3e31f65'
$outputPath = Join-Path $root 'src\SqlAssist.Ssms22\bin\x64\Debug\net48'
$sourceManifestPath = Join-Path $root 'src\SqlAssist.Ssms22\source.extension.vsixmanifest'

if (Get-Process -Name 'SSMS' -ErrorAction SilentlyContinue) {
    throw '請先關閉所有 SSMS 視窗，再部署 Debug 組件。'
}

if (-not $SkipBuild) {
    # 預設先建立完整 Debug VSIX，確保 DLL、PDB 與目前原始碼一致。
    & (Join-Path $PSScriptRoot 'Build-Extension.ps1') -Configuration Debug
}

if (-not (Test-Path -LiteralPath $sourceManifestPath)) {
    throw "找不到來源 VSIX Manifest：$sourceManifestPath"
}

[xml]$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw
$sourceVersion = [string]$sourceManifest.PackageManifest.Metadata.Identity.Version
$installations = @()
$ssmsRoots = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\SSMS" `
    -Directory `
    -Filter '22.0_*' `
    -ErrorAction SilentlyContinue

foreach ($ssmsRoot in $ssmsRoots) {
    $manifests = Get-ChildItem -Path (Join-Path $ssmsRoot.FullName 'Extensions') `
        -Recurse `
        -File `
        -Filter 'extension.vsixmanifest' `
        -ErrorAction SilentlyContinue

    foreach ($manifestFile in $manifests) {
        try {
            [xml]$installedManifest = Get-Content -LiteralPath $manifestFile.FullName -Raw
            $identity = $installedManifest.PackageManifest.Metadata.Identity

            if ($identity.Id -eq $extensionId) {
                $installations += [pscustomobject]@{
                    Version = [string]$identity.Version
                    Path = $manifestFile.Directory.FullName
                }
            }
        }
        catch {
            # 其他擴充的 Manifest 損壞不應阻擋 SqlAssist Debug 部署。
        }
    }
}

$installations = @($installations | Sort-Object Path -Unique)

if ($installations.Count -eq 0) {
    throw '找不到已安裝的 SqlAssist。請先執行 Install-Extension.ps1 -Configuration Debug。'
}

if ($installations.Count -gt 1) {
    $paths = ($installations.Path | ForEach-Object { "  $_" }) -join [Environment]::NewLine
    throw "找到多個 SqlAssist 安裝目錄，請先移除舊版本：$([Environment]::NewLine)$paths"
}

$installation = $installations[0]

if ($installation.Version -ne $sourceVersion) {
    throw "VSIX 版本不一致（已安裝 $($installation.Version)，來源 $sourceVersion）。請重新安裝 Debug VSIX。"
}

$fileNames = @(
    'SqlAssist.Core.dll',
    'SqlAssist.Core.pdb',
    'SqlAssist.Metadata.dll',
    'SqlAssist.Metadata.pdb',
    'SqlAssist.Ssms22.dll',
    'SqlAssist.Ssms22.pdb',
    'SqlAssist.registration.json'
)

foreach ($fileName in $fileNames) {
    $source = Join-Path $outputPath $fileName

    if (-not (Test-Path -LiteralPath $source)) {
        throw "找不到 Debug 輸出：$source"
    }

    # 只更新可安全替換的執行階段檔案，不碰 Manifest、PkgDef 與 VSCT 註冊。
    Copy-Item -LiteralPath $source -Destination $installation.Path -Force
}

foreach ($fileName in $fileNames) {
    $source = Join-Path $outputPath $fileName
    $destination = Join-Path $installation.Path $fileName

    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        throw "部署後檔案驗證失敗：$destination"
    }
}

# SSMS 把 MEF 組合圖與 Unified Settings 的定義各自快取起來，兩份都以「安裝擴充」
# 為更新時機，不看擴充資料夾裡的 DLL 有沒有換過。只部署 DLL 的話：
#
#   * MEF 快取裡記的是<b>完整型別名稱</b>。把匯出的類別搬到別的命名空間之後，
#     快取仍然要求舊名稱，那些部件會安靜地建立失敗——沒有例外、沒有記錄，
#     只有「命令處理常式整組失效」這種症狀（Tab 不展開、關鍵字不大寫、
#     ESC 關不掉預覽、輸入點號不重開清單）。
#   * 設定定義快取裡沒有新增的 moniker，設定頁就少一項，讀取時回報 NotPersisted。
#
# 兩份都刪掉，SSMS 下次啟動時重建。代價是那一次啟動慢幾秒。
$hiveRoot = Split-Path -Parent (Split-Path -Parent $installation.Path)
$staleCaches = @(
    (Join-Path $hiveRoot 'ComponentModelCache')
    (Join-Path $hiveRoot 'UnifiedSettings\DefinitionCache.dat')
)
$cleared = @()

foreach ($cache in $staleCaches) {
    if (-not (Test-Path -LiteralPath $cache)) {
        continue
    }

    try {
        Remove-Item -LiteralPath $cache -Recurse -Force
        $cleared += $cache
    }
    catch {
        # 清不掉不該擋下部署，但一定要講出來：留著舊快取就是上面那些症狀。
        Write-Warning "無法清除快取，請手動刪除後再啟動 SSMS：$cache"
    }
}

Write-Host 'Debug 組件部署完成：' -ForegroundColor Green
Write-Host $installation.Path

foreach ($cache in $cleared) {
    Write-Host "已清除快取：$cache" -ForegroundColor DarkGray
}

Write-Host '現在可在 Visual Studio 按 F5 啟動 SSMS（首次啟動會重建快取，會慢幾秒）。'
