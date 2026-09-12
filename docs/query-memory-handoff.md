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
| `ee8800a` | B2a：schema v3、政策驅動有界清理、容量計量、游標續跑與引用競賽測試 |
| `2e8a5e5` | B2b-1：政策驅動筆數配額、schema v4 部分索引、自我測試與封裝 probe 涵蓋 |
| `2e639d8` | B3：Saved 名稱／說明／SQL 全文搜尋，與 History 共用字面搜尋條件 |
| `fada9ae` | B4：Saved SQL 編輯交易、schema v5 版本標記與每 Saved 版本配額 |

最近完整測試 2900 項通過（B4 新增 10 項）；封裝與未驗項目見[驗收](query-memory-validation.md)及[維護驗收](query-memory-maintenance-validation.md)。
A 批曾在真實安裝目錄驗證 11 個檔案並清除兩份快取；不能當成 B1 已部署或實機通過。
這不是整個功能完成：正式擷取、設定與 History UI 仍未啟用；其他實機門檻以驗收文件為準。

## A 批已完成

`Deploy-DebugExtension.ps1` 現在只替換白名單內的產品 DLL／PDB 與註冊 JSON；
Query Memory.Hosting／Sqlite 純程式碼改動已用真實安裝目錄成功 Deploy。provider、native、隔離設定、
pkgdef、Manifest 或其他安裝資產變更仍須 Install；完整條件見[Debug 部署完整性](debug-deployment.md)。

## B1～B3 已完成的範圍

契約與限制在主題文件，這裡只列邊界，不重述細節；四批都沒有啟用 SQL 擷取。
見 [Saved](query-memory-saved.md)與[有界維護](query-memory-maintenance.md)。

- **B1 Saved CRUD／scope**：沿用原始 `SavedQuery` 形狀，儲存與隔離 Hosting 共用
  `ISavedQueryRepository`，沒有改造背景擷取流程。metadata CRUD 不是 SQL 編輯流程。
- **B2a 有界維護**：五階段 keyset、游標綁 StoreId／政策、交易內重查引用、`RequiresAnotherPass`
  續跑，以及與容量量測分離的保護根。
- **B2b-1 筆數配額**：Execution 與每 Session auto revision 的界線取第 N 新的時間並與截止時間取
  聯集；超額 auto revision 只離開 History 清單投影，版本鏈保留限制與期限清理相同。
- **B3 Saved 搜尋**：scope 之上的字面篩選，命中名稱、說明或目前版本 SQL 全文即納入，與 History
  共用同一個條件，沒有改 schema 或加索引。UI 可見範圍合併留給 UI 批。

## B4 Saved SQL 編輯已完成

`EditSavedQuerySqlAsync` 在同一交易新增 `SavedQueryEdit` 版本並換 `CurrentRevisionId` 與版本 token；
不進 History、不建 Session、不動 head。schema v5 以 `ADD COLUMN` 加上 `SavedQueryId` 標記與部分索引，
`ParentRevisionId` 留空，否則舊版本會被子版本永久保護而回收不到。
每 Saved 版本配額走既有五階段清理，保留目前版本與其他 Saved 引用；收藏刪除後才回到草稿期限。
詳細契約見 [Saved](query-memory-saved.md#sql-編輯)與[有界維護](query-memory-maintenance.md)；配額值仍留給設定批次。
版本清單與還原 API 不在本批，屬 UI 批。

## 下一批：已裁決的語意

2026-09-12 與使用者確認，以下不再是待補猜的規格；實際數值仍由設定批次提供。

**B2b-2 維護策略延伸**：

- 容量超限時把截止時間與配額分級收緊後重跑同一條有界清理，不新增刪除路徑，保護根不放寬。
- Session 加 MachineName／ProcessId／ProcessStartTime／RenewedAt 心跳；心跳過期且同機同三元組
  程序不存在，才可回收 Recovery，跨機器只認過期。同一張表兼作跨程序維護 lease。
- 排程為啟動後延遲首跑、固定間隔與宿主 idle，每次只跑一個有界 `MaintainAsync`；
  `RequiresAnotherPass` 只排下一輪，不在一次工作中 drain。
- 實體整理只做背景 `wal_checkpoint(TRUNCATE)`；完整 VACUUM 是設定頁的手動命令，
  `auto_vacuum` 轉換留到真有需求再開 schema。

**C 批**：設定先、擷取次之，兩者都不等 UI。擷取預設關閉、由設定開啟，不因自我測試 PASS 就啟用。

## UI 由外部實作

History／Saved UI 不在本倉庫排程內，由使用者另行指派；本倉庫仍須維持可對接的契約，不得放寬：
repository 的上限與 opaque keyset 游標、240 字元列表預覽與延遲全文、Saved scope 不合併父層。
接線與自製視窗另受[平台](rules-platform.md)與 [UI 準則](ui-guidelines.md)約束，不另造通用視窗框架。

## 每批最低交付

依[開發流程](development.md)跑完整測試；產品／封裝改動另建 VSIX，儲存／載入改動另跑封裝 probe。
文件改動跑文件與文字檢查。說清楚要 Deploy 還是 Install、是否已在實機驗證；不把兩者混為一談。
