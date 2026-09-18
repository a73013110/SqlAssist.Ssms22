# SQL Memory：維護與保護根

`ISqlMemoryMaintenanceStore` 由 SQLite／Isolation 實作，宿主排程與擷取共用生命週期。
維護不另建刪除捷徑；使用者刪除 History 與維護共用同一份引用清單，儲存契約見[儲存](sql-memory-storage.md)。

## 期限、配額與引用

政策接受 Draft／Execution／Recovery 的 UTC 半開截止、內容容量上限、執行筆數、每 Session auto revision
及每 Favorite 版本配額。null 表示停用該期限或不限；0 可用於驗證無法回收的情境。
配額取第 N 新時間，與期限取聯集；只刪嚴格更舊的列，同時間整批保留，因此筆數可能略超額。

保護根不因容量壓力而放寬：

- 所有 Favorites 的目前 Revision。
- Session 的文件／執行 head，以及仍被子版本引用的 ParentRevision。
- 有程序心跳租約的 Recovery。

Execution 配額同時限制執行專用版本；auto revision 配額只認非選取的 AutoCheckpoint。

Execution 期限與筆數配額以**執行事件**計，不以 History 列計：儲存與巡查的單位都是事件，以列計會讓一個候選
帶出合併列底下無界的執行。[合併列](sql-memory-search.md)跟著剩下的執行走：刪一筆就把次數減一，並沿
`IX_Executions_Entry` 各讀一個索引項重算最後時間、引用版本與 `FirstExecutedAt`；最後一筆被刪才刪投影並釋出內容。
收藏保護的執行留下時，列時間可能退回那一次。畫面上的次數只代表仍保存的執行，列數可能少於配額。
舊 auto revision 可先離開 History，但 ParentRevision 鏈仍保護內容；不能把清單減少當成版本已回收。
不清空 head、不關閉外鍵、不改寫不可變版本，也不刪除 Session／Document／Capture 重送紀錄。

收藏存在時，它自己建立的版本只受每 Favorite 配額處理，草稿期限不套用；目前引用另外受保護。
移除收藏後，失去根的版本才依草稿期限回收。每 Session／Favorite 的界線一批只解析一次並快取，
部分索引 `IX_Revisions_SessionAuto`、`IX_Revisions_Favorite` 及 `IX_Executions_Time`／`IX_Executions_Entry` 避免全表排序。

## 有界巡查

每批最多 1～500 個候選單位，一個 IMMEDIATE 交易，和收藏寫入序列化。先 LIMIT 候選再重查保護根；
沒有 CASCADE。一個候選最多刪本體與投影兩列，每列再帶出一個 Content。

索引巡查只讀可能過期或超額的列，以 (時間, 鍵) keyset 前進並 `INDEXED BY`：

| 階段 | 候選 | 索引 |
|---|---|---|
| Execution | 早於期限與配額界線的聯集 | `IX_Executions_Time` |
| Draft History | 逐 Session，組內早於草稿期限或 auto 配額界線 | `IX_History_SessionDrafts` |
| 選取版本 | 早於執行界線 | `IX_Revisions_SelectionTime` |
| 收藏版本 | 逐收藏，存在用配額界線、已移除用草稿期限 | `IX_Revisions_Favorite` |
| Recovery | 早於回復內容期限 | `IX_Recovery_Time` |

分組探測算一個單位。擷取的文件版本不是候選：新版本一律接在 head 之後，舊版本必有子版本。
本批刪除列引用的 ContentId 在批次結束前重查引用後刪除，容量同批下降。

完整掃描先跑同樣階段，再逐主鍵巡 Revision、Content，承接不是由刪除產生的孤立資料
（例如收藏換掉目前版本後不再被引用的舊版本）。每 24 輪索引巡查一次，升級分級前也先跑一次。

取消／錯誤回復整批，先前批次保留；鎖等待、單一 SQL 或大型 BLOB 刪除不是硬上限。批次失敗以 `SqlMemoryRuntime.MaintenanceFailed` 交給宿主，除了紀錄檔另送一則通知（`SqlMemory`／`Ambient`／`Debug`，狀態為失敗）；擷取不跟著停，下一輪重跑同一個游標。
游標綁 StoreId、政策與掃描方式，改批次大小可續讀；null 才完成一輪，`RequiresAnotherPass`
表示有刪除。跨批不是快照，並行寫入的列可能要下一輪才看見。

## 共用輪次

`MaintenanceState` 單列保存版本、計畫指紋、輪次起點、級數、心跳授權、掃描方式、距完整掃描輪數、
游標與最後結論。帶 Claim 的批次在同一交易確認維護租約屬於自己、版本相符才寫回，否則整批回復為
Conflict。重啟或換程序讀狀態接續，同一輪以輪次起點換算政策，游標才對得上。
計畫指紋不同就從日常級重開；設定不一致的程序會輪流重開。起點晚於現在（時鐘回撥）或游標被拒時，保留級數從當下重開。

## 心跳與容量

程序以 MachineName／PID／ProcessStartTime 三元組在 Leases 續一列，Session.LeaseId 是外鍵；
相同三元組重開沿用原租約。另以固定 `maintenance` 識別碼作跨程序維護租約，卸載時交回。
租約契約無狀態：續約、提交與讀過期租約都明確帶識別碼，儲存層不記得「自己的租約」。
交易內租約列已被回收就寫成無租約，下一次心跳重開後再標上，不因外鍵失敗。
心跳每分鐘一次、10 分鐘過期。過期不等於死亡：同機還要確認程序／啟動時間，未知視為存活；
跨機只認過期。交易內重查，避免刪掉已回來續約的租約；維護租約與呼叫端排除的自己租約不在回收候選。
先解除 Session 標記才刪租約，Recovery 仍要過期才回收；失去自己的心跳立即撤回 Recovery 清理授權。

ContentBytes 只計去重後 `2 × Contents.Length`，不含 metadata、索引及 Capture。
DB／WAL 檔案大小分別觀測，非原子快照；WAL 不存在為 0，刪列只釋放可重用頁，不保證縮檔。
未超限為 WithinLimit；尚有巡查／進展為 MoreWorkRequired；整輪無進展且超限為 CannotReclaimWithinPolicy，
不是故障或可任意刪除資料的授權，也不是硬磁碟配額。

## 排程與吞吐量

日常期限／配額來自設定，固定倍率 1／2／4 推導分級，期限至少一天、配額至少一件。只在輪次邊界換級：
容量回復回日常級；索引巡查整輪無法回收先完整掃描，仍無法回收才收緊。每開新輪次都以當下換算並保留
級數，壓力期間截止時間不凍結；失去心跳則立即放棄該輪並回日常級。

宿主啟動延遲 3 分鐘、間隔預設 60 分鐘，兩分鐘未編輯可提前（最小間隔為四分之一間隔）。
尚有工作時 30 秒後跑下一批，一次一個 200 單位批次。改設定只換計畫與間隔，不重算啟動延遲；
正常卸載排空 writer 後等進行中的批次離開，再交回維護租約。

預設設定（執行上限 1 萬、穩態約 4 萬列）的估算：

- 索引巡查一輪 ≈ 上輪後過期／超額列＋30 天內有草稿的 Session＋有自己版本的收藏；
  每小時一輪通常 1～2 批、1 分鐘內。
- 執行上限由 1 萬降到 1 千：約 9 千執行＋同量選取版本，90 批約 45 分鐘。
- 完整掃描每 1 萬列 50 批約 25 分鐘，4 萬列約 100 分鐘。
- 8 小時工作日約 8 輪；遇上一次完整掃描仍有 6 輪索引巡查。

背景只做 `wal_checkpoint(TRUNCATE)`，被讀取擋下時回報未截斷而不打斷對方。
`VACUUM` 只供手動整理命令，完成後再截斷 WAL；不改 `auto_vacuum`。
使用者主動的立即維護、清除紀錄與備份見[用量與清理](sql-memory-usage.md)。
