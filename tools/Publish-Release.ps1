#Requires -Version 7.0
<#
.SYNOPSIS
    建置、驗證並在 GitHub 上建立一個草稿 Release。

.DESCRIPTION
    發布一律停在草稿。VSIX 有一種只在實機才看得出來的失敗：MEF 匯出型別的
    命名空間變動後，SSMS 的元件快取會安靜地讓那些部件建立失敗——沒有例外、
    沒有記錄，只有「功能整組消失」。建置與封裝檢查都攔不到它，所以最後一關
    必須是人：把草稿的 VSIX 裝進 SSMS 確認過，再自己按 Publish。

    這也是這個專案不需要 CI 建置 VSIX 的原因——CI 複製得了建置，複製不了那一關。

.PARAMETER SsmsInstallDir
    SSMS 22 的安裝路徑，未指定時用模組的預設值。

.PARAMETER Remote
    要推送 tag 的 git 遠端。未指定且只有一個遠端時自動採用它。
#>
[CmdletBinding()]
param(
    [string]$SsmsInstallDir,
    [string]$Remote
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

$root = Get-SqlAssistRoot

$gh = Get-Command 'gh' -CommandType Application -ErrorAction SilentlyContinue

if (-not $gh) {
    throw '找不到 GitHub CLI。請執行 winget install GitHub.cli，並開一個新的終端機讓 PATH 生效。'
}

& $gh.Source auth status *> $null

if ($LASTEXITCODE -ne 0) {
    throw '尚未登入 GitHub CLI。請先執行 gh auth login。'
}

# version.json 的 publicReleaseRefSpec 只認 master。在別的分支上建置，版號會多一段
# 代表分支的前置，包出來的 VSIX 覆蓋不了使用者手上那個。
$branch = git -C $root rev-parse --abbrev-ref HEAD

if ($branch -ne 'master') {
    throw "目前在 $branch 分支。發布一律從 master 進行，否則版號會帶上分支前置。"
}

# 版號的第三段是 git height，只從 commit 算，工作目錄的變更不列入。有未 commit 的
# 變更時，tag 指到的 commit 重建不出同一個 VSIX，之後就無從回推使用者手上是哪一版。
if (git -C $root status --porcelain) {
    throw '工作樹有未 commit 的變更。請先 commit 或還原，再發布。'
}

if (-not $Remote) {
    $remotes = @(git -C $root remote)

    if ($remotes.Count -ne 1) {
        throw "有 $($remotes.Count) 個 git 遠端，請以 -Remote 指定要推送的那一個。"
    }

    $Remote = $remotes[0]
}

git -C $root fetch $Remote --quiet

# 本機領先遠端時 tag 會指到遠端還沒有的 commit，Release 頁面上的原始碼連結會是死的。
$ahead = git -C $root rev-list --count "$Remote/$branch..HEAD"

if ($ahead -ne '0') {
    throw "本機比 $Remote/$branch 多 $ahead 個 commit。請先 git push，再發布。"
}

Write-Host '執行測試…'
& (Join-Path $PSScriptRoot 'Run-CoreTests.ps1')

Write-Host '建置 VSIX…'
& (Join-Path $PSScriptRoot 'Build-Extension.ps1') -SsmsInstallDir $SsmsInstallDir

$vsix = Get-SqlAssistVsixPath
$version = [version](Get-SqlAssistVsixVersion -VsixPath $vsix)

# Manifest 的版號有四段，第四段由 commit id 推導、不遞增，只用來回推來源。
# tag 取前三段，與 docs/release.md 的 v0.15.0 寫法一致；帶著第四段的 tag
# 排序看起來像亂數，也讓「這一版是哪一版」多一段要對照的數字。
$tag = 'v{0}.{1}.{2}' -f $version.Major, $version.Minor, $version.Build

if (git -C $root tag --list $tag) {
    throw "$tag 已經存在。同一個 commit 只發布一次；有新變更請先 commit，height 會自己往前。"
}

# 用 annotated tag：訊息裡記完整四段版號，把 tag 名稱丟掉的第四段補回來，之後才回推得出
# 使用者手上的 VSIX 是哪一次建置。附帶的好處是 git describe 預設就認得它，不必加 --tags。
git -C $root tag -a $tag -m "SqlAssist $version"
git -C $root push $Remote $tag

# --generate-notes 讓 GitHub 自己從 commit 產生變更摘要，省下手寫；草稿階段還能改。
& $gh.Source release create $tag $vsix `
    --draft `
    --generate-notes `
    --title $tag

if ($LASTEXITCODE -ne 0) {
    throw "建立 Release 失敗，結束代碼：$LASTEXITCODE"
}

Write-Host ''
Write-Host "草稿 Release 已建立：$tag"
Write-Host "VSIX：$vsix"
Write-Host ''
Write-Host '發布前請先把這個 VSIX 裝進 SSMS 確認功能正常，再到 GitHub 按 Publish。'
