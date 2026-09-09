#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$VsixPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
$OutputEncoding = Initialize-SqlAssistUtf8Output

# 讀組件參考表只需要 metadata，不必真的載入組件——PowerShell 7 已經不支援 ReflectionOnly。
function Get-SystemAssemblyReference {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$AssemblyPath
    )

    $versions = @{}
    $stream = [System.IO.File]::OpenRead($AssemblyPath)

    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)

        try {
            # 原生 DLL 與資源 DLL 沒有 metadata；掃目錄時會遇到，直接視為沒有參考。
            try {
                $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            }
            catch [System.InvalidOperationException] {
                return $versions
            }

            foreach ($handle in $metadata.AssemblyReferences) {
                $reference = $metadata.GetAssemblyReference($handle)
                $name = $metadata.GetString($reference.Name)

                if (-not $name.StartsWith('System.', [System.StringComparison]::Ordinal)) {
                    continue
                }

                if (-not $versions.ContainsKey($name) -or $versions[$name] -lt $reference.Version) {
                    $versions[$name] = $reference.Version
                }
            }
        }
        finally {
            $peReader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return $versions
}

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

    # 淺層 clone 或 Nerdbank.GitVersioning 沒生效時，版號會靜靜退成 0.0.x，或留下未展開的
    # GetBuildVersion 佔位符，包出一個永遠蓋不過既有安裝的 VSIX。擋在打包驗證最便宜。
    $identity = $manifest.SelectSingleNode('//vsix:Identity', $namespace)
    $identityVersion = $null

    if ($null -eq $identity -or -not [version]::TryParse($identity.Version, [ref]$identityVersion)) {
        throw "VSIX 版號未展開為實際版本：$($identity.Version)"
    }

    if ($identityVersion.Major -eq 0 -and $identityVersion.Minor -eq 0) {
        throw "VSIX 版號為 $identityVersion，表示建置時取不到 git 歷史或 version.json。請確認是完整 clone。"
    }

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

    # 夾帶 BCL 外掛組件會和 SSMS 已載入的那份撞型別，MEF 部件會安靜地建立失敗。
    $bundledSystemAssembly = $entryNames | Where-Object { $_ -like 'System.*.dll' }

    if ($bundledSystemAssembly) {
        throw "VSIX 夾帶了應由 SSMS 提供的組件：$($bundledSystemAssembly -join '、')"
    }

    # 0.17.1 在較舊 SSMS 上建議清單整組失效，就是從 PublicAssemblies 抓 DLL 當參考造成的：
    # 那會把建置機當下的修補版本（例如 System.Collections.Immutable 10.0.0.10）寫進參考，而
    # Ssms.exe.config 的 bindingRedirect 只涵蓋到宿主自己那份，裝到較舊的 SSMS 上就是
    # FileNotFoundException——沒有降級，只有用到的功能整組消失。契約版本一律是 x.0.0.0。
    $extensionDll = Join-Path ([System.IO.Path]::GetTempPath()) "SqlAssist.VsixCheck.$([Guid]::NewGuid().ToString('N')).dll"
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile(
        $archive.GetEntry('SqlAssist.Ssms22.dll'), $extensionDll, $true)

    try {
        $references = Get-SystemAssemblyReference -AssemblyPath $extensionDll

        if ($references.Count -eq 0) {
            throw 'SqlAssist.Ssms22.dll 讀不到任何組件參考，metadata 檢查失效。'
        }

        foreach ($reference in $references.GetEnumerator()) {
            if ($reference.Value.Build -eq 0 -and $reference.Value.Revision -eq 0) {
                continue
            }

            # 較舊的修補版本繫結得起來，但分不出是刻意選的還是抓到了安裝目錄，一律擋下重看一次。
            throw ("$($reference.Key) 參考修補版本 $($reference.Value)，契約版本一律是 x.0.0.0。" +
                '請改用 NuGet 的契約版本（PackageReference 加 ExcludeAssets="runtime"）；' +
                '若這個修補版本確實是刻意選的，就在這裡列為例外。')
        }
    }
    finally {
        Remove-Item -LiteralPath $extensionDll -Force -ErrorAction Ignore
    }

    Write-Host "VSIX 套件檢查通過：$($archive.Entries.Count) 個檔案"
}
finally {
    $archive.Dispose()
}
