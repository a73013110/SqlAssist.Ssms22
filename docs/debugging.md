# SSMS 開發偵錯與診斷

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
