#Requires -Version 7.0
[CmdletBinding()]
param(
    # 字元只是穩定的檔案預算，不假設中文與模型 token 一比一，也不估算快取費用。
    [ValidateRange(1, 2147483647)]
    [int]$CharBudget = 14000,
    [int]$WarnAt = 10000,
    [ValidateRange(1, 2147483647)]
    [int]$ClaudeMdBudget = 3500,
    [ValidateRange(1, 2147483647)]
    [int]$IndexMdBudget = 4000,
    [ValidateRange(1, 2147483647)]
    [int]$AgentsMdBudget = 800,
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$rootPath = (Resolve-Path -LiteralPath $Root).ProviderPath

function Get-MarkdownLines([string]$Text) {
    $fence = ''
    $fenceLength = 0
    $number = 0
    foreach ($line in $Text -split "`n") {
        $number++
        # 程式碼範例的 # 註解與 Markdown 字串不是標題或連結，不能拿來充當有效錨點。
        if ($line -match '^ {0,3}(`{3,}|~{3,})(.*)$') {
            $marker = $Matches[1]
            $suffix = $Matches[2]
            if (-not $fence) {
                $fence = $marker.Substring(0, 1)
                $fenceLength = $marker.Length
            }
            elseif ($marker.StartsWith($fence) -and $marker.Length -ge $fenceLength -and -not $suffix.Trim()) {
                $fence = ''
            }
            continue
        }
        if (-not $fence) { [pscustomobject]@{ Number = $number; Text = $line } }
    }
}

function ConvertTo-Anchor([string]$Text) {
    $value = ($Text -replace '<[^>]+>', '').Trim().ToLowerInvariant()
    $value = $value -replace '`', ''
    $value = $value -replace '[^\p{L}\p{Nd}\s_-]', ''
    return $value -replace '\s+', '-'
}

$targets = @(Get-ChildItem -LiteralPath (Join-Path $rootPath 'docs') -Filter '*.md' -Recurse)
foreach ($name in @('README.md', 'CLAUDE.md', 'AGENTS.md')) {
    $targets += Get-Item -LiteralPath (Join-Path $rootPath $name)
}
$budgets = @{ 'CLAUDE.md' = $ClaudeMdBudget; 'AGENTS.md' = $AgentsMdBudget; 'docs/index.md' = $IndexMdBudget }
$over = [System.Collections.Generic.List[string]]::new()
$warn = [System.Collections.Generic.List[string]]::new()
$anchors = @{}
$linesByPath = @{}
$lengths = @{}

foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\', '/')
    $text = [System.IO.File]::ReadAllText($file.FullName) -replace "`r", ''
    $lengths[$relative] = $text.Length
    $budget = if ($budgets.ContainsKey($relative)) { $budgets[$relative] } else { $CharBudget }
    if ($text.Length -gt $budget) { $over.Add("$relative：$($text.Length)/$budget 字元") }
    elseif ($text.Length -gt $WarnAt) { $warn.Add("$relative：$($text.Length) 字元") }

    $linesByPath[$file.FullName] = @(Get-MarkdownLines $text)
    $set = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($line in $linesByPath[$file.FullName]) {
        if ($line.Text -match '^#{1,6}\s+(.*)$') {
            $anchor = ConvertTo-Anchor $Matches[1]
            $unique = $anchor
            $suffix = 0
            while ($set.Contains($unique)) { $suffix++; $unique = "$anchor-$suffix" }
            [void]$set.Add($unique)
        }
    }
    $anchors[$file.FullName] = $set
}

$broken = [System.Collections.Generic.List[string]]::new()
foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\', '/')
    foreach ($line in $linesByPath[$file.FullName]) {
        foreach ($match in [regex]::Matches($line.Text, '\]\(([^)\s]+)\)')) {
            $link = $match.Groups[1].Value
            # 遠端 README.md 不能當成本機路徑；外部來源由作者另行核對，不在本機驗證假裝通過。
            if ($link -match '^(?:[a-z][a-z0-9+.-]*:|//)') { continue }
            $parts = $link.Split('#', 2)
            if ($parts[0] -and $parts[0] -notmatch '\.md$') { continue }
            $target = if ($parts[0]) {
                [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName ([uri]::UnescapeDataString($parts[0]))))
            }
            else { $file.FullName }
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                $broken.Add("${relative}:$($line.Number) 檔案不存在 -> $link")
            }
            elseif ($parts.Length -eq 2 -and $parts[1] -and $anchors.ContainsKey($target)) {
                $want = [uri]::UnescapeDataString($parts[1])
                if (-not $anchors[$target].Contains($want)) {
                    $broken.Add("${relative}:$($line.Number) 錨點不存在 -> $link")
                }
            }
        }
    }
}

if ($broken.Count -gt 0) {
    Write-Host '壞掉的本機 Markdown 連結：' -ForegroundColor Red
    $broken | ForEach-Object { Write-Host "  $_" }
}
if ($over.Count -gt 0) {
    Write-Host '文件超過各自預算，請拆分並更新索引：' -ForegroundColor Red
    $over | ForEach-Object { Write-Host "  $_" }
}
if ($warn.Count -gt 0) {
    Write-Host '接近單檔上限，擴充前請先拆分：' -ForegroundColor Yellow
    $warn | ForEach-Object { Write-Host "  $_" }
}
if ($broken.Count -gt 0 -or $over.Count -gt 0) { throw '文件檢查未通過。' }

Write-Host ("文件檢查通過：{0} 份；CLAUDE {1}/{2}、AGENTS {3}/{4}、索引 {5}/{6} 字元。" -f `
    $targets.Count, $lengths['CLAUDE.md'], $ClaudeMdBudget, $lengths['AGENTS.md'], $AgentsMdBudget, `
    $lengths['docs/index.md'], $IndexMdBudget)
