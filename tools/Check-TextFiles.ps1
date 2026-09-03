#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$errors = [System.Collections.Generic.List[string]]::new()
$textFileCount = 0

# 新增但尚未 stage 的腳本與文件也要驗證；否則第一次提交最容易漏掉 BOM／CRLF。
$trackedFiles = @(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard --eol)
if ($LASTEXITCODE -ne 0) {
    throw '無法取得 Git 工作樹檔案。'
}

foreach ($entry in $trackedFiles) {
    $separator = $entry.IndexOf("`t", [System.StringComparison]::Ordinal)
    if ($separator -lt 0) {
        $errors.Add("無法解析 Git 輸出：$entry")
        continue
    }

    $attributes = $entry.Substring(0, $separator)
    $isExplicitText = $attributes -match 'attr/text(?:\s|$)'
    $detectedBinary = $attributes.StartsWith('i/-text', [System.StringComparison]::Ordinal) -or
        $attributes -match '^i/\s+w/-text(?:\s|$)'
    if ($attributes -match 'attr/-text(?:\s|$)' -or ($detectedBinary -and -not $isExplicitText)) {
        continue
    }

    $relativePath = $entry.Substring($separator + 1)
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $errors.Add("找不到追蹤檔案：$relativePath")
        continue
    }

    # 直接檢查位元組，否則讀取 API 可能先替我們轉換 BOM 或換行，讓違規檔案漏網。
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $textFileCount++

    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        $errors.Add("$relativePath：含 UTF-8 BOM")
    }

    try {
        [void]$strictUtf8.GetString($bytes)
    }
    catch [System.Text.DecoderFallbackException] {
        $errors.Add("$relativePath：不是有效的 UTF-8")
    }

    if ($bytes -contains 0x0D) {
        $errors.Add("$relativePath：含 CR 或 CRLF 換行")
    }

    if ($bytes.Length -gt 0 -and $bytes[-1] -ne 0x0A) {
        $errors.Add("$relativePath：檔尾缺少 LF")
    }
}

if ($errors.Count -gt 0) {
    Write-Host '文字檔格式檢查失敗：' -ForegroundColor Red
    foreach ($message in $errors) {
        Write-Host "  - $message" -ForegroundColor Red
    }

    exit 1
}

Write-Host "文字檔格式檢查通過：$textFileCount 個 UTF-8（無 BOM）／LF 檔案。"
