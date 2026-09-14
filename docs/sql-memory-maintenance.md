# SQL Memory：維護與保護根

`IQueryMemoryMaintenanceRepository` 由 SQLite／Hosting 實作，宿主排程與擷取共用生命週期。
維護不另建刪除捷徑；儲存契約見[儲存](sql-memory-storage.md)。

## 期限、配額與引用

政策接受 Draft／Execution／Recovery 的 UTC 半開截止、內容容量上限、執行筆數、每 Session auto revision
及每 Favorite SQL 編輯版本配額。null 表示停用該期限或不限；0 可用於驗證無法回收的情境。
配額取第 N 新時間，與期限取聯集；只刪嚴格更舊的列，同時間整批保留，因此筆數可能略超額。

保護根不因容量壓力而放寬：

- 所有 Favorites 的目前 Revision。
- Session 的文件／執行 head，以及仍被子版本引用的 ParentRevision。
- 有程序心跳租約的 Recovery。

Execution 配額同時限制執行專用版本；auto revision 配額只認非選取的 AutoCheckpoint。
舊 auto revision 可先離開 History，但 ParentRevision 鏈仍保護內容；不能把清單減少當成版本已回收。
不清空 head、不關閉外鍵、不改寫不可變版本，也不刪除 Session／Document／Capture 重送紀錄。

收藏存在時，其 SQL 編輯版本只受每 Favorite 配額處理，草稿期限不套用；目前引用另外受保護。
移除收藏後，失去根的版本才依草稿期限回收。每 Session／Favorite 的界線一批只解析一次並快取，
部分索引 `IX_Revisions_SessionAuto`、`IX_Revisions_Favorite` 及 `IX_Executions_Time` 避免全表排序。

## 有界巡查

每次最多檢查 1～500 個候選，依 Execution、Draft History、Revision、Recovery、Content、Context 六階段
主鍵 keyset 前進。先 LIMIT 候選再查引用，不用帶引用保護的 LIMIT 假裝有界。
Execution／Recovery 一個候選最多刪本體與投影兩列，其餘最多一列；沒有 CASCADE。

每批一個 IMMEDIATE 交易，和收藏寫入序列化：收藏先寫入就保護引用，清理先刪掉則收藏被外鍵拒絕。
引用探測有索引，取消／錯誤回復整批，先前完成的批次保留。
鎖等待、單一同步 SQL 或大型 BLOB 刪除不是硬毫秒／位元組上限。

游標綁 StoreId 與政策，不接受跨庫／跨政策；改批次大小可以續讀。null 才完成一輪，
`RequiresAnotherPass` 表示有刪除，需要另一輪處理新孤立資料。每批讓出執行權，不無界 drain。
回應遺失可重跑同一游標，游標遺失可從頭巡查。跨批不是快照，並行新增的列可能要下一輪才看見。

## 心跳與容量

程序以 MachineName／PID／ProcessStartTime 三元組在 Leases 續一列，Session.LeaseId 是外鍵；
相同三元組重開沿用原租約，識別碼只在交易提交後記住。另以固定 `maintenance` 識別碼作跨程序維護租約。
擷取提交明確帶入心跳目前的租約；交易內租約列已被回收就寫成無租約，下一次心跳重開後再標上，不因外鍵失敗。
心跳每分鐘一次、10 分鐘過期。過期不等於死亡：同機還要確認程序／啟動時間，未知視為存活；
跨機只認過期。交易內重查，避免刪掉已回來續約的租約；自己的及維護租約不在回收候選。
先解除 Session 標記才刪租約，Recovery 仍要過期才回收；失去自己的心跳立即撤回 Recovery 清理授權。

ContentBytes 只計去重後 `2 × Contents.Length`，不含 metadata、索引及 Capture。
DB／WAL 檔案大小分別觀測，非原子快照；WAL 不存在為 0，刪列只釋放可重用頁，不保證縮檔。
未超限為 WithinLimit；尚有巡查／進展為 MoreWorkRequired；整輪無進展且超限為 CannotReclaimWithinPolicy，
不是故障或可任意刪除資料的授權，也不是硬磁碟配額。

## 排程與實體整理

日常期限／配額來自設定，固定倍率 1／2／4 推導保留分級，期限至少一天、配額至少一件。
完整一輪且無法回收才收緊，容量回復則回日常級；只在輪次邊界換級，不跨政策續游標。
每完成一輪都以當下重算整條分級並保留目前級數（級數變少時停在最緊一級），一輪之內沿用同一份政策；
只在日常級重算會讓壓力期間的截止時間凍結，期限清理停止到重啟為止。失去心跳則不等輪次邊界，立即重建並回日常級。

宿主啟動延遲首跑、固定間隔、兩分鐘未編輯可提前（有最小間隔）；一次只跑一個 200 候選批次，
尚有工作只把下一輪排近。背景只做 `wal_checkpoint(TRUNCATE)`，被讀取擋下時回報未截斷而不打斷對方。
`VACUUM` 只供手動整理命令，完成後再截斷 WAL；不改 `auto_vacuum`。
