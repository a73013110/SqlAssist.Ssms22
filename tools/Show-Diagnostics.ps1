#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$installed = Get-SqlAssistInstallation

if ($installed.Count -eq 0) {
    Write-Host '安裝狀態：找不到 SqlAssist。' -ForegroundColor Yellow
}
else {
    Write-Host '安裝狀態：' -ForegroundColor Cyan
    $installed | Format-Table -AutoSize
}

$logPath = Join-Path $env:LOCALAPPDATA 'SqlAssist.Ssms22\SqlAssist.log'

if (Test-Path -LiteralPath $logPath) {
    Write-Host "`n最近診斷紀錄：$logPath" -ForegroundColor Cyan
    Get-Content -LiteralPath $logPath -Tail 50
}
else {
    Write-Host "`n尚無診斷紀錄：$logPath" -ForegroundColor Yellow
    Write-Host '安裝擴充並重新啟動 SSMS 後才會產生紀錄。'
}
