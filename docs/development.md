# 建置、測試與工具

## 環境需求

- Windows x64
- PowerShell 7+
- SQL Server Management Studio 22.9.x
- Visual Studio 18／2026 或相容的 MSBuild
- .NET SDK 10.0.400

## 建置與測試

```powershell
Set-Location 'D:\GitProject\SqlAssist.Ssms22'
.\tools\Run-CoreTests.ps1
.\tools\Build-Extension.ps1
```

預期輸出：

```text
src\SqlAssist.Ssms22\bin\x64\Release\net48\SqlAssist.Ssms22.vsix
```

測試執行器由 `global.json` 的 `test.runner` 指定為 Microsoft.Testing.Platform
（.NET 10 SDK 不再支援 VSTest 轉接層）。

AI 執行上述流程時，使用 [共用輸出包裝器](ai-workflow.md#工具輸出節流)，只縮短呈現，
不改變測試、建置範圍或結束碼。人工需要即時完整輸出時仍可直接執行原腳本。

### push 前自動跑測試

版本庫附了一個 `pre-push` hook。git 不會自動套用版控裡的 hook，clone 之後要手動
指一次：

```powershell
git config core.hooksPath .githooks
```

之後每次 `git push` 都會依序跑 `Check-TextFiles.ps1`、`Check-Docs.ps1`、
`Test-AgentWorkflow.ps1` 與 `Run-CoreTests.ps1`，任何一個失敗就擋下來。
真的要略過時用 `git push --no-verify`。


### PowerShell 輸出編碼

工具一律使用 **PowerShell 7+**。檔案是 UTF-8，不代表子程序的輸出也會是 UTF-8；
Git GUI、終端機與無主控台程序可能使用不同代碼頁。所有 PS1 在執行工作前共用：

```powershell
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
# 回傳給目前腳本的作用域，避免只改到模組內的管道偏好。
$OutputEncoding = Initialize-SqlAssistUtf8Output
```

父程序若用 ProcessStartInfo 讀取這些腳本，stdout／stderr 解碼也要明確指定 UTF-8；
只指定父端編碼不會替子程序轉碼。[Microsoft 說明](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.standardoutputencoding)
建置查詢 vswhere 另加 `-utf8`，避免中文安裝路徑被破壞。
節流器的原始命令紀錄仍直接保存位元組，不把外部程式的 OEM 輸出硬轉成 UTF-8。

`Test-AgentWorkflow.ps1` 會檢查所有腳本的共用入口，並從 UTF-8、Big5、CP437 啟動子程序，
驗證中文／Emoji、stdout／stderr、原生管道、Git 檔名及失敗結束碼。不以略過 hook 解決亂碼。

### 文字檔格式

所有文字檔統一為 **UTF-8（無 BOM）與 LF**。根目錄的 `.gitattributes` 會覆蓋
Windows 全域的 `core.autocrlf=true`，`.editorconfig` 則讓支援它的編輯器在儲存時沿用
同一份規則。這樣產生器或補丁工具寫出的 LF 不必再整檔「還原 CRLF」。

push 前的 hook 會先執行下列檢查，遇到 BOM、CRLF、無效 UTF-8 或缺少檔尾換行就停止：

```powershell
.\tools\Check-TextFiles.ps1
```

## 工具腳本的共用設定

SSMS 的安裝路徑、擴充的 Identity Id 與「已安裝的 SqlAssist 在哪裡」全部在
`tools\SqlAssist.Tools.psm1`，每支腳本都從那裡取。SSMS 裝在別的位置時不必改腳本，
傳 `-SsmsInstallDir` 就好：

```powershell
.\tools\Build-Extension.ps1 -SsmsInstallDir 'D:\SSMS 22\Release'
```

`Install-Extension.ps1`、`Uninstall-Extension.ps1`、`Deploy-DebugExtension.ps1` 與
`Generate-Keywords.ps1` 收同一個參數。專案檔的 `SsmsInstallDir` 屬性另有一份預設值，
因為 MSBuild 讀不到 PowerShell 模組；`Build-Extension.ps1` 一律把解析後的路徑
以 `/p:SsmsInstallDir=` 傳進去，不靠專案檔那份。

## 工具腳本

AI 輔助腳本不用每天手動跑；先看 [三個腳本的白話用途](ai-workflow.md#腳本用途)。

| 腳本 | 做什麼 |
|---|---|
| `Run-CoreTests.ps1` | 以方案為目標跑單元測試（執行器由 `global.json` 指定） |
| `Build-Extension.ps1` | 建置並產出 VSIX |
| `Install-Extension.ps1` | 以官方 VSIXInstaller 安裝 |
| `Uninstall-Extension.ps1` | 解除安裝（保留使用者設定與紀錄） |
| `Deploy-DebugExtension.ps1` | 部署 Debug 組件並**清除 MEF 快取**，供 F5 偵錯 |
| `Show-Diagnostics.ps1` | 顯示安裝狀態與最近的診斷紀錄 |
| `Generate-Keywords.ps1` | 以 ScriptDom 重新產生 `SqlKeywordCatalog.Generated.cs` |
| `Publish-Release.ps1` | 建置、驗證並建立 GitHub 草稿 Release |
| `Test-VsixPackage.ps1` | 檢查 VSIX 套件結構 |
| `Test-CommandTable.ps1` | 交叉驗證 VSCT、`CommandIds` 與註冊檔的命令識別碼 |
| `Check-TextFiles.ps1` | 檢查文字檔皆為 UTF-8（無 BOM）、LF 且有檔尾換行 |
| `Check-Docs.ps1` | 檢查文件的大小預算與所有 Markdown 連結和錨點 |
| `Read-Context.ps1` | 給 AI 分段讀長檔，不一次讀完整份 |
| `Invoke-QuietCommand.ps1` | 給 AI 短輸出，完整命令紀錄留在磁碟 |
| `Test-AgentWorkflow.ps1` | 工具回歸檢查，不是產品單元測試 |
| `SqlAssist.Tools.psm1` | 共用 UTF-8 輸出、SSMS 路徑與擴充 Id 探索 |
