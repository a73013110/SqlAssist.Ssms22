#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$SsmsInstallDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force

$root = Get-SqlAssistRoot
$ssmsPath = Get-SsmsInstallPath -InstallDir $SsmsInstallDir -Require
$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Test-Path -LiteralPath $vsWhere)) {
    throw '找不到 vswhere.exe。'
}

$visualStudioPath = & $vsWhere `
    -latest `
    -products Microsoft.VisualStudio.Product.Enterprise `
    -version '[18.0,19.0)' `
    -property installationPath

if (-not $visualStudioPath) {
    throw '找不到 Visual Studio 18。'
}

$msBuild = Join-Path $visualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'

# 命令表掛錯層不會編譯失敗，選單只是安靜地不出現，因此在建置前先驗證。
& (Join-Path $PSScriptRoot 'Test-CommandTable.ps1')

# 明確使用方案的 x64 組態，統一輸出到 bin\x64。
& $msBuild `
    (Join-Path $root 'SqlAssist.Ssms22.sln') `
    /restore `
    /m `
    /v:minimal `
    "/p:Configuration=$Configuration" `
    "/p:Platform=x64" `
    "/p:SsmsInstallDir=$ssmsPath"

if ($LASTEXITCODE -ne 0) {
    throw "建置失敗，結束代碼：$LASTEXITCODE"
}

$vsix = Get-SqlAssistVsixPath -Configuration $Configuration

if (-not (Test-Path -LiteralPath $vsix)) {
    throw "建置完成但找不到 VSIX：$vsix"
}

& (Join-Path $PSScriptRoot 'Test-VsixPackage.ps1') -VsixPath $vsix
Write-Host "VSIX：$vsix"
