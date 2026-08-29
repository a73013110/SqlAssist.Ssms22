#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$SsmsInstallDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force

Assert-SsmsClosed -Action '執行安裝'
$installer = Get-SsmsVsixInstaller -InstallDir $SsmsInstallDir
$vsix = Get-SqlAssistVsixPath -Configuration $Configuration

if (-not (Test-Path -LiteralPath $vsix)) {
    throw "找不到 VSIX，請先執行 tools\Build-Extension.ps1：$vsix"
}

& (Join-Path $PSScriptRoot 'Test-VsixPackage.ps1') -VsixPath $vsix

# 顯示官方 VSIXInstaller 介面，讓使用者確認安裝目標與權限。
$process = Start-Process -FilePath $installer -ArgumentList @("`"$vsix`"") -Wait -PassThru

if ($process.ExitCode -ne 0) {
    throw "VSIXInstaller 結束代碼：$($process.ExitCode)"
}
