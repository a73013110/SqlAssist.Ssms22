#Requires -Version 7.0

<#
.SYNOPSIS
讓 AI 分段讀長檔，避免整份文件佔滿上下文；不需要 RTK 或 Serena。
.EXAMPLE
./tools/Read-Context.ps1 -Path docs/development.md -StartLine 20 -LineCount 40
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [ValidateRange(1, 2147483647)]
    [int]$StartLine = 1,
    [ValidateRange(1, 500)]
    [int]$LineCount = 80,
    [ValidateRange(1024, 8000)]
    [int]$MaxChars = 6000
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output
$reader = $null

try {
    $fullPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw '只能讀取文字檔，不能列舉目錄。'
    }

    # 預留路徑與續讀提示的空間，否則內容未超標，實際工具輸出卻可能超標。
    $contentBudget = $MaxChars - $fullPath.Length - 240
    if ($contentBudget -lt 100) { throw '路徑太長，請提高 MaxChars。' }
    $rows = [System.Collections.Generic.List[string]]::new()
    $used = 0
    $lineNumber = 0
    $nextLine = 0
    $longLine = $false
    $reader = [System.IO.StreamReader]::new($fullPath, [System.Text.UTF8Encoding]::new($false, $true), $false)

    while ($null -ne ($line = $reader.ReadLine())) {
        $lineNumber++
        if ($lineNumber -lt $StartLine) { continue }
        $row = '{0}: {1}' -f $lineNumber, $line
        if ($rows.Count -ge $LineCount -or $used + $row.Length + 1 -gt $contentBudget) {
            $nextLine = $lineNumber
            $longLine = $rows.Count -eq 0
            break
        }
        $rows.Add($row)
        $used += $row.Length + 1
    }

    if ($lineNumber -lt $StartLine -and -not ($lineNumber -eq 0 -and $StartLine -eq 1)) {
        throw "StartLine=$StartLine 超出檔尾（共 $lineNumber 行）。"
    }

    $footer = if ($longLine) {
        # 半行 JSON 或程式碼容易被誤認為完整內容，寧可明確要求欄位抽取。
        "第 $nextLine 行超過輸出預算，未顯示；請用本機工具抽取所需欄位，或提高 MaxChars（最多 8000）。"
    }
    elseif ($nextLine -gt 0) {
        "尚有內容未顯示。續讀：-StartLine $nextLine -LineCount $LineCount -MaxChars $MaxChars"
    }
    else { '已到檔尾。' }

    Write-Output "$fullPath`n$($rows -join "`n")`n$footer"
    if ($longLine) { exit 2 }
}
catch {
    $message = "讀取失敗：$($_.Exception.Message)"
    Write-Output $message.Substring(0, [Math]::Min($message.Length, $MaxChars - 1))
    exit 1
}
finally {
    if ($reader) { $reader.Dispose() }
}
