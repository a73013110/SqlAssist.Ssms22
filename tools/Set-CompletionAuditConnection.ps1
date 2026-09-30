#Requires -Version 7.0
<#
.SYNOPSIS
    設定召回稽核直連的伺服器；存在版控外，密碼以 DPAPI（目前使用者）加密。

.DESCRIPTION
    互動輸入伺服器 IP、埠、資料庫清單、登入名稱與密碼，寫到
    %LOCALAPPDATA%\SqlAssist\CompletionAudit\connection.json。重跑就是修改：每一欄直接按 Enter 沿用現值，
    密碼留空沿用原本那一份。登入名稱輸入 - 改用 Windows 驗證（不存密碼）。

    密碼以 ConvertFrom-SecureString 加密，只有同一台電腦、同一個 Windows 使用者解得開；
    排程工作也要以這個使用者執行。建議使用只有 CONNECT 與 VIEW DEFINITION 的唯讀登入。

.PARAMETER Show
    只印出非機密欄位（伺服器、埠、資料庫、登入名稱、是否信任憑證、有沒有存密碼），不修改。

.PARAMETER Path
    設定檔位置；預設是稽核工具讀的那一份。
#>
[CmdletBinding()]
param(
    [switch]$Show,
    [string]$Path = (Join-Path $env:LOCALAPPDATA 'SqlAssist\CompletionAudit\connection.json')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$current = if (Test-Path -LiteralPath $Path) {
    Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
}
else {
    $null
}

if ($Show) {
    if (-not $current) {
        Write-Host "尚未設定：$Path"
        return
    }

    [pscustomobject]@{
        設定檔       = $Path
        伺服器       = $current.server
        埠           = $current.port
        資料庫       = ($current.databases -join ', ')
        登入名稱     = if ($current.login) { $current.login } else { '（Windows 驗證）' }
        信任伺服器憑證 = [bool]$current.trustServerCertificate
        已存密碼     = [bool]$current.password
    } | Format-List
    return
}

function Read-Value([string]$Prompt, [string]$Default) {
    $suffix = if ($Default) { "（Enter 沿用 $Default）" } else { '' }
    $value = Read-Host "$Prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value.Trim()
}

$server = Read-Value '伺服器 IP 或名稱' $current.server
if (-not $server) { throw '伺服器不可空白。' }

$portText = Read-Value '埠（0 表示不指定，給具名執行個體）' $(if ($null -ne $current.port) { [string]$current.port } else { '1433' })
$port = 0
if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 0 -or $port -gt 65535) { throw "埠不是 0 到 65535 的整數：$portText" }

$databaseText = Read-Value '資料庫（逗號分隔）' ($current.databases -join ',')
$databases = @($databaseText -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($databases.Count -eq 0) { throw '至少要一個資料庫。' }

$login = Read-Value '登入名稱（輸入 - 改用 Windows 驗證）' $current.login
if ($login -eq '-') { $login = $null }

$password = $null
if ($login) {
    $secure = Read-Host '密碼（留空沿用原本的）' -AsSecureString
    if ($secure.Length -gt 0) {
        # 沒有給金鑰時用 DPAPI（CurrentUser）；稽核工具以 ProtectedData 在同一個使用者下解開。
        $password = ConvertFrom-SecureString -SecureString $secure
    }
    elseif ($current.password -and $current.login -eq $login) {
        $password = $current.password
    }
    else {
        throw '這個登入名稱還沒有存過密碼。'
    }
}

$trustDefault = if ($current -and $current.trustServerCertificate) { 'y' } else { 'n' }
$trust = (Read-Value '信任伺服器憑證，不驗證簽發者（y/n；自簽憑證的伺服器選 y）' $trustDefault) -match '^(y|yes)$'

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
[ordered]@{
    server                 = $server
    port                   = $port
    databases              = $databases
    login                  = $login
    password               = $password
    trustServerCertificate = $trust
} | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding utf8NoBOM

Write-Host "已寫入 $Path（密碼以 DPAPI 加密；用 -Show 檢視非機密欄位）。"
