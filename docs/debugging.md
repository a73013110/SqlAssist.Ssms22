# SSMS 開發偵錯與診斷

本頁包含開發偵錯流程、Debug 部署契約、SSMS 的快取，以及詳細記錄診斷；安裝與版本見[發布](release.md)。

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

Deploy 不是安裝程式：首次安裝、修改 VSIX Manifest、PkgDef、VSCT 或 major.minor 時仍須重新執行
Debug VSIX 安裝，且必須先建置相同 Configuration 的 VSIX。

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

命令表不看這兩份快取，清了也沒用；改命令表的版號與重裝規則見[殼層命令](shell-commands.md#改了命令表一定要重新安裝)。

## Deploy 的必要與可選檔案

`tools/SqlAssist.Deployment.psm1` 是 Debug 部署及 VSIX 必要檔案檢查的共用白名單。
新增相依時須一併更新清單與隔離測試，不能把整個建置輸出複製到 SSMS。

| 分類 | Deploy 行為 |
|---|---|
| 五個產品 DLL：Core、Metadata、Ssms22、SqlMemory.Isolation／Sqlite | 必要；來源與安裝都須存在，允許更新 |
| `SqlAssist.registration.json` | 必要；允許更新，隨後清除設定定義快取 |
| 上述產品 DLL 的 PDB | 可選；存在就部署，來源缺少時移除對應的舊 PDB，避免符號錯配 |
| provider、native DLL、ScriptDom、隔離 config、授權、圖示、pkgdef | 必要；來源與安裝 SHA-256 不同（pkgdef 的 CacheTag 除外）或安裝缺檔就要求 Install，不覆寫 |
| Manifest | 必要；只忽略 Identity 版號的 patch／revision 與 XML 註解，其餘差異要求 Install，不覆寫 |
| 其他建置輸出，包括 System.*、probe、VSIX | 不部署；封裝檢查仍拒絕夾帶 System.* |

舊安裝缺少 SQL Memory 資產時，不能靠 Deploy 補齊。即使 major.minor 相同，
命令表資源版號或其他 pkgdef 內容變更也會要求 Install；不再只比較命令表的一個數字。

## Deploy 的失敗邊界

1. 正式入口先確認 SSMS 已關閉；預設建立完整 Debug VSIX，`-SkipBuild` 由呼叫者保證產物最新。
2. 複製前檢查全部來源與安裝必要檔案、安裝相容性，並預先計算 SHA-256；預檢失敗不改寫安裝。
3. 只覆寫白名單允許更新的檔案；部署後以預檢 hash 核對來源及目的地，成功後才清快取。

此流程不是檔案系統交易。複製中遭遇鎖定、磁碟故障或另一個建置改寫來源，仍可能留下部分更新；
失敗後不要啟動 SSMS，修正原因並重新部署或 Install。安裝缺檔與相依升級不會先複製一部分才失敗。

## Deploy 的隔離回歸

先建置 Debug，再執行 `tools/Test-DebugDeployment.ps1`。成功案例使用真正 Debug 產物，
負向矩陣用小型 fixture；全部寫入被忽略的 `artifacts/debug-deployment-tests/`，保留失敗證據。
測試不探索或修改使用者安裝目錄，也不清除真正快取。

預檢失敗須比對整個 fixture 安裝目錄未變。工具測試不能取代實際部署與 SSMS 重啟驗收；
驗收流程見[SQL Memory 驗收](sql-memory-validation.md)。

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
