#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [switch]$Quiet,
    [string]$SsmsInstallDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force

$extensionId = Get-SqlAssistExtensionId

Assert-SsmsClosed -Action '執行解除安裝'
$installer = Get-SsmsVsixInstaller -InstallDir $SsmsInstallDir
$installed = Get-SqlAssistInstallation -ExtensionId $extensionId

if ($installed.Count -eq 0) {
    Write-Host '解除安裝略過：目前找不到 SqlAssist for SSMS 22。' -ForegroundColor Yellow
    return
}

Write-Host '即將解除安裝：' -ForegroundColor Cyan
$installed | Format-Table -AutoSize

if (-not $PSCmdlet.ShouldProcess(
        'SqlAssist for SSMS 22',
        "使用 SSMS VSIXInstaller 解除安裝延伸模組 $extensionId")) {
    return
}

$arguments = @("/uninstall:$extensionId") # 依 VSIX Identity 精確解除安裝 SqlAssist。

if ($Quiet) {
    $arguments += '/quiet'
    $process = Start-Process `
        -FilePath $installer `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
}
else {
    # 預設顯示 SSMS 官方解除安裝介面，讓使用者確認操作。
    $process = Start-Process `
        -FilePath $installer `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
}

if ($process.ExitCode -ne 0) {
    throw "VSIXInstaller 解除安裝失敗，結束代碼：$($process.ExitCode)"
}

$remaining = Get-SqlAssistInstallation -ExtensionId $extensionId

if ($remaining.Count -gt 0) {
    Write-Warning '仍偵測到 SqlAssist；可能已取消操作，或解除安裝程序尚未完成。'
    $remaining | Format-Table -AutoSize
    return
}

Write-Host 'SqlAssist for SSMS 22 已解除安裝。' -ForegroundColor Green
Write-Host '使用者設定與診斷紀錄仍保留於：' -ForegroundColor DarkGray
Write-Host (Join-Path $env:LOCALAPPDATA 'SqlAssist.Ssms22') -ForegroundColor DarkGray
