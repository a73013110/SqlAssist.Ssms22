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
