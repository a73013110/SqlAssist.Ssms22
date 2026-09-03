#Requires -Version 7.0
<#
.SYNOPSIS
    工具腳本共用的 UTF-8 輸出與環境探索。

.DESCRIPTION
    SSMS 的安裝路徑、擴充的 Identity Id、以及「已安裝的 SqlAssist 在哪裡」原本
    在六支腳本裡各寫一次。SSMS 換版或裝到別的磁碟時，改到的只會是其中幾支，
    剩下的仍指向舊路徑——而症狀是「安裝成功但部署說找不到」這種互相矛盾的訊息。

    所有預設值只留在這裡，每支腳本都收 -SsmsInstallDir 參數把它覆寫掉。
#>

<#
.SYNOPSIS
    統一腳本的 UTF-8 輸出，回傳供呼叫端設定管道的編碼。

.DESCRIPTION
    Git GUI 啟動的父程序可能用 UTF-8 解碼，但無主控台的子程序卻回到 Big5／CP437，
    即使原始檔正確也會出現亂碼。每支腳本開始工作前明確設定，不依賴啟動環境。
    不寫入使用者／系統的永久設定，也不轉碼原始紀錄；外部程式另依自身編碼處理。

.EXAMPLE
    $OutputEncoding = Initialize-SqlAssistUtf8Output
#>
function Initialize-SqlAssistUtf8Output {
    [CmdletBinding()]
    [OutputType([System.Text.Encoding])]
    param()

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = $encoding

    # 管道偏好屬於呼叫端作用域，只改模組內的同名變數不會生效。
    return $encoding
}

$DefaultSsmsInstallDir = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release'

<#
.SYNOPSIS
    版本庫根目錄。
#>
function Get-SqlAssistRoot {
    Split-Path -Parent $PSScriptRoot
}

<#
.SYNOPSIS
    SSMS 22 的安裝路徑。

.PARAMETER InstallDir
    明確指定時直接採用；空值時用內建預設值。

.PARAMETER Require
    要求路徑必須存在，不存在就中止。
#>
function Get-SsmsInstallPath {
    [CmdletBinding()]
    param(
        [string]$InstallDir,
        [switch]$Require
    )

    $path = if ($InstallDir) { $InstallDir } else { $script:DefaultSsmsInstallDir }

    if ($Require -and -not (Test-Path -LiteralPath $path)) {
        throw "找不到 SSMS 22 安裝目錄：$path。請以 -SsmsInstallDir 指定實際路徑。"
    }

    return $path
}

<#
.SYNOPSIS
    SSMS 隨附的 VSIX 安裝程式。
#>
function Get-SsmsVsixInstaller {
    [CmdletBinding()]
    param([string]$InstallDir)

    $installer = Join-Path (Get-SsmsInstallPath -InstallDir $InstallDir) 'Common7\IDE\VSIXInstaller.exe'

    if (-not (Test-Path -LiteralPath $installer)) {
        throw "找不到 SSMS VSIX 安裝程式：$installer"
    }

    return $installer
}

<#
.SYNOPSIS
    擴充的 VSIX Identity Id，直接讀來源 Manifest。

.DESCRIPTION
    寫死在腳本裡的話，改了 Manifest 之後解除安裝與診斷會安靜地找不到任何東西，
    看起來就像「本來就沒裝」。
#>
function Get-SqlAssistExtensionId {
    $manifestPath = Join-Path (Get-SqlAssistRoot) 'src\SqlAssist.Ssms22\source.extension.vsixmanifest'

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "找不到擴充 Manifest：$manifestPath"
    }

    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    return [string]$manifest.PackageManifest.Metadata.Identity.Id
}

<#
.SYNOPSIS
    建置產物 VSIX 的路徑。
#>
function Get-SqlAssistVsixPath {
    [CmdletBinding()]
    param(
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Release'
    )

    # 方案一律建置 x64，輸出統一在 bin\x64。
    return Join-Path (Get-SqlAssistRoot) "src\SqlAssist.Ssms22\bin\x64\$Configuration\net48\SqlAssist.Ssms22.vsix"
}

<#
.SYNOPSIS
    建置產物 VSIX 的 Identity 版號。

.DESCRIPTION
    發布時的版號一律從產物讀回來，不從 version.json 自己算：版號第三段是 git
    height，只有建置完才確定，自己算的值會與實際包出去的那一個分歧，而 Release
    頁面標的版本與使用者安裝到的版本對不起來時無從查起。

    `Test-VsixPackage.ps1` 另有一份讀取 Manifest 的程式碼，因為它是在單次開啟
    封存的過程中順帶驗證，不只是取值。兩邊的 XPath 若分歧，`Build-Extension.ps1`
    裡的那一份會先擲出例外，不會安靜地讓錯的版號流到 Release。
#>
function Get-SqlAssistVsixVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$VsixPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($VsixPath)

    try {
        $entry = $archive.GetEntry('extension.vsixmanifest')

        if (-not $entry) {
            throw "VSIX 缺少 extension.vsixmanifest：$VsixPath"
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())

        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $namespace = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespace.AddNamespace('vsix', 'http://schemas.microsoft.com/developer/vsx-schema/2011')
    $identity = $manifest.SelectSingleNode('//vsix:Identity', $namespace)

    if (-not $identity) {
        throw "VSIX 的 Manifest 沒有 Identity：$VsixPath"
    }

    return [string]$identity.Version
}

<#
.SYNOPSIS
    找出已安裝的 SqlAssist，回傳版號與所在資料夾。

.DESCRIPTION
    掃描每一個 SSMS 22 的使用者 hive。讀不到的 Manifest 一律略過而不是中止：
    那幾乎都是別的擴充，與這裡要找的東西無關，警告出來只是雜訊。
#>
function Get-SqlAssistInstallation {
    [CmdletBinding()]
    param([string]$ExtensionId)

    if (-not $ExtensionId) {
        $ExtensionId = Get-SqlAssistExtensionId
    }

    $installations = @()
    $hives = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\SSMS" `
        -Directory `
        -Filter '22.0_*' `
        -ErrorAction SilentlyContinue

    foreach ($hive in $hives) {
        $manifests = Get-ChildItem -Path (Join-Path $hive.FullName 'Extensions') `
            -Recurse `
            -File `
            -Filter 'extension.vsixmanifest' `
            -ErrorAction SilentlyContinue

        foreach ($manifestFile in $manifests) {
            try {
                [xml]$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw
                $identity = $manifest.PackageManifest.Metadata.Identity

                if ($identity.Id -eq $ExtensionId) {
                    $installations += [pscustomobject]@{
                        Version = [string]$identity.Version
                        Path = $manifestFile.Directory.FullName
                    }
                }
            }
            catch {
                Write-Verbose "略過讀不到的延伸模組資訊：$($manifestFile.FullName)"
            }
        }
    }

    return @($installations | Sort-Object Path -Unique)
}

<#
.SYNOPSIS
    確認 SSMS 沒有在執行。

.DESCRIPTION
    擴充的檔案在 SSMS 執行中是鎖住的，複製會半途失敗並留下新舊混合的目錄。
#>
function Assert-SsmsClosed {
    [CmdletBinding()]
    param([string]$Action = '執行這個操作')

    if (Get-Process -Name 'SSMS' -ErrorAction SilentlyContinue) {
        throw "請先儲存查詢並關閉所有 SSMS 視窗，再$Action。"
    }
}

Export-ModuleMember -Function `
    Initialize-SqlAssistUtf8Output, `
    Get-SqlAssistRoot, `
    Get-SsmsInstallPath, `
    Get-SsmsVsixInstaller, `
    Get-SqlAssistExtensionId, `
    Get-SqlAssistVsixPath, `
    Get-SqlAssistVsixVersion, `
    Get-SqlAssistInstallation, `
    Assert-SsmsClosed
