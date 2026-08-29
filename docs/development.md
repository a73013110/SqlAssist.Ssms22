# 建置、安裝與偵錯

## 環境需求

- Windows x64
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

## 版本號

版號的唯一來源是根目錄的 `version.json` 加上 git 歷史，由
[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) 在建置時計算。
專案檔、VSIX Manifest 與 README 都不再寫死版號。

```json
{ "version": "0.14" }
```

`version` 只寫 `major.minor`，第三段（patch）填的是 **git height**——從 HEAD 回推到
`version.json` 的 `version` 最後一次變動之間的 commit 數。因此：

| 產物 | 格式 | 範例 |
|---|---|---|
| VSIX Manifest／`AssemblyFileVersion` | `major.minor.height.commitId` | `0.14.7.64243` |
| `AssemblyInformationalVersion` | `major.minor.height+commitId` | `0.14.7+faf306205d` |
| `AssemblyVersion` | `major.minor.0.0` | `0.14.0.0` |

第三段每個 commit 遞增，所以**每一次 commit 建出來的 VSIX 都能直接覆蓋安裝**，
不必再手動把 Manifest 的版號 +1。這正是舊版 Manifest 一路累加到 `0.13.14`，
而專案檔還停在 `0.13.1` 的原因。第四段由 commit id 推導、不遞增，只用來回推來源。

### 什麼時候要改 version.json

只有 **minor 或 major 要進位時**才改，patch 自己會走：

```powershell
# 開始開發 0.15 這一輪。改完 commit，height 歸零重算。
# version.json: "version": "0.15"
git commit -am "build: 版號進入 0.15"
git tag v0.15.0    # 選用，只是給人看的發布記錄，不影響版號計算
```

Tag 不參與版號計算，加不加都不影響建置結果。

### 三個會踩到的地方

- **改了程式卻沒 commit，版號不動。** height 是從 commit 算的，工作目錄的變更不列入。
  日常偵錯走 `Deploy-DebugExtension.ps1`（直接覆蓋檔案、不比對版號遞增），不受影響。
- **淺層 clone 會靜靜退成 `0.0.x`。** CI 上 `actions/checkout` 必須設
  `fetch-depth: 0`。`Test-VsixPackage.ps1` 會擋下這種版號，不會讓它包成 VSIX。
- **只改文件不會推進版號。** `version.json` 的 `pathFilters` 排除了 `docs/`、
  `README.md`、`CLAUDE.md` 與 `LICENSE`，因為那些內容不進 VSIX，
  不該讓已安裝的使用者看到一個「新版本」。

`Deploy-DebugExtension.ps1` 只比對已安裝與建置版號的 `major.minor`。兩者不同時
代表 pkgdef、vsct 或 Manifest 的註冊內容已經改變，必須重跑 `Install-Extension.ps1`，
光覆蓋 DLL 不夠。

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

## 解除安裝

先儲存查詢並關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Uninstall-Extension.ps1
```

預設會顯示 VSIXInstaller 確認介面，如需無介面模式加上 `-Quiet`。
解除安裝只移除 VSIX，會保留 `%LOCALAPPDATA%\SqlAssist.Ssms22` 內的設定與診斷紀錄。

## 診斷

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log
```

```powershell
.\tools\Show-Diagnostics.ps1
```
