# Query Memory：有界維護

`IQueryMemoryMaintenanceRepository` 由 SQLite 與隔離 Hosting 共用；不接擷取 writer、
不啟用排程，也不重做 [Saved CRUD](query-memory-saved.md)。驗證見[維護驗收](query-memory-maintenance-validation.md)。

## 政策與保護根

`QueryMemoryMaintenancePolicy` 接受 Draft／Execution 的 UTC 半開截止時間、可空的內容容量上限，
以及 Execution、每 Session auto revision 與每 Saved 版本的可空筆數配額，還有未存檔草稿的截止時間。
null 截止時間停用該類期限清理；null 上限與 null 配額表示不限，0 可用於驗證無法回收情境。
容量上限只用於回報，不授權刪除期限內資料；天數、筆數與設定建議值不寫死在 Domain。

- Saved 引用不論 Pinned 都保護 Revision；Manual Snapshot、Pinned History 也保護版本與歷史。
- Session 的文件／執行 head 不刪除。過期的非受保護 History／Execution 可移除，head 仍可讀。
- Recovery 受 [Session 心跳租約](query-memory-storage.md#session-心跳租約)保護，租約還在就不回收；
  Pinned 的投影也一併擋下草稿本身。
- ParentRevisionId 不改寫。被子版本引用的父版本保留；無根葉節點移除後，再巡一輪回收新孤立父版本。
- 每批以 IMMEDIATE 交易包住候選、引用檢查及刪除，與 Saved 新增／更新序列化；全程啟用外鍵。
  Saved 若先提交就受保護；清理先移除目標時，Saved 寫入由外鍵拒絕且不留下 Context。
- Session、Document、Capture 冪等紀錄不刪除。重送舊 Capture 不會因維護重新產生 History。

上述保留可能使大部分版本鏈無法回收。不能靠清空 head、關閉外鍵或改寫不可變版本達到容量上限。

## 筆數配額

界線是第 N 新的時間，與截止時間取聯集後才是淘汰條件，期限內但超額的列因此可回收。
只刪嚴格更舊的列，同時間的列一併保留，實際筆數可能略多於配額；配額 0 表示全部超額。
配額不放寬任何保護根，也不改變 `CapacityStatus`，該狀態只描述 `MaxContentBytes`。

Execution 配額的界線同時套用執行專用版本，否則配額只會留下無法回收的孤立版本。
每 Session 配額只認 `AutoCheckpoint` 且非選取的版本，不波及關閉、Recovery 或手動快照。
超額的 auto revision 先移出 History 清單投影；版本本身仍受 ParentRevision 鏈保護，
與期限清理同一限制，不能當成版本鏈已回收。

每 Saved 版本配額只認 [Saved SQL 編輯](query-memory-saved.md#sql-編輯)產生的版本，界線含目前版本。
收藏還在時，只有這個配額能回收它的版本，草稿期限不適用；配額為 null 就是不限。
目前版本另受 Saved 引用保護，配額不會把收藏清成沒有 SQL。收藏刪除後標記成為孤立資料，
改依草稿期限回收，不另開刪除路徑。

界線每批解析一次並以 Session 或收藏快取；批次只刪比界線舊的列，最新 N 筆不會在批次內移動。
`IX_Executions_Time`、v4 的 `IX_Revisions_SessionAuto` 與 v5 的 `IX_Revisions_Saved`
使界線只掃描前 N 筆索引項。部分索引要看到常數條件才會命中，`Reason` 因此以常數而非參數
寫入界線查詢，`SavedQueryId` 的界線查詢也明寫 `IS NOT NULL`。

## 工作量、取消與續跑

每次 `MaintainAsync` 最多檢查 1～500 個候選，不是最多刪除 500 筆。
按 Execution、Draft History、Revision、Recovery、Content、Context 六階段，以主鍵 keyset 分批；
先 LIMIT 候選，再逐筆檢查引用，不以帶保護條件的 LIMIT 隱藏全庫掃描。
Execution 與 Recovery 候選各最多刪除本體與投影兩列；其餘最多一列，沒有 CASCADE。
引用與 Pinned 探測均有索引，日常容量讀取不 SUM 全庫，不載入 SQL BLOB。

回傳 Cursor 綁定 StoreId 及政策，允許改批次大小，不能跨庫、跨政策或與其他分頁混用；
階段組成改變時前綴一併換版，舊游標被明確拒絕而不是默默巡錯階段。
Cursor 為 null 才完成一輪；`RequiresAnotherPass` 表示該輪有刪除，應另排下一輪處理新孤立資料。
宿主應在批次間讓出執行權，不在一次工作中無界 drain。
遺失回應可重跑原游標；遺失游標可從頭巡查，不需重建資料庫。

SQLite 在候選讀取間、刪除前及 commit 前檢查取消；取消／例外回復整批，之前成功批次保留。
單次同步 SQL、鎖等待及大型 BLOB 刪除不是硬毫秒／位元組上限；鎖等待沿用 repository busy timeout。
隔離 Hosting 仍只在派送前接受取消，派送後以該批交易結果為準；宿主可在批次間停止。

跨批不是資料庫快照。插入主鍵落在已巡過範圍或引用並行改變時，下次從頭巡查才會看見；
不得把一輪完成當作阻止新資料寫入的配額鎖。

## 容量與 schema 升級

`ContentBytes` 為去重後 `2 × Contents.Length`，只計 UTF-16 SQL，不含 metadata／索引／Capture。
`DatabaseFileBytes`、`WalFileBytes` 為各自的非原子檔案觀測；WAL 消失記為 0。
刪除列只釋放 SQLite 可重用空間，不代表 DB／WAL 已縮小；要縮小得另外做實體整理。

容量結果：未超限為 `WithinLimit`；超限但巡查未完或該輪有進展為 `MoreWorkRequired`；
超限且完整一輪沒有刪除為 `CannotReclaimWithinPolicy`，不是失敗也不是所有資料皆可刪的暗示。
並行寫入可能改變結果；此值只描述已巡查政策下的進展，不承諾硬磁碟配額。

v2 → v3 在原 migration 交易新增引用索引與單列 `StorageUsage`；一次 SUM 建立基數，
Contents INSERT／DELETE／Length UPDATE trigger 在同一交易維護計量，去重與回復不重複計數。
新庫沿用 v1 → … → v6；不改 StoreId 或 Saved 版本 token。升級建索引與初次計量需掃描舊庫，
不受日常候選上限約束，必須留在背景。v3 → v4 只新增上述部分索引，v5 以 `ALTER TABLE ADD COLUMN`
為 Revisions 加上 `SavedQueryId` 標記與部分索引；兩者都不動資料、StoreId 或計量。
標記不設外鍵：加了就得在刪除收藏時連帶刪版本或改寫不可變列。
v6 新增 `Leases` 表並為 Sessions 加上參照它的 `LeaseId`；既有 Session 一律無租約，
升級不追認任何程序還在用它們。舊版程式拒絕較新 schema，不能手改 user_version 降版。

## 壓力分級、排程與實體整理

容量超限且完整一輪回收不到東西，才換下一級收緊後的保留重跑同一條有界清理；分級只能縮短
期限與配額，容量上限是觸發條件而非分級內容，升降級只在一輪邊界發生，游標不跨級沿用。
排程為啟動後延遲首跑、固定間隔與宿主閒置提前（有最小間隔），一次只跑一個有界
`MaintainAsync`，還有工作只把下一輪排近一點。背景實體整理只做 `wal_checkpoint(TRUNCATE)`，
被讀舊快照的連線擋下時回報未截斷而不中斷對方；VACUUM 重建整個資料庫並接著截斷 WAL，
只供手動命令，不轉換 `auto_vacuum`。

配額與分級的值仍由設定批次提供，宿主未給就是不限；隱私設定與 History UI 依[接續](query-memory-handoff.md)另批處理。
