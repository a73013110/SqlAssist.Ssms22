#Requires -Version 7.0
<#
.SYNOPSIS
    核對內建說明目錄裡的線上文件位址是否還連得到。

.DESCRIPTION
    位址是隨組件發布的字串，寫錯不會有任何徵兆，要等使用者點下「線上文件」看到
    404 才知道。這支腳本逐一送 HEAD 請求核對，改過或新增 docsUrl 之後手動跑。

    需要對外連線，因此**不**接進 Check-Docs.ps1 或 push 前的 hook：那條路要在
    離線與沒有網路的機器上一樣過得去，而站台改版或暫時斷線不該擋下 push。
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Path,
    [ValidateRange(1, 300)]
    [int]$TimeoutSec = 30,
    [ValidateRange(1, 32)]
    [int]$ThrottleLimit = 6,
    [ValidateRange(0, 5)]
    [int]$RetryCount = 2
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$rootPath = (Resolve-Path -LiteralPath $Root).ProviderPath
if (-not $Path) { $Path = Join-Path $rootPath 'src/SqlAssist.Core/Keywords/BuiltInDocs.json' }
$jsonPath = (Resolve-Path -LiteralPath $Path).ProviderPath
$relative = [System.IO.Path]::GetRelativePath($rootPath, $jsonPath).Replace('\', '/')

$catalog = Get-Content -LiteralPath $jsonPath -Raw -Encoding utf8 | ConvertFrom-Json
if (-not $catalog.docs) { throw "$relative 沒有 docs 區塊。" }

# 同一個位址被好幾筆共用（CAST 與 CONVERT、四個日期函式），只問一次但要報出全部名稱。
$owners = [ordered]@{}
$missing = [System.Collections.Generic.List[string]]::new()
foreach ($doc in $catalog.docs) {
    $label = '{0}（{1}）' -f $doc.name, $doc.kind
    if (-not $doc.docsUrl) { $missing.Add($label); continue }
    if (-not $owners.Contains($doc.docsUrl)) {
        $owners[$doc.docsUrl] = [System.Collections.Generic.List[string]]::new()
    }
    $owners[$doc.docsUrl].Add($label)
}

# 形狀不對的位址連問都不必問，產品那條路也會直接擋掉，不要浪費一次往返。
$malformed = [System.Collections.Generic.List[string]]::new()
$probe = [System.Collections.Generic.List[string]]::new()
foreach ($url in @($owners.Keys)) {
    if ($url -match '^https://[^\s]+$') { $probe.Add($url) } else { $malformed.Add($url) }
}

Write-Host ("核對 {0}：{1} 個位址、{2} 筆說明。" -f $relative, $probe.Count, $catalog.docs.Count)

$responses = $probe | ForEach-Object -Parallel {
    $url = $_
    $timeoutSec = $using:TimeoutSec
    $retryCount = $using:RetryCount
    $status = 0
    $reason = ''
    for ($attempt = 0; $attempt -le $retryCount; $attempt++) {
        try {
            $request = @{
                Uri                = $url
                Method             = 'Head'
                MaximumRedirection = 5
                TimeoutSec         = $timeoutSec
                SkipHttpErrorCheck = $true
                ErrorAction        = 'Stop'
            }
            $response = Invoke-WebRequest @request
            $status = [int]$response.StatusCode
            # 有些站台只擋 HEAD，改用 GET 才問得出真正的狀態；不拿 405 當通過。
            if ($status -in 403, 405) {
                $request.Method = 'Get'
                $status = [int](Invoke-WebRequest @request).StatusCode
            }
            $reason = ''
            # 4xx 是內容真的不在了，重試只會讓整批慢上好幾倍；只有連線失敗值得再試。
            break
        }
        catch {
            $status = 0
            $reason = $_.Exception.Message
            if ($attempt -lt $retryCount) { Start-Sleep -Seconds (1 + $attempt) }
        }
    }
    [pscustomobject]@{ Url = $url; Status = $status; Reason = $reason }
} -ThrottleLimit $ThrottleLimit

$statusByUrl = @{}
foreach ($response in $responses) { $statusByUrl[$response.Url] = $response }

$broken = [System.Collections.Generic.List[string]]::new()
foreach ($url in $probe) {
    $response = $statusByUrl[$url]
    if ($response -and $response.Status -ge 200 -and $response.Status -lt 400) { continue }
    $detail = if (-not $response) { '沒有回應' }
    elseif ($response.Status -gt 0) { "HTTP $($response.Status)" }
    else { "連線失敗：$($response.Reason)" }
    # 括號不能省：方法呼叫裡的逗號會被當成引數分隔，-f 只會收到第一個值。
    $broken.Add(("{0}`n    {1}（{2}）" -f ($owners[$url] -join '、'), $url, $detail))
}

if ($missing.Count -gt 0) {
    Write-Host '缺少 docsUrl：' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" }
}
if ($malformed.Count -gt 0) {
    Write-Host 'docsUrl 不是絕對的 https 位址：' -ForegroundColor Red
    $malformed | ForEach-Object { Write-Host "  $_ <- $($owners[$_] -join '、')" }
}
if ($broken.Count -gt 0) {
    Write-Host '線上文件連不上：' -ForegroundColor Red
    $broken | ForEach-Object { Write-Host "  $_" }
}
if ($missing.Count -gt 0 -or $malformed.Count -gt 0 -or $broken.Count -gt 0) {
    throw '線上文件檢查未通過。'
}

Write-Host ("線上文件檢查通過：{0} 個位址全部回應成功。" -f $probe.Count) -ForegroundColor Green
