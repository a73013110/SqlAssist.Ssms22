#Requires -Version 7.0
<#
.SYNOPSIS
    以 ScriptDom 產生 T-SQL 關鍵字目錄（SqlKeywordCatalog.Generated.cs）。

.DESCRIPTION
    關鍵字清單刻意不手寫，改由 Microsoft 自己的剖析器推導，換版本重跑即可更新。
    六個階段、片語的宣告與產物的寫出都在 tools/SqlAssist.KeywordGenerator（入口是 CatalogGenerator）；
    這支腳本只找到 SSMS 那一份 ScriptDom、建置並載入產生器，再把參數交過去。
    要新增片語或樣板，改的是那個專案 Data 資料夾裡的宣告。

.PARAMETER SsmsInstallDir
    SSMS 22 安裝路徑。ScriptDom 隨 SSMS 附帶，不必另外安裝。

.PARAMETER OutputPath
    產出的 .cs 檔路徑。

.PARAMETER CachePath
    剖析結果快取的路徑，預設在不進版控的 artifacts/cache/。沒有快取的第一次約二十多分鐘，
    之後只剖析新出現的文字；ScriptDom 換版本時整份自動作廢。跑的途中每兩分鐘存一次，
    中途失敗也會存，中斷或失敗之後重跑從存下的地方接著算。

.PARAMETER NoCache
    不讀舊快取、全部重新剖析，結果照樣寫回快取。懷疑快取與剖析器不一致時用。

.NOTES
    產物要進版控。剖析要上百萬次，建置時不跑，手動執行、結果 commit 進去。
    產生器與 Core 一樣以 NuGet 的 ScriptDom 編譯，執行期載入 SSMS 安裝目錄那一份（組件身分相同）：
    SourceVersion 記的是 SSMS 帶的版本，換 SSMS 版本重跑就跟著換，不必等 NuGet 發版。
#>
[CmdletBinding()]
param(
    [string]$SsmsInstallDir,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\SqlAssist.Core\Keywords\SqlKeywordCatalog.Generated.cs'),
    [string]$CachePath = (Join-Path $PSScriptRoot '..\artifacts\cache\Generate-Keywords.cache'),
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$SsmsInstallDir = Get-SsmsInstallPath -InstallDir $SsmsInstallDir
$scriptDomPath = Join-Path $SsmsInstallDir 'Common7\IDE\Extensions\Application\Microsoft.SqlServer.TransactSql.ScriptDom.dll'

if (-not (Test-Path $scriptDomPath)) {
    throw "找不到 ScriptDom：$scriptDomPath。請以 -SsmsInstallDir 指定 SSMS 22 的安裝路徑。"
}

# 組件載進來就換不掉：同一個工作階段重跑的話，建置覆寫不了被鎖住的檔案，載入的也還是舊的那一份。
if ('SqlAssist.KeywordGenerator.CatalogGenerator' -as [type]) {
    throw '產生器已經載入這個 PowerShell 工作階段，改過的程式載不進來；請以 pwsh -File 在新的程序執行。'
}

$generatorProject = Join-Path $PSScriptRoot 'SqlAssist.KeywordGenerator\SqlAssist.KeywordGenerator.csproj'
dotnet build $generatorProject --configuration Release --nologo --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    throw "產生器建置失敗，結束代碼：$LASTEXITCODE"
}

# 先載入 SSMS 那一份 ScriptDom：產生器以 NuGet 的版本編譯，組件身分相同，執行期就綁到已經載入的這一份。
$assembly = [System.Reflection.Assembly]::LoadFrom($scriptDomPath)
Add-Type -Path (Join-Path $PSScriptRoot 'SqlAssist.KeywordGenerator\bin\Release\netstandard2.0\SqlAssist.KeywordGenerator.dll')

$options = [SqlAssist.KeywordGenerator.GeneratorOptions]@{
    ScriptDomPath     = $assembly.Location
    OutputPath        = [System.IO.Path]::GetFullPath($OutputPath)
    CachePath         = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CachePath)
    UseCache          = -not $NoCache
    StatementDocsPath = Join-Path $PSScriptRoot '..\src\SqlAssist.Core\Keywords\BuiltInDocs\statements.json'
    FunctionDocsPath  = Join-Path $PSScriptRoot '..\src\SqlAssist.Core\Keywords\BuiltInDocs\functions.json'
}

$log = [SqlAssist.KeywordGenerator.GeneratorLog]::new(
    { param($message) Write-Host $message },
    { param($message) Write-Warning $message },
    {
        param($status)

        if ($null -eq $status) {
            Write-Progress -Activity '探測子句片語' -Completed
        }
        else {
            Write-Progress -Activity '探測子句片語' -Status $status
        }
    })

[SqlAssist.KeywordGenerator.CatalogGenerator]::Run($options, $log)
