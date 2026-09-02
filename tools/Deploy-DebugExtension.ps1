#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$SsmsInstallDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force

$outputPath = Join-Path (Get-SqlAssistRoot) 'src\SqlAssist.Ssms22\bin\x64\Debug\net48'
# 來源 Manifest 的版號是 GetBuildVersion 佔位符，只有建置產物裡才是展開後的實際版號。
$builtManifestPath = Join-Path $outputPath 'extension.vsixmanifest'

function Get-MajorMinor {
    param([string]$Version)

    $parsed = $null

    if (-not [version]::TryParse($Version, [ref]$parsed)) {
        throw "無法解析 VSIX 版號：$Version"
    }

    return "$($parsed.Major).$($parsed.Minor)"
}

Assert-SsmsClosed -Action '部署 Debug 組件'

if (-not $SkipBuild) {
    # 預設先建立完整 Debug VSIX，確保 DLL、PDB 與目前原始碼一致。
    & (Join-Path $PSScriptRoot 'Build-Extension.ps1') -Configuration Debug -SsmsInstallDir $SsmsInstallDir
}

if (-not (Test-Path -LiteralPath $builtManifestPath)) {
    throw "找不到建置後的 VSIX Manifest：$builtManifestPath。請先執行 Build-Extension.ps1 -Configuration Debug。"
}

[xml]$builtManifest = Get-Content -LiteralPath $builtManifestPath -Raw
$builtVersion = [string]$builtManifest.PackageManifest.Metadata.Identity.Version
$installations = Get-SqlAssistInstallation

if ($installations.Count -eq 0) {
    throw '找不到已安裝的 SqlAssist。請先執行 Install-Extension.ps1 -Configuration Debug。'
}

if ($installations.Count -gt 1) {
    $paths = ($installations.Path | ForEach-Object { "  $_" }) -join [Environment]::NewLine
    throw "找到多個 SqlAssist 安裝目錄，請先移除舊版本：$([Environment]::NewLine)$paths"
}

$installation = $installations[0]

# 版號的第三段是 git height，每個 commit 都會變動，嚴格比對會讓每次部署都失敗。
# 這道檢查真正要擋的是 pkgdef、vsct 與 Manifest 註冊已經改變卻沒重裝，
# 而那些變更一律伴隨 version.json 的 major.minor 調整，因此以 major.minor 為準。
$installedMajorMinor = Get-MajorMinor $installation.Version
$builtMajorMinor = Get-MajorMinor $builtVersion

if ($installedMajorMinor -ne $builtMajorMinor) {
    throw "VSIX 主版本不一致（已安裝 $($installation.Version)，建置 $builtVersion）。請重新安裝 Debug VSIX。"
}

# 命令表（選單項目、命令識別碼、鍵繫結）雖然編譯在 DLL 的資源裡，殼層卻是照
# pkgdef 的 "Menus.ctmenu, N" 那個 N 決定要不要重讀的。只換 DLL 的話 N 沒變，
# 殼層繼續用舊的命令表——症狀是新的選單項目不出現、新綁的鍵完全沒反應，
# 而且沒有任何錯誤訊息。與 MEF 快取是同一類的坑，但清快取救不了它：
# pkgdef 本身也不在部署清單裡，非重新安裝不可。
function Get-MenuResourceVersion {
    param([string]$PkgDefPath)

    if (-not (Test-Path -LiteralPath $PkgDefPath)) {
        return $null
    }

    $match = [regex]::Match(
        [System.IO.File]::ReadAllText($PkgDefPath),
        'Menus\.ctmenu,\s*(\d+)')

    return $match.Success ? $match.Groups[1].Value : $null
}

$builtMenuVersion = Get-MenuResourceVersion (Join-Path $outputPath 'SqlAssist.Ssms22.pkgdef')
$installedMenuVersion = Get-MenuResourceVersion (Join-Path $installation.Path 'SqlAssist.Ssms22.pkgdef')

if ($builtMenuVersion -and $installedMenuVersion -and $builtMenuVersion -ne $installedMenuVersion) {
    throw @"
命令表版本不一致（已安裝 $installedMenuVersion，建置 $builtMenuVersion）。
部署只會替換 DLL，不會更新 pkgdef，殼層會繼續使用舊的命令表——新的選單項目與
鍵繫結都不會生效，而且不會有任何錯誤。請改用：
  tools\Install-Extension.ps1 -Configuration Debug
"@
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
