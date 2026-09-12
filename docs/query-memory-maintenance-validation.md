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

## B2b-2 維護策略延伸自動驗證

2026-09-12，提交 `df95ab8` 後完整測試 **2940 項成功、0 失敗、0 略過**（本批新增 29 項）。涵蓋：

- 保留分級：只認「完整一輪且回收不到東西」才升級，容量回到上限內直接回到日常級，
  升降級只在一輪邊界發生因此游標不跨級；建構時擋下比前一級寬鬆的期限、配額與不同的容量上限。
- 心跳租約：同三元組重開沿用同一列、跨機器只認過期、同機比對三元組、判斷不出來視為還活著、
  交易內重查使回來續約的租約不被釋放，以及自己的與維護租約永遠不在過期清單與釋放範圍內。
- Recovery：租約還在時期限到了也不回收；`RecoveryBefore` 為 null 時完全不憑年齡刪除；
  釋放租約後連投影與內容一起回收；Pinned 的投影連草稿本身一起擋下。
- 排程：啟動延遲內連閒置都不跑，完成一輪等固定間隔，還有工作只排近一輪，閒置提前有最小間隔。
- 實體整理：checkpoint 截斷 WAL 且不動邏輯容量，被讀舊快照的連線擋下時回報未截斷；
  VACUUM 清掉自由頁、縮小檔案並保留 `user_version`、`application_id` 與外鍵完整性。
- v5 → v6 並行升級保留計量、Saved token 與內容，既有 Session 不被追認成有租約；
  建表、`ADD COLUMN` 與索引 DDL 失敗一併回復並可重試。`EXPLAIN QUERY PLAN` 確認過期查詢命中
  `IX_Leases_Renewed`、釋放命中 `IX_Sessions_Lease`，擁有權探測走主鍵而不需第二個索引。
- 自我測試多一個 repository 生命週期模擬另一個程序回收失效租約，報告含「Session 心跳租約」與
  「回收失效租約後才清除未存檔草稿」；probe 以真實 VSIX 與宿主設定重跑，
  紀錄在 `artifacts/query-memory-package/a8db08cc…`。

自我測試在此批抓到真實缺陷：PID 0 查得到程序卻問不到啟動時間，舊的探測契約會把它判成已結束。

## 尚未完成或未實機驗證

- 尚未部署或操作真正 SSMS；這些批次只改 Core／Query Memory.Hosting／Sqlite／測試，依部署契約可用
  `Deploy-DebugExtension.ps1`，不需 Install。更新後仍要重跑 Query Memory 儲存自我測試、SSMS 重啟、
  既有功能共存及舊庫 migration。
- 排程、分級、租約與整理都只有可測試的核心與儲存層，**尚未接上宿主**：沒有計時器、沒有真正的
  程序探測、沒有 idle 訊號，也沒有人呼叫 checkpoint／VACUUM。`MaxContentBytes` 只回報政策下
  是否能回收，不提前刪期限內資料，也不是硬磁碟配額。
- 筆數配額的值仍由後續設定批次提供；超額 auto revision 只離開清單投影，版本鏈未回收。
- 未啟用 SQL 擷取、設定與 History UI；自動測試通過不代表產品功能已開啟。

## 手動驗收

1. 關閉 SSMS，建置相同 Configuration；純程式碼改動可執行 `Deploy-DebugExtension.ps1`。
2. 以詳細記錄執行「Query Memory 儲存自我測試」，確認報告含「有界維護續跑、筆數配額、容量量測與
   無法回收時保護 Session head／Recovery」、「Session 心跳租約」與「回收失效租約後才清除未存檔草稿」。
3. 重跑一次、操作補全／物件總管／結果格線，再重啟 SSMS 重測；確認更新後 Hosting 建置版本。
4. 若要驗證舊庫，先保留副本並確認 `PRAGMA user_version` 從 1～5 任一版升到 6、Saved 與內容仍可讀，
   不手改版本或刪庫。失敗保留 `report.txt` 與資料庫供診斷。
