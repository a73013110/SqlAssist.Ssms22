# Query Memory：有界維護驗收

## B2a 自動驗證

2026-09-12，B2a 完成後完整測試 **2879 項成功、0 失敗、0 略過**。
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

## 尚未完成或未實機驗證

- 尚未部署或操作真正 SSMS；本批只改 Core／Query Memory.Hosting／Sqlite／測試，依部署契約可用
  `Deploy-DebugExtension.ps1`，不需 Install。更新後仍要重跑 Query Memory 儲存自我測試、SSMS 重啟、
  既有功能共存及舊庫 migration。
- 未接宿主排程、筆數／每 Session 配額、活動 Recovery 的跨程序租約判定、VACUUM／checkpoint 或硬磁碟配額。
  `MaxContentBytes` 只回報政策下是否能回收，不提前刪期限內資料。
- 未啟用 SQL 擷取、設定、Saved SQL 編輯／搜尋與 History UI；B2a 不代表產品功能已開啟。

## 手動驗收

1. 關閉 SSMS，建置相同 Configuration；純程式碼改動可執行 `Deploy-DebugExtension.ps1`。
2. 以詳細記錄執行「Query Memory 儲存自我測試」，確認報告含「有界維護續跑、容量量測與無法回收時保護
   Session head／Recovery」。
3. 重跑一次、操作補全／物件總管／結果格線，再重啟 SSMS 重測；確認更新後 Hosting 建置版本。
4. 若要驗證舊庫，先保留副本並確認 `PRAGMA user_version` 從 1 或 2 升到 3、Saved 與內容仍可讀，
   不手改版本或刪庫。失敗保留 `report.txt` 與資料庫供診斷。
