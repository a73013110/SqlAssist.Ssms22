# Query Memory：接續與分工

本頁記錄接手順序與工作邊界，不重述架構。每批完成後更新；以目前 Git 和測試校對，不盲信舊進度。

## 從哪裡開始

1. 讀 `CLAUDE.md`、[文件路由](index.md)及本批命中的護欄。
2. 檢查 branch、status、近期 commit；保留未提交變更，不因接手就 checkout／reset。
3. 讀[核心契約](query-memory.md)、[儲存決策](query-memory-storage.md)、[驗收與部署](query-memory-validation.md)。
4. 只定位本批相關型別與測試；先說明現況差異及本批範圍，再實作，不重新設計已完成部分。

原架構附件沒有納入 Git；跨電腦或其他 AI 不應假設能存取 Downloads。
現有決策以版本庫文件為依據；若本批涉及尚未記錄的產品規格，向使用者取得原附件，不自行補猜。

## 可核對的里程碑

| Commit | 已完成 |
|---|---|
| `74b2849` | 純核心內容位址、版本引擎、交易與分頁契約 |
| `d9551f1` | 有界背景寫入器、合併與交易衝突重試 |
| `6859668` | SQLite schema v1、WAL／去重／分頁、隔離層及封裝測試 |
| `dddabee` | SSMS 儲存自我測試命令 |
| `1b7fd2a` | 外部 LoadFrom 載入的代理轉型修正、回歸測試 |
| `0b23c56` | A 批 Debug 部署白名單、完整預檢、SHA-256 驗證與隔離 fixture |
| `52a73ff` | B1：Saved CRUD／scope、GUID CAS、schema v2 migration 與真實引用保護測試 |
| 工作區本批 | B2a：schema v3、政策驅動有界清理、容量計量、游標續跑與引用競賽測試（待提交） |

最近完整測試 2879 項通過（B2a 新增 26 項）；B1／B2a 的封裝與未驗項目見[驗收](query-memory-validation.md)及[維護驗收](query-memory-maintenance-validation.md)。
A 批曾在真實安裝目錄驗證 11 個檔案並清除兩份快取；不能當成 B1 已部署或實機通過。
這不是整個功能完成：正式擷取、設定與 History UI 仍未啟用；其他實機門檻以驗收文件為準。

## A 批已完成

`Deploy-DebugExtension.ps1` 現在只替換白名單內的產品 DLL／PDB 與註冊 JSON；
Query Memory.Hosting／Sqlite 純程式碼改動已用真實安裝目錄成功 Deploy。provider、native、隔離設定、
pkgdef、Manifest 或其他安裝資產變更仍須 Install；完整條件見[Debug 部署完整性](debug-deployment.md)。

## B1 已完成

已沿用原始 SavedQuery 形狀完成既有 Revision 的 CRUD／scope；儲存與隔離 Hosting 共用
`ISavedQueryRepository`，沒有改造背景擷取流程。v1 有資料升級至 v2、並行開啟、失敗回復及
Saved 外鍵保護均有真實 SQLite 測試。自我測試已加入 Saved CRUD／scope；SQL 擷取仍停用。
詳細責任與限制見 [Saved 與維護](query-memory-saved.md)，不可把 metadata CRUD 當成 SQL 編輯流程。

## 建議下一批

**B2a 已完成：有界儲存維護基礎**。`IQueryMemoryMaintenanceRepository` 與 SQLite／隔離 Hosting
共用；v2 → v3 migration 建立 `StorageUsage` trigger 及引用索引。期限清理按五階段 keyset 分批，
游標綁 StoreId／政策，交易內重查引用，取消／失敗回復整批，`RequiresAnotherPass` 支援孤立父版本續跑。
Saved、Pinned、Manual、Recovery、Session head 與 ParentRevision 保護已用真實 SQLite 覆蓋；容量
邏輯位元組與 DB／WAL 大小分開回報。限制與未實機項目見[維護](query-memory-maintenance.md)及[驗收](query-memory-maintenance-validation.md)。
Saved SQL 編輯／建立新版本的交易流程與搜尋仍需後續批次，不自行補猜產品互動規格。

**B2b：維護策略延伸**。待產品確認後再加入宿主排程、Execution／Session 配額、活動 Recovery 租約及實體檔案整理；
不可把 `MaxContentBytes` 回報當成已完成硬容量配額。

**C：擷取、設定與 UI**。待前置契約和實機門檻確認後，依核心文件分批完成。
不要因自我測試 PASS 就啟用實際 SQL 擷取；隱私設定與可見的降級提示仍未完成。

## 每批最低交付

依[開發流程](development.md)跑完整測試；產品／封裝改動另建 VSIX，儲存／載入改動另跑封裝 probe。
文件改動跑文件與文字檢查。說清楚要 Deploy 還是 Install、是否已在實機驗證；不把兩者混為一談。
