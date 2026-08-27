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

Write-Host 'Debug 組件部署完成：' -ForegroundColor Green
Write-Host $installation.Path
Write-Host '現在可在 Visual Studio 按 F5 啟動 SSMS。'
