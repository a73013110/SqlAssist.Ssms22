[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$extensionId = 'SqlAssist.Ssms22.7f693af0-846a-4ee8-ab70-a174a3e31f65'
$ssmsPath = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release'
$installer = Join-Path $ssmsPath 'Common7\IDE\VSIXInstaller.exe'

function Get-SqlAssistInstallation {
    $ssmsRoots = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\SSMS" `
        -Directory `
        -Filter '22.0_*' `
        -ErrorAction SilentlyContinue

    foreach ($root in $ssmsRoots) {
        $manifests = Get-ChildItem -Path (Join-Path $root.FullName 'Extensions') `
            -Recurse `
            -File `
            -Filter 'extension.vsixmanifest' `
            -ErrorAction SilentlyContinue

        foreach ($manifestFile in $manifests) {
            try {
                [xml]$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw
                $identity = $manifest.PackageManifest.Metadata.Identity

                if ($identity.Id -eq $extensionId) {
                    [pscustomobject]@{
                        Version = [string]$identity.Version
                        Path = $manifestFile.Directory.FullName
                    }
                }
            }
            catch {
                Write-Warning "無法讀取延伸模組資訊：$($manifestFile.FullName)"
            }
        }
    }
}

if (Get-Process -Name 'SSMS' -ErrorAction SilentlyContinue) {
    throw '請先儲存查詢並關閉所有 SSMS 視窗，再執行解除安裝。'
}

if (-not (Test-Path -LiteralPath $installer)) {
    throw "找不到 SSMS VSIX 安裝程式：$installer"
}

$installed = @(Get-SqlAssistInstallation)

if ($installed.Count -eq 0) {
    Write-Host '解除安裝略過：目前找不到 SqlAssist for SSMS 22。' -ForegroundColor Yellow
    return
}

Write-Host '即將解除安裝：' -ForegroundColor Cyan
$installed | Format-Table -AutoSize

if (-not $PSCmdlet.ShouldProcess(
        'SqlAssist for SSMS 22',
        "使用 SSMS VSIXInstaller 解除安裝延伸模組 $extensionId")) {
    return
}

$arguments = @("/uninstall:$extensionId") # 依 VSIX Identity 精確解除安裝 SqlAssist。

if ($Quiet) {
    $arguments += '/quiet'
    $process = Start-Process `
        -FilePath $installer `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
}
else {
    # 預設顯示 SSMS 官方解除安裝介面，讓使用者確認操作。
    $process = Start-Process `
        -FilePath $installer `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
}

if ($process.ExitCode -ne 0) {
    throw "VSIXInstaller 解除安裝失敗，結束代碼：$($process.ExitCode)"
}

$remaining = @(Get-SqlAssistInstallation)

if ($remaining.Count -gt 0) {
    Write-Warning '仍偵測到 SqlAssist；可能已取消操作，或解除安裝程序尚未完成。'
    $remaining | Format-Table -AutoSize
    return
}

Write-Host 'SqlAssist for SSMS 22 已解除安裝。' -ForegroundColor Green
Write-Host '使用者設定與診斷紀錄仍保留於：' -ForegroundColor DarkGray
Write-Host (Join-Path $env:LOCALAPPDATA 'SqlAssist.Ssms22') -ForegroundColor DarkGray
