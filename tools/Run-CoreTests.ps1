[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 測試執行器由 global.json 的 test.runner 指定為 Microsoft.Testing.Platform。
dotnet test (Join-Path $root 'tests\SqlAssist.Core.Tests\SqlAssist.Core.Tests.csproj') `
    --configuration $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "核心測試失敗，結束代碼：$LASTEXITCODE"
}
