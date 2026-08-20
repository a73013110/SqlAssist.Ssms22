$ErrorActionPreference = 'Stop'
$extensionId = 'SqlAssist.Ssms22.7f693af0-846a-4ee8-ab70-a174a3e31f65'
$ssmsRoots = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\SSMS" -Directory -Filter '22.0_*' -ErrorAction SilentlyContinue
$installed = @()

foreach ($root in $ssmsRoots) {
    $manifests = Get-ChildItem -Path (Join-Path $root.FullName 'Extensions') `
        -Recurse `
        -File `
        -Filter 'extension.vsixmanifest' `
        -ErrorAction SilentlyContinue

    foreach ($manifestFile in $manifests) {
        [xml]$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw
        $identity = $manifest.PackageManifest.Metadata.Identity

        if ($identity.Id -eq $extensionId) {
            $installed += [pscustomobject]@{
                Version = $identity.Version
                Path = $manifestFile.Directory.FullName
            }
        }
    }
}

if ($installed.Count -eq 0) {
    Write-Host '安裝狀態：找不到 SqlAssist。' -ForegroundColor Yellow
}
else {
    Write-Host '安裝狀態：' -ForegroundColor Cyan
    $installed | Format-Table -AutoSize
}

$logPath = Join-Path $env:LOCALAPPDATA 'SqlAssist.Ssms22\SqlAssist.log'

if (Test-Path -LiteralPath $logPath) {
    Write-Host "`n最近診斷紀錄：$logPath" -ForegroundColor Cyan
    Get-Content -LiteralPath $logPath -Tail 50
}
else {
    Write-Host "`n尚無診斷紀錄：$logPath" -ForegroundColor Yellow
    Write-Host '安裝 0.4.1 並重新啟動 SSMS 後才會產生紀錄。'
}
