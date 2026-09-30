#Requires -Version 7.0
<#
.SYNOPSIS
    以目前的使用者註冊（或移除）召回稽核的夜間排程。

.DESCRIPTION
    在 Windows 工作排程器建立每天一次的工作，執行 Audit-Completions.ps1 -Log，輸出另存到
    artifacts\completion-audit\last-run.log。只註冊，不立刻執行。

    工作以目前使用者、只在登入時執行：連線密碼是這個使用者的 DPAPI 加密，換成別的帳戶或
    S4U 都解不開。要登出也跑，到工作排程器把這個工作改成「不論使用者是否登入」並輸入 Windows 密碼。

.PARAMETER At
    每天幾點跑（24 小時制，例如 02:00）。

.PARAMETER MaxMinutes
    交給 Audit-Completions.ps1 的時間上限；排程的逾時再多給三十分鐘收尾與建置。

.PARAMETER Sources
    語料來源；預設全部。

.PARAMETER TaskName
    工作名稱。

.PARAMETER Unregister
    移除這個工作。
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d{1,2}:\d{2}$')]
    [string]$At = '02:00',
    [ValidateRange(1, 1440)]
    [int]$MaxMinutes = 120,
    [ValidateSet('recall', 'memory', 'modules', 'microsoft')]
    [string[]]$Sources = @('recall', 'memory', 'modules', 'microsoft'),
    [string]$SsmsInstallDir,
    [string]$TaskName = 'SqlAssist Completion Audit',
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "已移除排程：$TaskName"
    return
}

$root = Get-SqlAssistRoot
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$script = Join-Path $PSScriptRoot 'Audit-Completions.ps1'
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Log -MaxMinutes $MaxMinutes -Sources $($Sources -join ',')"

if ($SsmsInstallDir) {
    $arguments += " -SsmsInstallDir `"$SsmsInstallDir`""
}

$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $pwsh -Argument $arguments -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact($At, 'H:mm', [cultureinfo]::InvariantCulture))
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes ($MaxMinutes + 30))

Register-ScheduledTask `
    -TaskName $TaskName `
    -Description 'SqlAssist 建議清單召回稽核（tools\Audit-Completions.ps1）；報告在 artifacts\completion-audit。' `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Force | Out-Null

Write-Host "已註冊排程「$TaskName」：每天 $At 以 $user 執行（只在登入時），上限 $MaxMinutes 分。"
