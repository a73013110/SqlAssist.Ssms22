[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$VsixPath
)

$ErrorActionPreference = 'Stop'
$requiredEntries = @(
    'extension.vsixmanifest',
    'SqlAssist.Core.dll',
    'SqlAssist.Metadata.dll',
    'SqlAssist.Ssms22.dll',
    'SqlAssist.Ssms22.pkgdef'
)

if (-not (Test-Path -LiteralPath $VsixPath)) {
    throw "找不到 VSIX：$VsixPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($VsixPath)

try {
    $entryNames = @($archive.Entries.FullName)

    foreach ($entryName in $requiredEntries) {
        if ($entryName -notin $entryNames) {
            throw "VSIX 缺少必要檔案：$entryName"
        }
    }

    $manifestEntry = $archive.GetEntry('extension.vsixmanifest')
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())

    try {
        [xml]$manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $namespace = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespace.AddNamespace('vsix', 'http://schemas.microsoft.com/developer/vsx-schema/2011')
    $target = $manifest.SelectSingleNode(
        '//vsix:InstallationTarget[@Id="Microsoft.VisualStudio.Ssms"]',
        $namespace)

    if ($null -eq $target -or $target.Version -ne '[22.0,23.0)') {
        throw 'VSIX 未正確限定 SSMS 22。'
    }

    $packageAsset = $manifest.SelectSingleNode(
        '//vsix:Asset[@Type="Microsoft.VisualStudio.VsPackage"]',
        $namespace)

    if ($null -eq $packageAsset -or $packageAsset.Path -ne 'SqlAssist.Ssms22.pkgdef') {
        throw 'VSIX 缺少 AsyncPackage 註冊資產。'
    }

    $pkgdefEntry = $archive.GetEntry('SqlAssist.Ssms22.pkgdef')
    $pkgdefReader = [System.IO.StreamReader]::new($pkgdefEntry.Open())

    try {
        $pkgdef = $pkgdefReader.ReadToEnd()
    }
    finally {
        $pkgdefReader.Dispose()
    }

    # 沒有此註冊時，MEF 快捷功能可運作，但「工具 > SqlAssist」不會載入。
    if ($pkgdef -notmatch [regex]::Escape('AutoLoadPackages\{adfc4e64-0397-11d1-9f4e-00a0c911004f}')) {
        throw 'VSIX 缺少 SqlAssist AsyncPackage 的自動載入註冊。'
    }

    if ($pkgdef -notmatch [regex]::Escape('Menus.ctmenu')) {
        throw 'VSIX 缺少 SqlAssist 工具選單註冊。'
    }

    Write-Host "VSIX 套件檢查通過：$($archive.Entries.Count) 個檔案"
}
finally {
    $archive.Dispose()
}
