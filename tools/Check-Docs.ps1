[CmdletBinding()]
param(
    # 每一份 docs 的字元上限。中文一個字約等於一個 token，所以字元數比位元組數
    # 更接近「讀這一份要付多少 context」——那才是拆檔的真正理由。
    [int]$CharBudget = 14000,
    [int]$WarnAt = 10000,
    # CLAUDE.md 每一次 API 呼叫都會重送一遍，成本是 docs 的數十倍，門檻另計。
    [int]$ClaudeMdBudget = 8000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Get-DocText([string]$path) {
    # -Raw 保留換行；utf8 讀取會自動吃掉 BOM，字元數才不會因為 BOM 多算三個。
    # CR 也一併去掉：它不是內容也不佔 token，留著會讓 CRLF 的檔案憑空多出 3% 的「大小」。
    (Get-Content -LiteralPath $path -Raw -Encoding utf8) -replace "`r", ''
}

# 標題轉錨點：GitHub 會轉小寫、丟掉標點、把空白換成連字號，中文原樣保留。
# 這裡只做同一組轉換，比對不上就是連結真的會落空。
function ConvertTo-Anchor([string]$text) {
    $t = $text.Trim().ToLowerInvariant()
    $t = $t -replace '`', ''
    $t = $t -replace '[^\p{L}\p{Nd}\s_-]', ''
    $t = $t -replace '\s+', '-'
    return $t
}

$docs = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' -Recurse)
$targets = @($docs) + @(Get-Item -LiteralPath (Join-Path $root 'README.md'))

$over = [System.Collections.Generic.List[object]]::new()
$warn = [System.Collections.Generic.List[object]]::new()
$maxLen = 0
$maxName = ''

foreach ($f in $targets) {
    $len = (Get-DocText $f.FullName).Length
    $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
    if ($len -gt $maxLen) { $maxLen = $len; $maxName = $rel }
    if ($len -gt $CharBudget) { $over.Add([pscustomobject]@{ Path = $rel; Len = $len }) }
    elseif ($len -gt $WarnAt) { $warn.Add([pscustomobject]@{ Path = $rel; Len = $len }) }
}

$claudeMd = Join-Path $root 'CLAUDE.md'
$claudeLen = (Get-DocText $claudeMd).Length

# 連結檢查。拆檔之後最容易壞的就是連結，而 Markdown 壞連結不會有任何徵兆——
# 點下去才發現，通常是幾個月後的事。
$anchors = @{}
foreach ($f in $targets) {
    $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
    $set = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($line in (Get-DocText $f.FullName) -split "`n") {
        if ($line -match '^#{1,6}\s+(.*)$') { [void]$set.Add((ConvertTo-Anchor $Matches[1])) }
    }
    $anchors[$rel] = $set
}

$broken = [System.Collections.Generic.List[string]]::new()
foreach ($f in @($targets) + @(Get-Item -LiteralPath $claudeMd)) {
    $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
    $dir = Split-Path -Parent $f.FullName
    $n = 0
    foreach ($line in (Get-DocText $f.FullName) -split "`n") {
        $n++
        foreach ($m in [regex]::Matches($line, '\]\(([^)\s]+?\.md)(#[^)\s]*)?\)')) {
            $dest = Join-Path $dir $m.Groups[1].Value
            if (-not (Test-Path -LiteralPath $dest)) {
                $broken.Add("${rel}:${n} 檔案不存在 -> $($m.Groups[1].Value)")
                continue
            }
            if ($m.Groups[2].Success) {
                $destRel = (Resolve-Path -LiteralPath $dest).Path.Substring($root.Length + 1).Replace('\', '/')
                $want = $m.Groups[2].Value.Substring(1)
                if ($anchors.ContainsKey($destRel) -and -not $anchors[$destRel].Contains($want)) {
                    $broken.Add("${rel}:${n} 錨點不存在 -> $($m.Groups[1].Value)#$want")
                }
            }
        }
    }
}

$failed = $false

if ($broken.Count -gt 0) {
    $failed = $true
    Write-Host "壞掉的連結：" -ForegroundColor Red
    $broken | ForEach-Object { Write-Host "  $_" }
}

if ($over.Count -gt 0) {
    $failed = $true
    Write-Host "超過 $CharBudget 字元，必須拆分並更新 docs/index.md：" -ForegroundColor Red
    $over | ForEach-Object { Write-Host ("  {0,-44}{1,7} 字元" -f $_.Path, $_.Len) }
}

if ($claudeLen -gt $ClaudeMdBudget) {
    $failed = $true
    Write-Host "CLAUDE.md 超過 $ClaudeMdBudget 字元（目前 $claudeLen）：" -ForegroundColor Red
    Write-Host "  它每一次 API 呼叫都會重送，細節請移進 docs/，只留禁令與指路。"
}

if ($warn.Count -gt 0) {
    Write-Host "接近上限，下次擴充前先想好怎麼拆：" -ForegroundColor Yellow
    $warn | ForEach-Object { Write-Host ("  {0,-44}{1,7} 字元" -f $_.Path, $_.Len) }
}

if ($failed) { throw '文件檢查未通過。' }

if ($warn.Count -eq 0) {
    Write-Host ("文件檢查通過：{0} 份，最大 {1} 字元（{2}）；CLAUDE.md {3}/{4}。" -f `
        $targets.Count, $maxLen, $maxName, $claudeLen, $ClaudeMdBudget)
}
