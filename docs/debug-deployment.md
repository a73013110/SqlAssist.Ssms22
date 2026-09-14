# Debug 部署完整性

入口與快取行為見[偵錯](debugging.md)。Deploy 不是安裝程式；首次安裝與安裝資產升級仍走
[Install](release.md#安裝)，且必須先建置相同 Configuration 的 VSIX。

## 必要與可選檔案

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
VSCT 變更仍須遵守[平台護欄](rules-platform.md)的命令資源版號規則。

## 失敗邊界

1. 正式入口先確認 SSMS 已關閉；預設建立完整 Debug VSIX，`-SkipBuild` 由呼叫者保證產物最新。
2. 複製前檢查全部來源與安裝必要檔案、安裝相容性，並預先計算 SHA-256；預檢失敗不改寫安裝。
3. 只覆寫白名單允許更新的檔案；部署後以預檢 hash 核對來源及目的地，成功後才清快取。

此流程不是檔案系統交易。複製中遭遇鎖定、磁碟故障或另一個建置改寫來源，仍可能留下部分更新；
失敗後不要啟動 SSMS，修正原因並重新部署或 Install。安裝缺檔與相依升級不會先複製一部分才失敗。

## 隔離回歸

先建置 Debug，再執行 `tools/Test-DebugDeployment.ps1`。成功案例使用真正 Debug 產物，
負向矩陣用小型 fixture；全部寫入被忽略的 `artifacts/debug-deployment-tests/`，保留失敗證據。
測試不探索或修改使用者安裝目錄，也不清除真正快取。

涵蓋必要來源／安裝逐檔缺失、相依與註冊升級、可選符號、patch 放行、minor／Manifest 阻擋、
白名單外檔案、同目錄拒絕、部署後損毀偵測；預檢失敗須比對整個 fixture 安裝目錄未變。
工具測試不能取代實際部署與 SSMS 重啟驗收；驗收流程見[SQL Memory 驗收](sql-memory-validation.md)。
