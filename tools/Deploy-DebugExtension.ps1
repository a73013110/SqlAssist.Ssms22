#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$SsmsInstallDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Deployment.psm1') -Force

$outputPath = Join-Path (Get-SqlAssistRoot) 'src\SqlAssist.Ssms22\bin\x64\Debug\net48'
Assert-SsmsClosed -Action '部署 Debug 組件'

if (-not $SkipBuild) {
    # 預設先建立完整 Debug VSIX，確保 DLL、PDB 與目前原始碼一致。
    & (Join-Path $PSScriptRoot 'Build-Extension.ps1') -Configuration Debug -SsmsInstallDir $SsmsInstallDir
}

$installations = Get-SqlAssistInstallation

if ($installations.Count -eq 0) {
    throw '找不到已安裝的 SqlAssist。請先執行 Install-Extension.ps1 -Configuration Debug。'
}

if ($installations.Count -gt 1) {
    $paths = ($installations.Path | ForEach-Object { "  $_" }) -join [Environment]::NewLine
    throw "找到多個 SqlAssist 安裝目錄，請先移除舊版本：$([Environment]::NewLine)$paths"
}

$installation = $installations[0]

# 安裝資產不覆寫；完整預檢及 SHA-256 驗證由 fixture 共用的部署實作負責。
$deployed = @(Invoke-SqlAssistDebugFileDeployment -OutputPath $outputPath -InstallationPath $installation.Path)

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
    # 遞迴刪除前確認絕對路徑仍在本次安裝的 hive，且快取本身不是目錄連結。
    $cache = [IO.Path]::GetFullPath($cache)
    $hivePrefix = [IO.Path]::GetFullPath($hiveRoot).TrimEnd('\') + '\'
    if (-not $cache.StartsWith($hivePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "快取路徑超出安裝 hive：$cache"
    }
    if (-not (Test-Path -LiteralPath $cache)) {
        continue
    }
    if ((Get-Item -LiteralPath $cache -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        Write-Warning "略過連結快取，請人工確認：$cache"
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
Write-Host "已驗證 $($deployed.Count) 個部署檔案的 SHA-256。"

foreach ($cache in $cleared) {
    Write-Host "已清除快取：$cache" -ForegroundColor DarkGray
}

Write-Host '現在可在 Visual Studio 按 F5 啟動 SSMS（首次啟動會重建快取，會慢幾秒）。'
