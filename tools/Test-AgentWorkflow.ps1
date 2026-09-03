#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$testRoot = Join-Path $root ('artifacts/agent-workflow-tests/' + [guid]::NewGuid().ToString('N') + ' 空白 [案例]')
[void][System.IO.Directory]::CreateDirectory($testRoot)
$utf8 = [System.Text.UTF8Encoding]::new($false)
$script:checks = 0

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "驗證失敗：$Message" }
    $script:checks++
}

function Write-Fixture([string]$Name, [string]$Content) {
    $path = Join-Path $testRoot $Name
    [System.IO.File]::WriteAllText($path, ($Content -replace "`r", ''), $utf8)
    return $path
}

function Invoke-TestScript([string]$Script, [hashtable]$Parameters) {
    # 獨立程序才能驗證真正的結束碼，不能讓被測腳本的 exit 終止測試本身。
    $statement = "& '$($Script.Replace("'", "''"))'"
    foreach ($key in $Parameters.Keys) {
        $values = @($Parameters[$key] | ForEach-Object { "'$(([string]$_).Replace("'", "''"))'" })
        $value = if ($Parameters[$key] -is [array]) { '@(' + ($values -join ',') + ')' } else { $values[0] }
        $statement += " -$key $value"
    }
    $statement += '; exit $LASTEXITCODE'
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $pwsh
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($value in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand',
        [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($statement)))) {
        $info.ArgumentList.Add($value)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "測試逾時：$Script"
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Out = $stdout.GetAwaiter().GetResult()
            Error = $stderr.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

function Invoke-QuietCase([string]$Name, [string]$Fixture, [hashtable]$Extra = @{}) {
    $logRoot = Join-Path $testRoot $Name
    $parameters = @{ ScriptPath = $Fixture; LogDirectory = $logRoot; MaxChars = 2048; WorkingDirectory = $testRoot }
    foreach ($key in $Extra.Keys) { $parameters[$key] = $Extra[$key] }
    $run = Invoke-TestScript (Join-Path $PSScriptRoot 'Invoke-QuietCommand.ps1') $parameters
    $json = @(Get-ChildItem -LiteralPath $logRoot -Filter 'result.json' -Recurse)
    Assert-Condition ($json.Count -eq 1) "$Name 必須有唯一結果檔"
    return [pscustomobject]@{ Run = $run; Result = (Get-Content -LiteralPath $json[0].FullName -Raw | ConvertFrom-Json) }
}

$noisy = Write-Fixture '大量輸出.ps1' @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
for ($i = 0; $i -lt 2000; $i++) {
    [Console]::Out.WriteLine(('OUT-{0:D4} 中文 ' -f $i) + ('x' * 64))
    [Console]::Error.WriteLine(('ERR-{0:D4} 中文 ' -f $i) + ('y' * 64))
}
exit 0
'@
$case = Invoke-QuietCase 'large' $noisy
Assert-Condition ($case.Run.ExitCode -eq 0) '大量雙串流輸出不可失敗或死鎖'
Assert-Condition ($case.Run.Out.Length -le 2048 -and $case.Run.Error.Length -eq 0) '輸出必須受字元預算約束'
Assert-Condition ($case.Run.Out.Contains('中文')) 'UTF-8 預覽不可出現亂碼'
$fullOut = [System.IO.File]::ReadAllText($case.Result.stdout_path)
$fullError = [System.IO.File]::ReadAllText($case.Result.stderr_path)
Assert-Condition ($fullOut.Contains('OUT-0000 中文') -and $fullOut.Contains('OUT-1999 中文')) 'stdout 頭尾與 Unicode 必須完整'
Assert-Condition ($fullError.Contains('ERR-0000 中文') -and $fullError.Contains('ERR-1999 中文')) 'stderr 頭尾與 Unicode 必須完整'
Assert-Condition ($case.Result.stdout_bytes -eq (Get-Item -LiteralPath $case.Result.stdout_path).Length) '紀錄大小必須正確'
Assert-Condition ($fullOut.Length + $fullError.Length -gt $case.Run.Out.Length * 20) '預覽必須確實節流，而非只把完整紀錄換個位置印出'
$sampleRawChars = $fullOut.Length + $fullError.Length
$samplePreviewChars = $case.Run.Out.Length

$failure = Write-Fixture '失敗.ps1' "[Console]::WriteLine('passed success'); [Console]::Error.WriteLine('EXPECTED FAILURE'); exit 23`n"
$case = Invoke-QuietCase 'failure' $failure
Assert-Condition ($case.Run.ExitCode -eq 23 -and $case.Result.exit_code -eq 23) '不得用成功字樣蓋掉原結束碼'
Assert-Condition ($case.Run.Out.Contains('EXPECTED FAILURE')) '失敗的 stderr 必須可見'

$arguments = Write-Fixture '引數.ps1' "[Console]::OutputEncoding = [Text.UTF8Encoding]::new(`$false); [Console]::WriteLine((ConvertTo-Json -InputObject @(`$args) -Compress)); exit 0`n"
$values = @('含 空白', '雙"引號;與&符號', "單'引號", '', '末尾\')
$case = Invoke-QuietCase 'arguments' $arguments @{ Arguments = $values }
$actual = @(Get-Content -LiteralPath $case.Result.stdout_path -Raw | ConvertFrom-Json)
Assert-Condition ($actual.Count -eq $values.Count) '空引數不得消失'
for ($i = 0; $i -lt $values.Count; $i++) {
    Assert-Condition ($actual[$i] -ceq $values[$i]) "第 $i 個引數不得經 shell 重新解釋"
}
$named = Write-Fixture '具名引數.ps1' "param([ValidateSet('Debug','Release')][string]`$Configuration = 'Release'); [Console]::WriteLine(`$Configuration); exit 0`n"
$case = Invoke-QuietCase 'named-arguments' $named @{ Arguments = @('-Configuration', 'Debug') }
Assert-Condition ($case.Run.ExitCode -eq 0 -and (Get-Content -LiteralPath $case.Result.stdout_path -Raw).Trim() -eq 'Debug') '既有腳本的具名引數必須原樣傳遞'

$silent = Write-Fixture '安靜.ps1' "exit 0`n"
$case = Invoke-QuietCase 'silent' $silent @{ TailLines = 0 }
Assert-Condition ($case.Run.ExitCode -eq 0 -and $case.Result.stdout_bytes -eq 0 -and $case.Result.stderr_bytes -eq 0) '沒有輸出不等於失敗'
Assert-Condition (-not $case.Run.Out.Contains('--- stdout')) 'TailLines=0 只印狀態與紀錄位置'

if ($IsWindows) {
    $oem = Write-Fixture 'OEM.ps1' @'
$encoding = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
$bytes = $encoding.GetBytes('預覽編碼正常')
$stream = [Console]::OpenStandardOutput()
$stream.Write($bytes, 0, $bytes.Length)
$stream.Flush()
exit 0
'@
    $case = Invoke-QuietCase 'oem' $oem
    # 某些系統 OEM 編碼不支援中文，只對能往返的系統核對文字，不能把測試綁死台灣地區。
    $encoding = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
    $expectedText = $encoding.GetString($encoding.GetBytes('預覽編碼正常'))
    Assert-Condition ($case.Run.Out.Contains($expectedText)) 'OEM 預覽必須可讀，原始位元組仍保留'
    $mixed = Write-Fixture '混合編碼.ps1' @'
$stream = [Console]::OpenStandardOutput()
$utf8 = [Text.UTF8Encoding]::new($false)
$oem = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
foreach ($encoding in @($utf8, $oem)) {
    $bytes = $encoding.GetBytes("混合編碼測試`n")
    $stream.Write($bytes, 0, $bytes.Length)
}
$stream.Flush()
exit 0
'@
    $case = Invoke-QuietCase 'mixed-encoding' $mixed
    $expectedOem = $encoding.GetString($encoding.GetBytes('混合編碼測試'))
    $expectedCount = if ($expectedOem -eq '混合編碼測試') { 2 } else { 1 }
    Assert-Condition ($case.Run.ExitCode -eq 0 -and ([regex]::Matches($case.Run.Out, '混合編碼測試')).Count -eq $expectedCount) '混合串流不能因一行 OEM 把所有 UTF-8 行解錯'
    Assert-Condition ($case.Run.Out.Contains($expectedOem)) '混合串流的 OEM 行也必須可讀'
}

$huge = Write-Fixture '超長輸出.ps1' "[Console]::Write(('x' * 200000)); exit 0`n"
$case = Invoke-QuietCase 'huge-line' $huge
Assert-Condition ($case.Run.Out.Length -le 2048 -and $case.Result.stdout_bytes -eq 200000) '超長單行仍保留原檔且預覽有界'
Assert-Condition ($case.Run.Out.Contains('128 KiB')) '超出尾窗的長行必須明確告知省略'

$sleep = Write-Fixture '逾時.ps1' "Start-Sleep -Seconds 10`n"
$case = Invoke-QuietCase 'timeout' $sleep @{ TimeoutSeconds = 1 }
Assert-Condition ($case.Run.ExitCode -eq 124 -and $case.Result.timed_out) '逾時必須明確標記且回傳 124'

$missing = Invoke-TestScript (Join-Path $PSScriptRoot 'Invoke-QuietCommand.ps1') @{ Command = 'sqlassist-command-does-not-exist'; LogDirectory = $testRoot }
Assert-Condition ($missing.ExitCode -eq 125) '啟動失敗不得宣稱命令成功'

$reader = Join-Path $PSScriptRoot 'Read-Context.ps1'
$source = Write-Fixture '來源 [範例].txt' ((1..120 | ForEach-Object { "內容$_ " + ('字' * 50) }) -join "`n")
$beforeHash = (Get-FileHash -LiteralPath $source).Hash
$read = Invoke-TestScript $reader @{ Path = $source; StartLine = 2; LineCount = 2 }
Assert-Condition ($read.ExitCode -eq 0 -and $read.Out.Contains('2: 內容2') -and $read.Out.Contains('3: 內容3')) '區段行號必須精確'
Assert-Condition (-not $read.Out.Contains('1: 內容1') -and $read.Out.Contains('-StartLine 4')) '區段不得多讀，且要提供續讀位置'
$read = Invoke-TestScript $reader @{ Path = $source; LineCount = 120; MaxChars = 1024 }
Assert-Condition ($read.ExitCode -eq 0 -and $read.Out.Length -le 1024 -and $read.Out.Contains('尚有內容未顯示')) '行數與字元限制必須同時生效'
Assert-Condition ((Get-FileHash -LiteralPath $source).Hash -eq $beforeHash) '讀取工具不可改動來源'
$long = Write-Fixture '超長單行.txt' ('x' * 3000)
$read = Invoke-TestScript $reader @{ Path = $long; MaxChars = 1024 }
Assert-Condition ($read.ExitCode -eq 2 -and -not $read.Out.Contains('xxxx')) '不能把截斷的半行冒充完整資料'
$empty = Write-Fixture '空檔.txt' ''
$read = Invoke-TestScript $reader @{ Path = $empty }
Assert-Condition ($read.ExitCode -eq 0 -and $read.Out.Contains('已到檔尾')) '空檔可正常讀取'
$read = Invoke-TestScript $reader @{ Path = $source; StartLine = 200 }
Assert-Condition ($read.ExitCode -eq 1) '不存在的起始行必須失敗'
$read = Invoke-TestScript $reader @{ Path = (Join-Path $testRoot '不存在.txt') }
Assert-Condition ($read.ExitCode -eq 1) '不存在的檔案必須失敗'
$invalid = Join-Path $testRoot '無效編碼.txt'
[System.IO.File]::WriteAllBytes($invalid, [byte[]]@(0x80, 0x0A))
$read = Invoke-TestScript $reader @{ Path = $invalid }
Assert-Condition ($read.ExitCode -eq 1) '無效 UTF-8 不可靜默換成問號'
$utf16 = Join-Path $testRoot 'UTF16.txt'
[System.IO.File]::WriteAllText($utf16, '不是 UTF-8', [Text.Encoding]::Unicode)
$read = Invoke-TestScript $reader @{ Path = $utf16 }
Assert-Condition ($read.ExitCode -eq 1) 'BOM 不可讓非 UTF-8 來源繞過讀取編碼要求'

$docsRoot = Join-Path $testRoot 'docs-fixture'
[void][System.IO.Directory]::CreateDirectory((Join-Path $docsRoot 'docs'))
foreach ($name in @('README.md', 'CLAUDE.md', 'AGENTS.md')) {
    [System.IO.File]::WriteAllText((Join-Path $docsRoot $name), "# 規則`n`n## 規則`n", $utf8)
}
$indexPath = Join-Path $docsRoot 'docs/index.md'
$validIndex = @'
# 索引

[本機](../CLAUDE.md#規則-1)
[官方來源](https://example.invalid/README.md)

```text
# 不是真正標題
[不該檢查的範例](不存在.md)
```
'@
[System.IO.File]::WriteAllText($indexPath, $validIndex + "`n", $utf8)
$checker = Join-Path $PSScriptRoot 'Check-Docs.ps1'
$check = Invoke-TestScript $checker @{ Root = $docsRoot }
Assert-Condition ($check.ExitCode -eq 0) '檢查器須支援根目錄錨點、重複標題、外部來源與程式碼區塊'
[System.IO.File]::AppendAllText($indexPath, "`n[錯誤錨點](#不是真正標題)`n", $utf8)
$check = Invoke-TestScript $checker @{ Root = $docsRoot }
Assert-Condition ($check.ExitCode -ne 0 -and $check.Out.Contains('錨點不存在')) '程式碼區塊不得提供假錨點'
[System.IO.File]::WriteAllText($indexPath, $validIndex + "`n", $utf8)
$check = Invoke-TestScript $checker @{ Root = $docsRoot; IndexMdBudget = 10 }
Assert-Condition ($check.ExitCode -ne 0 -and $check.Out.Contains('docs/index.md')) '短索引預算必須能阻擋膨脹'
[System.IO.File]::AppendAllText((Join-Path $docsRoot 'AGENTS.md'), "`n[錯誤](docs/不存在.md)`n", $utf8)
$check = Invoke-TestScript $checker @{ Root = $docsRoot }
Assert-Condition ($check.ExitCode -ne 0 -and $check.Out.Contains('AGENTS.md')) 'Codex 入口的壞連結也必須阻擋'

Write-Host "AI 工作流程驗證通過：$script:checks 項；合成輸出 $sampleRawChars → $samplePreviewChars 字元（不是模型 token 量測）。"
Write-Host "測試紀錄：$testRoot"
