$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet run `
    --project (Join-Path $root 'tests\SqlAssist.Core.Tests\SqlAssist.Core.Tests.csproj') `
    --configuration Release

if ($LASTEXITCODE -ne 0) {
    throw "核心測試失敗，結束代碼：$LASTEXITCODE"
}

