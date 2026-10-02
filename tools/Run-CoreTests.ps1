#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output
$root = Split-Path -Parent $PSScriptRoot

# 測試執行器由 global.json 的 test.runner 指定為 Microsoft.Testing.Platform。
# 以方案為目標，新增測試專案時不需要再改這支腳本。
# global.json 從目前目錄往上找；在 repo 外呼叫會退回 VSTest 模式，每個專案都報錯。
# 方案一定要用 --solution：MTP 模式把位置引數轉給各測試程式，不拿來選方案。
Push-Location $root
try {
    dotnet test --solution (Join-Path $root 'SqlAssist.Ssms22.sln') --configuration $Configuration
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($exitCode -ne 0) {
    throw "核心測試失敗，結束代碼：$exitCode"
}
