#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$VsixPath,
    [Parameter(Mandatory)][string]$ProbePath,
    [string]$SsmsInstallDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output
$root = Split-Path -Parent $PSScriptRoot
$ide = Join-Path (Get-SsmsInstallPath -InstallDir $SsmsInstallDir) 'Common7/IDE'
$work = Join-Path $root ('artifacts/sql-memory-package/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $VsixPath).Path, $work)
$probe = Join-Path $work 'SqlAssist.SqlMemory.Probe.exe'
Copy-Item -LiteralPath $ProbePath -Destination $probe
# 使用宿主的 bindingRedirect；不複製 probe 建置目錄內的 System.* 或任何相依 DLL。
Copy-Item -LiteralPath (Join-Path $ide 'Ssms.exe.config') -Destination ($probe + '.config')
$database = Join-Path $work 'probe.db'

function Start-Probe([string]$Mode, [string]$Name) {
    $stdout = Join-Path $work ($Name + '.stdout.log')
    $stderr = Join-Path $work ($Name + '.stderr.log')
    $arguments = @('"' + $ide + '"', '"' + $database + '"', $Mode)
    Start-Process -FilePath $probe -ArgumentList $arguments -WorkingDirectory $work -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
}

function Wait-Probe($Process, [string]$Name) {
    if (-not $Process.WaitForExit(60000)) {
        $Process.Kill()
        throw "SQL Memory probe 逾時：$Name"
    }
    $Process.WaitForExit()
    Get-Content -LiteralPath (Join-Path $work ($Name + '.stdout.log')) -Encoding utf8
    if ($Process.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $work ($Name + '.stderr.log')) -Encoding utf8
        throw "SQL Memory probe 失敗：$Name，結束碼 $($Process.ExitCode)"
    }
}

function Invoke-Probe([string]$Mode) {
    $process = Start-Probe $Mode $Mode
    try { Wait-Probe $process $Mode }
    finally { $process.Dispose() }
}

Invoke-Probe 'runtime'
Invoke-Probe 'self-test'
# 模擬 SSMS：launcher 不含 SqlAssist DLL，擴充只能從另一個目錄以 LoadFrom 載入。
$launcherDirectory = Join-Path $work 'external-host'
$null = New-Item -ItemType Directory -Path $launcherDirectory
$launcher = Join-Path $launcherDirectory 'SqlAssist.SqlMemory.Probe.exe'
Copy-Item -LiteralPath $ProbePath -Destination $launcher
Copy-Item -LiteralPath (Join-Path $ide 'Ssms.exe.config') -Destination ($launcher + '.config')
$external = Start-Process -FilePath $launcher -WindowStyle Hidden -WorkingDirectory $launcherDirectory -PassThru `
    -ArgumentList @('--external-load', ('"' + $work + '"'), ('"' + $ide + '"'), ('"' + (Join-Path $work 'external-self-test') + '"')) `
    -RedirectStandardOutput (Join-Path $work 'external.stdout.log') -RedirectStandardError (Join-Path $work 'external.stderr.log')
try { Wait-Probe $external 'external' }
finally { $external.Dispose() }
# 同時啟動兩個 net48 x64 程序，不以同一程序的兩條連線冒充跨程序驗收。
$processes = @()
try {
    $first = Start-Probe 'write' 'writer-a'
    $processes += $first
    $second = Start-Probe 'write' 'writer-b'
    $processes += $second
    Wait-Probe $first 'writer-a'
    Wait-Probe $second 'writer-b'
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
Invoke-Probe 'verify'
# 明確驗證程序結束後檔案已釋放；不刪除驗證紀錄。
$file = [IO.File]::Open($database, 'Open', 'ReadWrite', 'None')
$file.Dispose()

# 反向驗證封裝護欄，防止日後把「開發輸出有 DLL」誤當成「VSIX 已經包含 DLL」。
foreach ($case in @('missing-native', 'missing-provider', 'wrong-architecture', 'nested-bcl')) {
    $badPackage = Join-Path $work ($case + '.vsix')
    Copy-Item -LiteralPath $VsixPath -Destination $badPackage
    $zip = [IO.Compression.ZipFile]::Open($badPackage, 'Update')
    try {
        switch ($case) {
            'missing-native' { $zip.GetEntry('e_sqlite3.dll').Delete(); $expected = '缺少必要檔案：e_sqlite3.dll' }
            'missing-provider' { $zip.GetEntry('SQLitePCLRaw.core.dll').Delete(); $expected = '缺少必要檔案：SQLitePCLRaw.core.dll' }
            'wrong-architecture' {
                $entry = $zip.GetEntry('e_sqlite3.dll')
                $stream = $entry.Open()
                $memory = [IO.MemoryStream]::new()
                try { $stream.CopyTo($memory); $bytes = $memory.ToArray() }
                finally { $stream.Dispose(); $memory.Dispose() }
                $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
                $bytes[$peOffset + 4] = 0x4c
                $bytes[$peOffset + 5] = 0x01
                $entry.Delete()
                $stream = $zip.CreateEntry('e_sqlite3.dll').Open()
                try { $stream.Write($bytes, 0, $bytes.Length) }
                finally { $stream.Dispose() }
                $expected = '不是 SSMS 所需的 x64'
            }
            'nested-bcl' { $null = $zip.CreateEntry('nested/System.Memory.dll'); $expected = '夾帶了應由 SSMS 提供的組件' }
        }
    }
    finally { $zip.Dispose() }
    $log = Join-Path $work ($case + '.log')
    & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'Test-VsixPackage.ps1') -VsixPath $badPackage *> $log
    if ($LASTEXITCODE -eq 0 -or (Get-Content -LiteralPath $log -Raw -Encoding utf8) -notmatch [regex]::Escape($expected)) {
        throw "封裝負向測試沒有因預期原因失敗：$case，紀錄：$log"
    }
}
Write-Host '封裝負向測試通過：缺 native、缺 provider、錯誤架構、子目錄夾帶 BCL。'
Write-Host "SQL Memory 封裝隔離驗證通過。紀錄：$work"
Write-Host '此驗證不代表已在真正的 SSMS 程序載入，也不執行安裝或解除安裝。'
