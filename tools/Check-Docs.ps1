#Requires -Version 7.0
[CmdletBinding()]
param(
    # 字元只是穩定的檔案預算，不假設中文與模型 token 一比一，也不估算快取費用。
    [ValidateRange(1, 2147483647)]
    [int]$CharBudget = 5000,
    [int]$WarnAt = 4500,
    [ValidateRange(1, 2147483647)]
    [int]$ReadmeMdBudget = 6000,
    [int]$ReadmeMdWarnAt = 5500,
    [ValidateRange(1, 2147483647)]
    [int]$ClaudeMdBudget = 1000,
    [ValidateRange(1, 2147483647)]
    [int]$IndexMdBudget = 4500,
    [ValidateRange(1, 2147483647)]
    [int]$AgentsMdBudget = 400,
    # 去掉空白後至少這麼長的句子在兩頁同時出現，就是同一規則寫了兩份。
    [ValidateRange(1, 2147483647)]
    [int]$DuplicateSentenceLength = 30,
    # 破折號多半是在句尾再接一段補充；本次修改讓一頁超過這個數目又比 HEAD 多時提醒。
    [int]$DashWarnAt = 10,
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output
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

function Get-ProseSentences($Lines) {
    # 段落與清單項跨行接起來再切句；標題與表格列不是句子，表格的重複由人判斷。
    $chunks = [System.Collections.Generic.List[object]]::new()
    $buffer = [System.Text.StringBuilder]::new()
    $start = 0
    foreach ($line in $Lines) {
        $text = $line.Text
        $isBreak = -not $text.Trim() -or $text -match '^\s*(#|\|)'
        if ($isBreak -or $text -match '^\s*([-*+]|\d+\.)\s') {
            if ($buffer.Length) { $chunks.Add([pscustomobject]@{ Line = $start; Text = $buffer.ToString() }) }
            [void]$buffer.Clear()
        }
        if ($isBreak) { continue }
        if (-not $buffer.Length) { $start = $line.Number }
        [void]$buffer.Append(($text -replace '^\s*([-*+]|\d+\.)\s+', ''))
    }
    if ($buffer.Length) { $chunks.Add([pscustomobject]@{ Line = $start; Text = $buffer.ToString() }) }
    foreach ($chunk in $chunks) {
        # 中文換行不帶空格、英文帶，去掉全部空白才比得到同一句不同折行。
        # 只算以句號收尾的完整句：清單裡的名詞標籤（版本、平台）重複是正常的。
        foreach ($sentence in (($chunk.Text -replace '\s+', '') -split '(?<=[。！？])')) {
            if ($sentence.Length -ge $DuplicateSentenceLength -and $sentence -match '[。！？]$') {
                [pscustomobject]@{ Line = $chunk.Line; Text = $sentence }
            }
        }
    }
}

function Get-HeadText([string]$Relative) {
    # 走 Process 而不是 & git：管道會吃掉結尾換行，字元數就與工作目錄的檔案差一。
    $info = [System.Diagnostics.ProcessStartInfo]::new('git')
    foreach ($argument in @('-C', $rootPath, 'show', "HEAD:$Relative")) { $info.ArgumentList.Add($argument) }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $process = [System.Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEnd()
    [void]$process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { return $null }
    return $output -replace "`r", ''
}

function ConvertTo-Anchor([string]$Text) {
    $value = ($Text -replace '<[^>]+>', '').Trim().ToLowerInvariant()
    $value = $value -replace '`', ''
    $value = $value -replace '[^\p{L}\p{Nd}\s_-]', ''
    return $value -replace '\s+', '-'
}

$targets = @(Get-ChildItem -LiteralPath (Join-Path $rootPath 'docs') -Filter '*.md' -Recurse)
foreach ($name in @('README.md', 'README.zh-TW.md', 'CLAUDE.md', 'AGENTS.md')) {
    $targets += Get-Item -LiteralPath (Join-Path $rootPath $name)
}
$budgets = @{ 'CLAUDE.md' = $ClaudeMdBudget; 'AGENTS.md' = $AgentsMdBudget; 'docs/index.md' = $IndexMdBudget; 'README.md' = $ReadmeMdBudget }
$over = [System.Collections.Generic.List[string]]::new()
$warn = [System.Collections.Generic.List[string]]::new()
# 每頁 H1 之後第一句要說明本頁範圍；索引是路由，入口檔只導向規則，都不適用。
$exemptFromScope = @('docs/index.md', 'CLAUDE.md', 'AGENTS.md', 'README.md', 'README.zh-TW.md')
$noScope = [System.Collections.Generic.List[string]]::new()
$anchors = @{}
$linesByPath = @{}
$lengths = @{}
$textByPath = @{}
$warnAtByPath = @{}

foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\', '/')
    $text = [System.IO.File]::ReadAllText($file.FullName) -replace "`r", ''
    $lengths[$relative] = $text.Length
    $textByPath[$relative] = $text
    $budget = if ($budgets.ContainsKey($relative)) { $budgets[$relative] } else { $CharBudget }
    $warnAtByPath[$relative] = if ($relative -eq 'README.md') { $ReadmeMdWarnAt } else { [Math]::Min($WarnAt, $budget) }
    if ($text.Length -gt $budget) { $over.Add("$relative：$($text.Length)/$budget 字元") }
    elseif ($text.Length -gt $warnAtByPath[$relative]) {
        $warn.Add("$relative：$($text.Length) 字元")
    }

    $linesByPath[$file.FullName] = @(Get-MarkdownLines $text)
    if ($relative -notin $exemptFromScope) {
        # 圍欄內的 # 已由 Get-MarkdownLines 濾掉，所以第一個非空白行就是真正的標題。
        $content = @($linesByPath[$file.FullName] | Where-Object { $_.Text.Trim() })
        $heading = $content | Select-Object -First 1
        $first = $content | Select-Object -Skip 1 -First 1
        if (-not $heading -or $heading.Text -notmatch '^#\s+\S') {
            $noScope.Add("${relative}：第一行要是 H1 標題")
        }
        elseif (-not $first -or $first.Text -match '^\s*(#|\||[-*+]\s|\d+\.\s)') {
            $noScope.Add("${relative}：H1 之後第一句要寫本頁包含什麼、不含的那一半在哪")
        }
    }
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

# 同一規則只留一份：README 是給訪客的產品摘要，允許重述文件裡的句子。
$sentences = @{}
foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\', '/')
    if ($relative -like 'README*') { continue }
    foreach ($sentence in Get-ProseSentences $linesByPath[$file.FullName]) {
        if (-not $sentences.ContainsKey($sentence.Text)) { $sentences[$sentence.Text] = [System.Collections.Generic.List[object]]::new() }
        $sentences[$sentence.Text].Add([pscustomobject]@{ Path = $relative; Line = $sentence.Line })
    }
}
$duplicates = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $sentences.GetEnumerator()) {
    $places = @($entry.Value | Sort-Object Path -Unique)
    if ($places.Count -gt 1) {
        $where = ($places | ForEach-Object { "$($_.Path):$($_.Line)" }) -join '、'
        $duplicates.Add("$where：$($entry.Key.Substring(0, [Math]::Min(40, $entry.Key.Length)))…")
    }
}

# 與 HEAD 比的兩項只看這次改到的檔案：警告線以上不得淨增，故事句型與破折號只提醒新增的。
$growth = [System.Collections.Generic.List[string]]::new()
$story = [System.Collections.Generic.List[string]]::new()
if (Get-Command git -ErrorAction SilentlyContinue) {
    $status = @(& git -C $rootPath -c core.quotepath=false status --porcelain=v1 --untracked-files=all -- `
            docs README.md README.zh-TW.md CLAUDE.md AGENTS.md 2>$null)
    foreach ($entry in $status) {
        $relative = ($entry.Substring(3) -split ' -> ')[-1].Trim('"')
        if (-not $textByPath.ContainsKey($relative)) { continue }
        $current = $textByPath[$relative]
        $head = Get-HeadText $relative
        $limit = $warnAtByPath[$relative]
        $baseline = if ($null -eq $head) { $limit } else { $head.Length }
        if ($current.Length -gt $limit -and $current.Length -gt $baseline) {
            $growth.Add("${relative}：$baseline → $($current.Length) 字元")
        }
        $before = if ($null -eq $head) { '' } else { $head }
        $storyBefore = [regex]::Matches($before, '那一版|舊版|以前').Count
        $storyAfter = [regex]::Matches($current, '那一版|舊版|以前').Count
        if ($storyAfter -gt $storyBefore) { $story.Add("${relative}：「那一版／舊版／以前」$storyBefore → $storyAfter") }
        $dashBefore = [regex]::Matches($before, '——').Count
        $dashAfter = [regex]::Matches($current, '——').Count
        if ($dashAfter -gt $DashWarnAt -and $dashAfter -gt $dashBefore) { $story.Add("${relative}：破折號 $dashBefore → $dashAfter") }
    }
}
else {
    Write-Host '找不到 git，略過與 HEAD 比較的淨增與句型檢查。' -ForegroundColor Yellow
}

if ($broken.Count -gt 0) {
    Write-Host '壞掉的本機 Markdown 連結：' -ForegroundColor Red
    $broken | ForEach-Object { Write-Host "  $_" }
}
if ($noScope.Count -gt 0) {
    Write-Host '缺少範圍句：' -ForegroundColor Red
    $noScope | ForEach-Object { Write-Host "  $_" }
}
if ($over.Count -gt 0) {
    Write-Host '文件超過各自預算，請先刪冗餘，必要時才拆分並更新索引：' -ForegroundColor Red
    $over | ForEach-Object { Write-Host "  $_" }
}
if ($duplicates.Count -gt 0) {
    Write-Host '跨頁重複的句子，同一規則只留一份，另一頁改成一句連結：' -ForegroundColor Red
    $duplicates | Sort-Object | ForEach-Object { Write-Host "  $_" }
}
if ($growth.Count -gt 0) {
    Write-Host '已超過警告線的頁不得淨增，新增多少就先刪多少：' -ForegroundColor Red
    $growth | ForEach-Object { Write-Host "  $_" }
}
if ($story.Count -gt 0) {
    Write-Host '這次修改多了講經過的句型，理由改寫成「否則 X 會 Y」：' -ForegroundColor Yellow
    $story | ForEach-Object { Write-Host "  $_" }
}
if ($warn.Count -gt 0) {
    Write-Host '接近單檔上限，擴充前請先回頭刪冗餘：' -ForegroundColor Yellow
    $warn | ForEach-Object { Write-Host "  $_" }
}
if ($broken.Count -gt 0 -or $over.Count -gt 0 -or $noScope.Count -gt 0 -or $duplicates.Count -gt 0 -or $growth.Count -gt 0) {
    throw '文件檢查未通過。'
}

Write-Host ("文件檢查通過：{0} 份；CLAUDE {1}/{2}、AGENTS {3}/{4}、索引 {5}/{6} 字元。" -f `
    $targets.Count, $lengths['CLAUDE.md'], $ClaudeMdBudget, $lengths['AGENTS.md'], $AgentsMdBudget, `
    $lengths['docs/index.md'], $IndexMdBudget)
