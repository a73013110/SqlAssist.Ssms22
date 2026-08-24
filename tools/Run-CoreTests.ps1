[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 測試執行器由 global.json 的 test.runner 指定為 Microsoft.Testing.Platform。
# 以方案為目標，新增測試專案時不需要再改這支腳本。
dotnet test (Join-Path $root 'SqlAssist.Ssms22.sln') --configuration $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "核心測試失敗，結束代碼：$LASTEXITCODE"
}
