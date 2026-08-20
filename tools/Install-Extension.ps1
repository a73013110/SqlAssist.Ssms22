[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ssmsPath = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release'
$installer = Join-Path $ssmsPath 'Common7\IDE\VSIXInstaller.exe'
$vsix = Join-Path $root "src\SqlAssist.Ssms22\bin\$Configuration\net48\SqlAssist.Ssms22.vsix"

if (Get-Process -Name 'SSMS' -ErrorAction SilentlyContinue) {
    throw '請先關閉所有 SSMS 視窗，再執行安裝。'
}

if (-not (Test-Path -LiteralPath $installer)) {
    throw "找不到 SSMS VSIX 安裝程式：$installer"
}

if (-not (Test-Path -LiteralPath $vsix)) {
    throw "找不到 VSIX，請先執行 tools\Build-Extension.ps1：$vsix"
}

& (Join-Path $PSScriptRoot 'Test-VsixPackage.ps1') -VsixPath $vsix

# 顯示官方 VSIXInstaller 介面，讓使用者確認安裝目標與權限。
$process = Start-Process -FilePath $installer -ArgumentList @("`"$vsix`"") -Wait -PassThru

if ($process.ExitCode -ne 0) {
    throw "VSIXInstaller 結束代碼：$($process.ExitCode)"
}

