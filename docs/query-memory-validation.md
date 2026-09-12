# Query Memory：SSMS 實機驗收

自我測試是[SQLite 儲存](query-memory-storage.md)的載入門檻，不代表 History 功能已啟用。
命令只在開啟 `sqlAssist.diagnostics.verboseLogging` 後出現，不依賴目前查詢或連線。

## 已回報的實機結果

2026-09-12 06:35:05 UTC，使用者回報真正 SSMS x64 程序內的自我測試 PASS（1027 ms）。
建置為 `0.17.15+1b7fd2a9eb`，SQLite 3.53.4；通過寫入／重送、隔離層卸載、重新開啟、
全文還原、檔案釋放及宿主 AppDomain 未新增 provider。未提供 SSMS 完整產品版號。
這是使用者提供的驗收證據，不是代理自行操作 SSMS；尚未回報既有功能共存、重啟後再次測試與解除安裝。

## A 批 Debug 部署驗證

2026-09-12，代理在使用者關閉 SSMS 後，以最新 Debug 輸出執行 `Deploy-DebugExtension.ps1 -SkipBuild`。
真實安裝目錄成功更新 11 個白名單檔案，逐一完成 SHA-256 驗證並清除 ComponentModel／Unified Settings
快取；沒有複製白名單外輸出。這證明 Query Memory.Hosting／Sqlite 純程式碼改動可走 Deploy，
不代表 SSMS 重新啟動、既有功能共存或本批後的儲存自我測試已完成。

## B1 自動驗證與待驗

2026-09-12，產品提交 `52a73ff`：完整測試 2853 項成功、0 失敗、0 略過（較 A 批新增 27 項）。
涵蓋有資料的 v1 → v2 migration、並行開啟、DDL 失敗回復、Saved CRUD／scope／版本衝突、
外鍵引用保護、Recovery 替換／Close 保留 Saved 內容，以及 scope 分頁索引不做暫存排序。
這不是容量清理的驗收；完整邊界見 [Saved 與維護](query-memory-saved.md)。

Release／Debug VSIX 建置及 38 檔封裝檢查通過；Debug 部署隔離 fixture 59 項通過。
Release 封裝 probe 通過標準／外部 LoadFrom 載入、新版自我測試、雙程序 40 次執行與負向案例。
封裝 Hosting 建置為 `0.17.17+52a73ff778`，紀錄保存在
`artifacts/query-memory-package/b7c3f586217a4facb6d54b6ecd262db5/`；
部署 fixture 為 `artifacts/debug-deployment-tests/da1b325404744169b817adb036772afb/`。
完整測試紀錄為 `artifacts/ai-logs/20260912-162513-c3a74a0839d94888918c1592c639d5d3/`。
這些本機產物不隨 Git 共用；文件與 UTF-8／LF 檢查亦已通過。

本批僅修改產品程式碼與測試，**應使用 Deploy**；schema migration 隨程式執行，不是安裝資產。
未修改 provider、native、隔離 config、命令表或 Manifest。關閉 SSMS 後執行
`tools/Deploy-DebugExtension.ps1`，再依下列步驟測試；尚未代為部署或操作 SSMS。
首次安裝或預檢指出安裝資產不相容仍須 Install，不能繞過預檢。

本批仍待實機：更新後自我測試、SSMS 重啟／既有功能共存、真正舊庫 migration 與解除安裝。
舊版自我測試 PASS 不涵蓋本批；報告應含「Saved Query CRUD、scope 分頁、搜尋、版本衝突與
刪除後歷史保留」。仍未啟用 SQL 擷取，維護排程／完整容量配額、Saved SQL 編輯與 UI 尚未完成。

B2a 與 B2b-1 的自動驗證見[維護驗收](query-memory-maintenance-validation.md)，B3 Saved 搜尋見
[Saved](query-memory-saved.md#自動驗證)；本頁保留 SSMS 安裝／部署生命週期門檻。
B2a 之後的批次同樣只改程式碼與測試，可 Deploy，但都尚未部署或操作 SSMS，
不能把自動測試通過當成實機通過。

## 更新後如何測試

| 變更 | 部署方式 |
|---|---|
| 只改白名單內的 Core／Metadata／Ssms22／Query Memory.Hosting／Sqlite 程式碼 | 可用 `Deploy-DebugExtension.ps1`；仍須先關閉 SSMS |
| `SqlAssist.registration.json` 或可選 PDB | 可用 `Deploy-DebugExtension.ps1`；部署後會清快取 |
| 新增／升級 provider、native DLL、隔離 config 或封裝檔案 | 完整建置、封裝驗證、Install、自我測試 |
| 命令表、pkgdef（僅 CacheTag 變動除外）、Manifest 或 major.minor 改變 | Install；不能用清快取替代重新註冊 |
| 只有文件 | 不需部署 |

Debug 腳本會在完整預檢與 hash 驗證後複製允許替換的檔案並清除定義快取；安裝資產變更不會部分覆寫。
參數與版號規則見[發布與安裝](release.md)。
Install 不會建置；先產生同一 Configuration 的 VSIX，再安裝。Deploy 預設會建置 Debug。
日常 Debug 驗證不能取代發布前對真正 VSIX 的安裝與封裝驗證。

儲存邏輯變更跑完整測試及封裝測試，再在 SSMS 自我測試；其他功能變更另測該功能。
自我測試報告不能證明尚未實作的 retention、Saved SQL 編輯或擷取接線正確。

## 操作

1. 儲存工作並關閉所有 SSMS 視窗，使用 `tools/Install-Extension.ps1` 安裝本次建置。
   首次安裝或命令資源有變更時必須 Install；本批純程式碼可 Deploy，不能以 Deploy 取代命令註冊，詳見[安裝](release.md#安裝)。
2. 啟動 SSMS，在「工具 → SqlAssist → 設定」開啟詳細記錄。
3. 執行「工具 → SqlAssist → Query Memory 儲存自我測試…」。
   狀態列會顯示執行中，同一程序不接受重複執行；完成後原生訊息框顯示結果與報告路徑。
4. 第一次通過後，再跑一次；確認兩次皆通過，打字、補全、物件總管與既有 SQLite 相依功能正常。
   可對測試用 SQL Server 執行 `SELECT 1;`，確認執行與結果格線未受影響；自我測試本身不會執行 SQL。
5. 關閉並重啟 SSMS，再測一次。也可先操作平常使用的宿主功能，再跑自我測試，檢查載入順序差異。
6. 若要驗證解除安裝，先儲存工作並關閉 SSMS，依[解除安裝](release.md#解除安裝)操作。
   重新開啟確認既有功能正常，再重新安裝；不需刪除任何使用者設定或資料。

請回報 SSMS／SqlAssist 版本、成功或失敗、報告與異常步驟；不需要提供業務 SQL 或連線字串。
報告含本機目錄與程序資訊，分享前可遮蔽使用者名稱等私人路徑。
報告的「Hosting 建置」與「Hosting 路徑」可確認重裝後使用的是新版本，而非僅看固定的 AssemblyVersion。

## 自動驗證範圍

`QueryMemory.Hosting/QueryMemoryStorageSelfTest` 是 SSMS 命令及封裝 probe 的唯一實作：

- 背景建立專用資料庫，保存 21 次內建圖書館範例 SQL，驗證冪等重送不重複新增。
- 卸載隔離 AppDomain，再重新開啟，驗證筆數、內容位址共用與全文還原。
- 驗證 Saved CRUD、scope 分頁與過期版本拒絕；刪除 Saved 後 History 內容仍可讀。
- 再次卸載，獨占開啟資料庫，確認檔案控制代碼已釋放。
- 比對宿主 AppDomain 前後的 provider 組件；新增 provider 即回報失敗。
- 報告逐步落盤，失敗保留最後成功步驟與例外；取消或失敗不宣稱通過。

每次測試資料存於 `%LOCALAPPDATA%\SqlAssist.Ssms22\QueryMemorySelfTest\<唯一識別碼>`。
`report.txt` 與 `self-test.db` 都是測試產物，不含使用者 SQL，測試不會覆寫既有報告或資料庫。
為保留失敗證據，不自動清除；驗收完可在 SSMS 關閉後自行刪除本次唯一識別碼目錄。

**PASS 不代表** native DLL 已從程序卸載或所有宿主功能均相容，也不涵蓋完整安裝生命週期。
未回報項目仍須上述人工操作確認；在此之前維持 SQL 擷取停用。
