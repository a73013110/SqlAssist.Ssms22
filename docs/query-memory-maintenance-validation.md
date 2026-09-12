# Query Memory：有界維護驗收

## B2a 自動驗證

2026-09-12，提交 `ee8800a` 後完整測試 **2879 項成功、0 失敗、0 略過**。
涵蓋：

- v2 → v3 migration：並行開啟、既有 Saved token／內容保留、計量基數、索引／trigger DDL 失敗回復。
- 有界五階段 keyset：Execution、Draft History、Revision、Content、Context；批次上限、游標綁定
  StoreId／政策、跨批重開、取消、失敗回復及 `RequiresAnotherPass`。
- 引用保護：Saved（含未 Pinned）、Pinned History、Manual Snapshot、Session 兩種 head、
  ParentRevision、活動 Recovery；無法回收時回報，不製造懸空外鍵。
- 容量：UTF-16 去重內容位元組的 trigger 計量，與 DB／WAL 實體大小分開回報；期限為 UTC 半開區間，
  null 截止不清理、邊界時間不誤刪，容量超限但有保護根時回報 `CannotReclaimWithinPolicy`。
- 競賽：Saved 更新與維護共用 IMMEDIATE 交易；新引用在下一批重新檢查，失敗不留下 Context／部分列。
- 隔離 Hosting DTO、取消與自我測試報告；自我測試也確認過期 Execution 清理但 head／Recovery 內容保留。

測試完整紀錄保留於 `artifacts/ai-logs/`，不進 Git。文件與 UTF-8／LF 檢查需另行執行。

## B2b-1 筆數配額自動驗證

2026-09-12，提交 `0b45bdd` 後完整測試 **2886 項成功、0 失敗、0 略過**（本批新增 7 項）。涵蓋：

- 只設配額、不設截止時間時回收期限內的超額執行，並一併回收其選取版本與內容。
- 同時間的執行整批保留；Saved 引用的執行即使最舊也不因配額刪除。
- 每 Session auto revision 配額分開計算，較舊 Session 的最新草稿不因另一 Session 較新而淘汰；
  Pinned 草稿保留，ParentRevision 鏈上的版本數不變。
- 配額改變使既有游標失效；v3 → v4 並行升級保留計量、Saved token 與內容，DDL 失敗回復整段升級。
- `EXPLAIN QUERY PLAN` 確認兩個界線查詢分別命中 `IX_Executions_Time` 與 `IX_Revisions_SessionAuto`。
- 自我測試與封裝 probe 通過，報告含「筆數配額」；probe 另在真實 VSIX 與宿主設定下重跑。

## B4 每 Saved 版本配額自動驗證

2026-09-12，提交 `fada9ae` 後完整測試 **2900 項成功、0 失敗、0 略過**（B4 共新增 10 項）。涵蓋：

- 配額保留目前版本與被另一個收藏引用的中段版本，只回收其餘超額版本及其內容。
- 界線逐個收藏解析；同一時間的版本整批保留，快取不把別的收藏算進同一份配額。
- 收藏還在時草稿期限不回收它的 SQL 版本；刪除收藏後標記成為孤立資料，才依草稿期限回收。
- 新配額使既有游標失效；`EXPLAIN QUERY PLAN` 確認界線命中 `IX_Revisions_Saved` 且無暫存排序。
- v4 → v5 並行升級保留計量、Saved token 與內容；`ADD COLUMN` 與索引 DDL 失敗一併回復並可重試。
- 自我測試與封裝 probe 通過，報告含「Saved Query 改 SQL 建立新版本、不進 History，配額只留最新版本」；
  probe 以真實 VSIX 與宿主設定重跑，紀錄在 `artifacts/query-memory-package/874e934c…`。

## 尚未完成或未實機驗證

- 尚未部署或操作真正 SSMS；這些批次只改 Core／Query Memory.Hosting／Sqlite／測試，依部署契約可用
  `Deploy-DebugExtension.ps1`，不需 Install。更新後仍要重跑 Query Memory 儲存自我測試、SSMS 重啟、
  既有功能共存及舊庫 migration。
- 未接宿主排程、活動 Recovery 的跨程序租約判定、VACUUM／checkpoint 或硬磁碟配額。
  `MaxContentBytes` 只回報政策下是否能回收，不提前刪期限內資料。
- 筆數配額的值仍由後續設定批次提供；超額 auto revision 只離開清單投影，版本鏈未回收。
- 未啟用 SQL 擷取、設定與 History UI；自動測試通過不代表產品功能已開啟。

## 手動驗收

1. 關閉 SSMS，建置相同 Configuration；純程式碼改動可執行 `Deploy-DebugExtension.ps1`。
2. 以詳細記錄執行「Query Memory 儲存自我測試」，確認報告含「有界維護續跑、筆數配額、容量量測與
   無法回收時保護 Session head／Recovery」。
3. 重跑一次、操作補全／物件總管／結果格線，再重啟 SSMS 重測；確認更新後 Hosting 建置版本。
4. 若要驗證舊庫，先保留副本並確認 `PRAGMA user_version` 從 1、2 或 3 升到 4、Saved 與內容仍可讀，
   不手改版本或刪庫。失敗保留 `report.txt` 與資料庫供診斷。
