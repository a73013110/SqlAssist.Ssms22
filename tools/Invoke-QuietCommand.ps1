#Requires -Version 7.0

<#
.SYNOPSIS
完整紀錄留在磁碟，只給 AI 短輸出；不縮減測試，也不吞失敗結束碼。
.DESCRIPTION
不是建置／測試本身；用 -ScriptPath 包住既有 PS1。不可包 MCP 或互動命令。
.EXAMPLE
./tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Run-CoreTests.ps1
#>
[CmdletBinding(DefaultParameterSetName = 'Native')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Native')]
    [string]$Command,
    [Parameter(Mandatory, ParameterSetName = 'Script')]
    [string]$ScriptPath,
    [AllowEmptyCollection()]
    [AllowEmptyString()]
    [string[]]$Arguments = @(),
    [string]$WorkingDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$LogDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/ai-logs'),
    [ValidateRange(0, 200)]
    [int]$TailLines = 20,
    [ValidateRange(1024, 8000)]
    [int]$MaxChars = 6000,
    [string]$PreviewEncoding = 'auto',
    [ValidateRange(1, 86400)]
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$process = $null
$stdoutFile = $null
$stderrFile = $null
$started = $false
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$exitCode = 125
$timedOut = $false

function Get-AutoPreviewTail([string]$Path, [int]$Count) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        # 只讀有界尾窗，避免單行數百 MB 的紀錄讓「摘要工具」自己吃滿記憶體。
        $size = [int][Math]::Min($stream.Length, 131072)
        $windowed = $stream.Length -gt $size
        $buffer = [byte[]]::new($size)
        [void]$stream.Seek(-$size, [System.IO.SeekOrigin]::End)
        $read = 0
        while ($read -lt $size) {
            $n = $stream.Read($buffer, $read, $size - $read)
            if ($n -eq 0) { break }
            $read += $n
        }
    }
    finally { $stream.Dispose() }

    $ranges = [System.Collections.Generic.List[object]]::new()
    $end = $read
    if ($end -gt 0 -and $buffer[$end - 1] -eq 10) { $end-- }
    for ($i = $end - 1; $i -ge 0 -and $ranges.Count -lt $Count; $i--) {
        if ($buffer[$i] -eq 10) {
            $ranges.Add(@{ Start = $i + 1; Length = $end - $i - 1 })
            $end = $i
        }
    }
    if ($ranges.Count -lt $Count -and -not $windowed -and $read -gt 0) {
        $ranges.Add(@{ Start = 0; Length = $end })
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $encodings = [System.Collections.Generic.HashSet[string]]::new()
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $oem = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
    for ($i = $ranges.Count - 1; $i -ge 0; $i--) {
        $range = $ranges[$i]
        try {
            $line = $strictUtf8.GetString($buffer, $range.Start, $range.Length)
            [void]$encodings.Add('utf-8')
        }
        catch [System.Text.DecoderFallbackException] {
            # 同一份 Windows 建置紀錄可能混有 pwsh 的 OEM 與 MSBuild 的 UTF-8，不能整檔只猜一次。
            $line = $oem.GetString($buffer, $range.Start, $range.Length)
            [void]$encodings.Add('oem')
        }
        $lines.Add($line.TrimEnd("`r"))
    }
    if ($windowed -and $ranges.Count -lt $Count) {
        $lines.Insert(0, '較早的內容或不完整長行超過 128 KiB 尾窗，未顯示；請查原始紀錄。')
    }
    return [pscustomobject]@{ Lines = $lines.ToArray(); Label = (($encodings | Sort-Object) -join '/') + '（逐行）' }
}

try {
    $cwd = (Resolve-Path -LiteralPath $WorkingDirectory).ProviderPath
    if (-not (Test-Path -LiteralPath $cwd -PathType Container)) { throw 'WorkingDirectory 必須是目錄。' }

    if ($PSCmdlet.ParameterSetName -eq 'Script') {
        $scriptFile = if ([System.IO.Path]::IsPathRooted($ScriptPath)) { $ScriptPath } else { Join-Path $cwd $ScriptPath }
        $scriptFile = (Resolve-Path -LiteralPath $scriptFile).ProviderPath
        if ([System.IO.Path]::GetExtension($scriptFile) -ne '.ps1') { throw 'ScriptPath 只接受 PowerShell 腳本。' }
        $Command = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
        $Arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $scriptFile) + $Arguments
    }

    $executable = (Get-Command -Name $Command -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    if ([System.IO.Path]::GetExtension($executable) -in @('.cmd', '.bat', '.ps1')) {
        throw '不隱式啟動 shell；PowerShell 請用 ScriptPath，其他情況請指定真正的執行檔。'
    }

    $runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')
    $runDir = [System.IO.Path]::GetFullPath((Join-Path $LogDirectory $runName))
    [void][System.IO.Directory]::CreateDirectory($runDir)
    $stdoutPath = Join-Path $runDir 'stdout.log'
    $stderrPath = Join-Path $runDir 'stderr.log'
    $resultPath = Join-Path $runDir 'result.json'

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.WorkingDirectory = $cwd
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = $true
    # 不拼接命令字串，路徑空白、引號與分號才不會變成另一段 shell 指令。
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutFile = [System.IO.File]::Create($stdoutPath)
    $stderrFile = [System.IO.File]::Create($stderrPath)
    $started = $process.Start()
    $process.StandardInput.Close()

    # 兩條管線同時排空到磁碟，避免大量 stderr 卡住程序，也不把整份紀錄留在記憶體。
    $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
    $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        $process.Kill($true)
        $process.WaitForExit()
    }
    [void]$stdoutCopy.GetAwaiter().GetResult()
    [void]$stderrCopy.GetAwaiter().GetResult()
    $exitCode = if ($timedOut) { 124 } else { $process.ExitCode }
    $stdoutFile.Dispose()
    $stdoutFile = $null
    $stderrFile.Dispose()
    $stderrFile = $null
    $clock.Stop()

    # 原始串流不過濾、不重編碼；JSON 中不記錄引數，避免把密碼再複製一份。
    $result = [ordered]@{
        command = [System.IO.Path]::GetFileName($executable)
        working_directory = $cwd
        exit_code = $exitCode
        timed_out = $timedOut
        elapsed_seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
        stdout_bytes = (Get-Item -LiteralPath $stdoutPath).Length
        stderr_bytes = (Get-Item -LiteralPath $stderrPath).Length
        stdout_path = $stdoutPath
        stderr_path = $stderrPath
    }
    [System.IO.File]::WriteAllText($resultPath, (($result | ConvertTo-Json) -replace "`r", '') + "`n", [System.Text.UTF8Encoding]::new($false))

    $state = if ($timedOut) { '逾時' } elseif ($exitCode -eq 0) { '成功' } else { '失敗' }
    $header = "命令$state；結束碼=$exitCode；耗時=$($result.elapsed_seconds) 秒。`n"
    $footer = "`n以下路徑保留完整原始紀錄；尾段不等於全部診斷：`nstdout: $stdoutPath`nstderr: $stderrPath`n結果: $resultPath"
    $remaining = $MaxChars - $header.Length - $footer.Length - 100
    if ($remaining -lt 0) { throw '紀錄路徑超過輸出預算，請縮短 LogDirectory。' }
    $preview = [System.Text.StringBuilder]::new()

    if ($TailLines -gt 0) {
        foreach ($item in @(@{ Label = 'stderr'; Path = $stderrPath }, @{ Label = 'stdout'; Path = $stdoutPath })) {
            $encodingLabel = $PreviewEncoding
            if ($PreviewEncoding -eq 'auto') {
                $decoded = Get-AutoPreviewTail $item.Path $TailLines
                $tail = @($decoded.Lines)
                $encodingLabel = $decoded.Label
            }
            else { $tail = @(Get-Content -LiteralPath $item.Path -Tail $TailLines -Encoding $PreviewEncoding) }
            if ($tail.Count -eq 0) { continue }
            foreach ($line in @("--- $($item.Label) 尾段 [$encodingLabel] ---") + $tail) {
                # ANSI 控制碼只從預覽移除；鑑識與重讀仍以原始檔為準。
                $clean = $line -replace '\x1b\[[0-?]*[ -/]*[@-~]', ''
                $take = [Math]::Min($clean.Length, [Math]::Max(0, $remaining - 1))
                if ($remaining -le 1) { break }
                [void]$preview.Append($clean.Substring(0, $take)).Append("`n")
                $remaining -= $take + 1
            }
        }
    }
    Write-Output ($header + $preview.ToString() + $footer)
}
catch {
    $message = "輸出包裝器失敗（125）：$($_.Exception.Message)"
    Write-Output $message.Substring(0, [Math]::Min($message.Length, $MaxChars - 1))
    $exitCode = 125
}
finally {
    if ($process) {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    if ($stdoutFile) { $stdoutFile.Dispose() }
    if ($stderrFile) { $stderrFile.Dispose() }
}

# 非零不是「摘要成功」；CI 與外層代理必須收到原命令的失敗。
exit $exitCode
