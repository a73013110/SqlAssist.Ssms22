#Requires -Version 7.0
<#
.SYNOPSIS
    建議清單召回稽核：在語料每個詞元的起點走產品路徑，作者寫的下一個詞不在清單就記一筆。

.DESCRIPTION
    先以目前的原始碼建置稽核執行器（稽核的就是這一份程式），再跑語料、寫報告到
    artifacts\completion-audit\<yyyyMMdd-HHmm>\：clusters.md 給 AI 審查，raw.jsonl 是完整紀錄。
    方法、語料來源與晨間審查流程見 docs\completion-audit.md。

    結束碼：有漏不算失敗（0）；工具本身出錯是 1、參數不對是 2。

.PARAMETER SsmsInstallDir
    SSMS 22 的安裝目錄；預設由 SqlAssist.Tools.psm1 取得。

.PARAMETER Sources
    語料來源：recall、memory、modules、microsoft；預設全部（沒有連線設定時 modules 自動略過）。

.PARAMETER Database
    只連這幾個資料庫；預設是連線設定裡的全部。

.PARAMETER MaxMinutes
    時間上限；到了就停，做完的段已經進快取，下一次接著跑。

.PARAMETER Cluster
    只重驗這一群：找最近一次記到它的原始紀錄，每一筆用目前的程式再問一次。

.PARAMETER RawNames
    報告裡照列原名；預設換成代號。

.PARAMETER NoCache
    不讀快取，全部重跑。

.PARAMETER Parallel
    同時跑幾段；預設是處理器數的一半。

.PARAMETER ConnectionFile
    另一份連線設定；預設是 Set-CompletionAuditConnection.ps1 寫的那一份。

.PARAMETER Log
    整份輸出另存到 artifacts\completion-audit\last-run.log（排程用）。

.EXAMPLE
    .\tools\Audit-Completions.ps1 -Sources recall -MaxMinutes 5

.EXAMPLE
    .\tools\Audit-Completions.ps1 -Cluster 1c63f62a
#>
[CmdletBinding()]
param(
    [string]$SsmsInstallDir,
    # 不用 ValidateSet：排程以 -File 呼叫時陣列傳成一個逗號字串，由執行器驗證名稱。
    [string[]]$Sources = @('recall', 'memory', 'modules', 'microsoft'),
    [string[]]$Database,
    [ValidateRange(0.1, 1440)]
    [double]$MaxMinutes = 60,
    [string]$Cluster,
    [switch]$RawNames,
    [switch]$NoCache,
    [ValidateRange(0, 64)]
    [int]$Parallel = 0,
    [string]$ConnectionFile,
    [switch]$Log
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$root = Get-SqlAssistRoot
$ssmsPath = Get-SsmsInstallPath -InstallDir $SsmsInstallDir -Require
$project = Join-Path $root 'tools\SqlAssist.CompletionAudit.Runner\SqlAssist.CompletionAudit.Runner.csproj'
$runner = Join-Path $root 'tools\SqlAssist.CompletionAudit.Runner\bin\Release\net48\SqlAssist.CompletionAudit.Runner.exe'
$output = Join-Path $root 'artifacts\completion-audit'

if ($Log) {
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Start-Transcript -LiteralPath (Join-Path $output 'last-run.log') -Force | Out-Null
}

try {
    # 只建執行器與它參照的純邏輯專案；VSIX 與它的 obj 不碰（那一份一律走 Build-Extension.ps1）。
    dotnet build $project --configuration Release --nologo --verbosity quiet "-p:SsmsInstallDir=$ssmsPath"

    if ($LASTEXITCODE -ne 0) {
        throw "稽核執行器建置失敗，結束代碼：$LASTEXITCODE"
    }

    $arguments = @(
        '--repo', $root,
        '--ide', (Join-Path $ssmsPath 'Common7\IDE'),
        '--sources', (($Sources | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) -join ','),
        '--max-minutes', $MaxMinutes.ToString([cultureinfo]::InvariantCulture)
    )

    if ($Database) { $arguments += @('--database', ($Database -join ',')) }
    if ($Cluster) { $arguments += @('--cluster', $Cluster) }
    if ($RawNames) { $arguments += '--raw-names' }
    if ($NoCache) { $arguments += '--no-cache' }
    if ($Parallel -gt 0) { $arguments += @('--parallel', $Parallel) }
    if ($ConnectionFile) { $arguments += @('--connection', (Resolve-Path -LiteralPath $ConnectionFile).Path) }

    & $runner @arguments
    $code = $LASTEXITCODE
}
finally {
    if ($Log) {
        Stop-Transcript | Out-Null
    }
}

exit $code
