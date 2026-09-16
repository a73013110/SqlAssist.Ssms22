#Requires -Version 7.0
Set-StrictMode -Version Latest

function Get-SqlAssistDeploymentFile {
    # 封裝與 Debug 部署共用白名單；建置輸出中的 System.* 絕不能整批帶進宿主。
    foreach ($name in @('SqlAssist.Core', 'SqlAssist.Metadata', 'SqlAssist.Ssms22',
            'SqlAssist.SqlMemory.Isolation', 'SqlAssist.SqlMemory.Sqlite')) {
        [pscustomobject]@{ Name = "$name.dll"; Policy = 'Replace'; Required = $true }
        [pscustomobject]@{ Name = "$name.pdb"; Policy = 'Replace'; Required = $false }
    }
    [pscustomobject]@{ Name = 'SqlAssist.registration.json'; Policy = 'Replace'; Required = $true }
    foreach ($name in @('SqlAssist.Ssms22.pkgdef', 'SqlMemory.Isolation.config',
            'ThirdPartyLicenses.txt', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll',
            'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll',
            'e_sqlite3.dll', 'Microsoft.SqlServer.TransactSql.ScriptDom.dll', 'SqlAssist.Icon.512.png')) {
        [pscustomobject]@{ Name = $name; Policy = 'Install'; Required = $true }
    }
    [pscustomobject]@{ Name = 'extension.vsixmanifest'; Policy = 'Manifest'; Required = $true }
}

function Get-DeploymentManifest {
    param([string]$Path)

    [xml]$manifest = [IO.File]::ReadAllText($Path)
    $namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespace.AddNamespace('v', 'http://schemas.microsoft.com/developer/vsx-schema/2011')
    $identity = $manifest.SelectSingleNode('/v:PackageManifest/v:Metadata/v:Identity', $namespace)
    $version = $null
    if ($null -eq $identity -or -not [version]::TryParse($identity.GetAttribute('Version'), [ref]$version)) {
        throw "無法解析 VSIX Identity 版號：$Path"
    }

    # 只忽略每次提交改變的版號與 XML 註解；其餘安裝資產／註冊變化都必須重裝。
    $identity.SetAttribute('Version', "$($version.Major).$($version.Minor)")
    foreach ($comment in @($manifest.SelectNodes('//comment()'))) {
        $null = $comment.ParentNode.RemoveChild($comment)
    }
    return $manifest.OuterXml
}

function Get-DeploymentComparableHash {
    param(
        [string]$Path,
        [string]$Name
    )

    # VS 會每次建置都更新 CacheTag；它不是命令表／註冊內容，不能讓每次 Deploy 都被迫 Install。
    if ($Name -eq 'SqlAssist.Ssms22.pkgdef') {
        $text = [IO.File]::ReadAllText($Path)
        $text = [regex]::Replace($text, '(?m)^("CacheTag"=qword:)[0-9A-Fa-f]+\s*$', '$1<volatile>')
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
        finally { $sha.Dispose() }
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Invoke-SqlAssistDebugFileDeployment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$InstallationPath
    )

    $ErrorActionPreference = 'Stop'
    $sourceItem = Get-Item -LiteralPath $OutputPath
    $targetItem = Get-Item -LiteralPath $InstallationPath
    if (-not $sourceItem.PSIsContainer -or -not $targetItem.PSIsContainer) {
        throw '建置與安裝路徑都必須是資料夾。'
    }
    $sourceRoot = $sourceItem.FullName
    $targetRoot = $targetItem.FullName
    if ($sourceRoot -eq $targetRoot -or
        $sourceRoot.StartsWith($targetRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $targetRoot.StartsWith($sourceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '建置與安裝目錄不可相同或互相包含。'
    }

    $files = @(Get-SqlAssistDeploymentFile)
    $plan = @()
    $staleSymbols = @()
    $problems = @()
    foreach ($file in $files) {
        $source = Join-Path $sourceRoot $file.Name
        $target = Join-Path $targetRoot $file.Name
        $sourceExists = Test-Path -LiteralPath $source -PathType Leaf
        $targetExists = Test-Path -LiteralPath $target -PathType Leaf
        if (-not $sourceExists) {
            if ($file.Required -or (Test-Path -LiteralPath $source)) {
                $problems += "缺少必要來源檔案或不是檔案：$($file.Name)"
            }
            elseif ($targetExists) {
                $staleSymbols += $target
            }
            elseif (Test-Path -LiteralPath $target) {
                $problems += "部署目的地不是檔案：$($file.Name)"
            }
            continue
        }
        if ((Test-Path -LiteralPath $target) -and -not $targetExists) {
            $problems += "部署目的地不是檔案：$($file.Name)"
            continue
        }
        if ($file.Required -and -not $targetExists) {
            $problems += "安裝缺少 $($file.Name)；請執行 Install-Extension.ps1 -Configuration Debug。"
        }

        # 先讀完並雜湊全部來源，再開始任何覆寫，避免晚發現缺檔而混用新舊組件。
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $comparableHash = Get-DeploymentComparableHash -Path $source -Name $file.Name
        $targetComparableHash = if ($targetExists) {
            Get-DeploymentComparableHash -Path $target -Name $file.Name
        } else { $null }
        if ($targetExists -and $file.Policy -eq 'Install' -and
            $comparableHash -ne $targetComparableHash) {
            $problems += "安裝資產已變更：$($file.Name)；請執行 Install-Extension.ps1 -Configuration Debug。"
        }
        if ($targetExists -and $file.Policy -eq 'Manifest' -and
            (Get-DeploymentManifest $source) -ne (Get-DeploymentManifest $target)) {
            $problems += 'Manifest／major.minor 已變更；請執行 Install-Extension.ps1 -Configuration Debug。'
        }
        $plan += [pscustomobject]@{
            Name = $file.Name; Source = $source; Target = $target; Hash = $hash
            ComparableHash = $comparableHash; TargetComparableHash = $targetComparableHash
            Policy = $file.Policy
        }
    }
    if ($problems.Count -gt 0) {
        throw ($problems -join [Environment]::NewLine)
    }

    foreach ($file in $plan | Where-Object Policy -EQ 'Replace') {
        Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force
    }
    foreach ($file in $plan) {
        # 目的檔與預檢的來源快照比對；也拒絕在部署期間被另一個建置改寫的來源。
        if ((Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash -ne $file.Hash) {
            throw "部署後 SHA-256 驗證失敗：$($file.Name)。請勿啟動 SSMS，重新建置部署或 Install。"
        }
        if ($file.Policy -eq 'Replace' -and
            (Get-FileHash -LiteralPath $file.Target -Algorithm SHA256).Hash -ne $file.Hash) {
            throw "部署後 SHA-256 驗證失敗：$($file.Name)。請勿啟動 SSMS，重新建置部署或 Install。"
        }
        if ($file.Policy -eq 'Install' -and
            (Get-DeploymentComparableHash -Path $file.Target -Name $file.Name) -ne $file.TargetComparableHash) {
            throw "安裝資產在部署期間改變：$($file.Name)。請勿啟動 SSMS，改用 Install。"
        }
        if ($file.Policy -eq 'Manifest' -and
            (Get-DeploymentManifest $file.Target) -ne (Get-DeploymentManifest $file.Source)) {
            throw 'Manifest 在部署期間改變；請勿啟動 SSMS，改用 Install。'
        }
    }
    foreach ($path in $staleSymbols) {
        # PDB 可省略，但不能讓舊符號繼續對應已更新的 DLL；只刪白名單中的單一檔案。
        Remove-Item -LiteralPath $path -Force
    }
    return @($plan | Where-Object Policy -EQ 'Replace' | Select-Object Name, Hash)
}

Export-ModuleMember -Function Get-SqlAssistDeploymentFile, Invoke-SqlAssistDebugFileDeployment
