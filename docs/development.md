# 建置、安裝與偵錯

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

## 版本號

版號的唯一來源是根目錄的 `version.json` 加上 git 歷史，由
[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) 在建置時計算。
專案檔、VSIX Manifest 與 README 都不再寫死版號。

```json
{ "version": "0.15" }
```

`version` 只寫 `major.minor`，第三段（patch）填的是 **git height**——從 HEAD 回推到
`version.json` 的 `version` 最後一次變動之間的 commit 數。因此：

| 產物 | 格式 | 範例（height 7） |
|---|---|---|
| VSIX Manifest／`AssemblyFileVersion` | `major.minor.height.commitId` | `major.minor.7.64243` |
| `AssemblyInformationalVersion` | `major.minor.height+commitId` | `major.minor.7+faf306205d` |
| `AssemblyVersion` | `major.minor.0.0` | `major.minor.0.0` |

第三段每個 commit 遞增，所以**每一次 commit 建出來的 VSIX 都能直接覆蓋安裝**，
不必再手動把 Manifest 的版號 +1。這正是舊版 Manifest 一路累加到 `0.13.14`，
而專案檔還停在 `0.13.1` 的原因。第四段由 commit id 推導、不遞增，只用來回推來源。

### 什麼時候要改 version.json

只有 **minor 或 major 要進位時**才改，patch 自己會走：

```powershell
# 把 version.json 的 minor 加一，開始新的一輪。改完 commit，height 歸零重算。
git commit -am "build: 版號進入 <新的 major.minor>"
git tag v<新的 major.minor>.0    # 選用，只是給人看的發布記錄，不影響版號計算
```

Tag 不參與版號計算，加不加都不影響建置結果。

### 四個會踩到的地方

- **改了程式卻沒 commit，版號不動。** height 是從 commit 算的，工作目錄的變更不列入。
  日常偵錯走 `Deploy-DebugExtension.ps1`（直接覆蓋檔案、不比對版號遞增），不受影響。
- **淺層 clone 會靜靜退成 `0.0.x`。** CI 上 `actions/checkout` 必須設
  `fetch-depth: 0`。`Test-VsixPackage.ps1` 會擋下這種版號，不會讓它包成 VSIX。
- **只改文件不會推進版號。** `version.json` 的 `pathFilters` 排除了 `docs/`、
  根目錄的說明文字（`README.md`、`CLAUDE.md`、`AGENTS.md`、`LICENSE`）、代理設定
  （`.claude/`、`.codex/`、`.mcp.json`）與只當參考的 `menus.decompiled.*`，
  因為那些內容不進 VSIX，不該讓已安裝的使用者看到一個「新版本」。
- **加 pathFilters 會讓版號倒退。** 排除項目變多，height 就重新算成一個更小的數，
  已安裝的使用者會覆蓋不了。所以調整 `pathFilters` 只能跟 minor 進位放在同一個
  commit——那時 height 本來就歸零，不存在倒退。

`Deploy-DebugExtension.ps1` 只比對已安裝與建置版號的 `major.minor`。兩者不同時
代表 pkgdef、vsct 或 Manifest 的註冊內容已經改變，必須重跑 `Install-Extension.ps1`，
光覆蓋 DLL 不夠。

## 發布

```powershell
.\tools\Publish-Release.ps1
```

腳本會依序確認發布前提（在 `master`、工作樹乾淨、本機沒有領先遠端、`gh` 已登入）、
跑測試、建 VSIX，然後以產物的 Identity 版號打 tag，在 GitHub 上建立**草稿** Release
並附上 VSIX。

**草稿是刻意的，不要改成直接發布。** VSIX 有一種只在實機才看得出來的失敗：MEF
匯出型別的命名空間變動後，SSMS 的元件快取會安靜地讓那些部件建立失敗——沒有例外、
沒有記錄，只有「功能整組消失」。`Test-VsixPackage.ps1` 驗得了封裝內容，驗不了這件事。
所以最後一關必須是人：把草稿的 VSIX 裝進 SSMS 確認過，再到 GitHub 按 Publish。

同一個 commit 只發布一次。tag 已存在時腳本會擋下來——有新變更就先 commit，
版號的 height 會自己往前。

這也是這個專案不架 CI 建置 VSIX 的原因：CI 複製得了建置，複製不了上面那一關。

## 安裝

先關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Install-Extension.ps1
```

安裝程式會開啟 SSMS 隨附的 `VSIXInstaller.exe`，請在畫面中確認安裝目標為
**SQL Server Management Studio 22**。安裝後重新啟動 SSMS。

## 開發偵錯

Visual Studio 的 `SqlAssist.Ssms22` Debug Profile 會以 Managed Debugger 直接啟動 SSMS。
第一次偵錯前先安裝 Debug VSIX：

```powershell
.\tools\Build-Extension.ps1 -Configuration Debug
.\tools\Install-Extension.ps1 -Configuration Debug
```

同一次 F5 工作階段中的方法內容修改可使用 Hot Reload。需要重新啟動 SSMS 時，先關閉
SSMS，再用下列命令建立並部署最新的 DLL/PDB；腳本會依 Extension ID 自動尋找安裝目錄，
不需寫死 VSIXInstaller 產生的隨機資料夾名稱：

```powershell
.\tools\Deploy-DebugExtension.ps1
```

若已在 Visual Studio 建立過最新 Debug 輸出，可略過重複建置：

```powershell
.\tools\Deploy-DebugExtension.ps1 -SkipBuild
```

修改 VSIX Manifest、PkgDef、VSCT 或版本號時，仍須重新執行 Debug VSIX 安裝，
不能只部署 DLL。

### SSMS 的兩份快取

SSMS 把 MEF 組合圖與 Unified Settings 的定義各自快取在
`%LOCALAPPDATA%\Microsoft\SSMS.0_*\` 底下：

| 快取 | 內容 | 沒更新時的症狀 |
|---|---|---|
| `ComponentModelCache\` | MEF 組合圖，記的是**完整型別名稱** | 匯出的部件安靜地建立失敗 |
| `UnifiedSettings\DefinitionCache.dat` | 各擴充註冊的設定定義 | 設定頁少一項，讀取回報 `NotPersisted` |

兩份都以「安裝擴充」為更新時機，**不看擴充資料夾裡的 DLL 有沒有換過**。
因此把一個 MEF 匯出的類別搬到別的命名空間、只部署 DLL 的話，快取仍然要求舊的
完整型別名稱，那個部件就再也建立不出來——**沒有例外、沒有記錄、沒有任何錯誤訊息**，
只有功能整組消失。

`Deploy-DebugExtension.ps1` 因此每次部署都會刪掉這兩份快取，SSMS 下次啟動時重建
（那一次啟動會慢幾秒）。

怎麼認出這個症狀：記錄檔裡**沒有**這一行，就代表
`SqlAssistTextViewCreationListener` 根本沒被建立出來，也就是 MEF 快取過期了。

```text
SQL 編輯器已建立，SqlAssist 已載入
```

這種失效有一個好認的形狀：**沒搬過命名空間的部件照常運作，搬過的整組失效**。
建議清單還在、停留提示還在、預覽視窗開得起來，但 Tab 不展開、關鍵字不大寫、
Esc 關不掉預覽、輸入點號不重開清單——那就是這一件事，不是四個 bug。

### 第三份：命令表

命令表（選單項目、命令識別碼、鍵繫結）雖然編譯在 `SqlAssist.Ssms22.dll` 的資源裡，
殼層卻是照 pkgdef 的 `Menus.ctmenu, N` 那個 **N** 決定要不要重讀的。清快取救不了
它——pkgdef 本身也不在部署清單裡。

因此新增命令、選單項目或鍵繫結時，兩件事一定要一起做：

1. `SqlAssistPackage` 的 `[ProvideMenuResource("Menus.ctmenu", N)]` 版號加一。
2. 用 `Install-Extension.ps1` 重新安裝，**不要**用 `Deploy-DebugExtension.ps1`。

漏掉的症狀與 MEF 快取同一類：新的選單項目不出現、新綁的鍵完全沒反應，
沒有例外也沒有記錄。第 1 件會讓第 2 件變成強制——部署腳本比對兩邊的 N，
不一致就直接擋下來並要求重裝。

## 解除安裝

先儲存查詢並關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Uninstall-Extension.ps1
```

預設會顯示 VSIXInstaller 確認介面，如需無介面模式加上 `-Quiet`。
解除安裝只移除 VSIX，會保留 `%LOCALAPPDATA%\SqlAssist.Ssms22` 內的設定與診斷紀錄。

## 診斷

SSMS 裡的 **工具 → SqlAssist → 關於與診斷…** 會顯示發布版號、Build commit、SSMS／Windows
環境、目前生效的設定與健康檢查。按「複製診斷資訊」產生的摘要不含 SQL、伺服器名稱、
資料庫名稱與 Windows 使用者名稱，適合直接貼到公開 Issue。

完整紀錄用於需要逐步追查的問題，仍可能包含資料庫物件名稱；分享前要先檢查內容。

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log
```

```powershell
.\tools\Show-Diagnostics.ps1
```

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
