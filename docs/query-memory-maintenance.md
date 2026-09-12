# Query Memory：有界維護

B2a 提供 `IQueryMemoryMaintenanceRepository`，SQLite 與隔離 Hosting 共用；不接擷取 writer、
不啟用排程，也不重做 [Saved CRUD](query-memory-saved.md)。驗證見[維護驗收](query-memory-maintenance-validation.md)。

## 政策與保護根

`QueryMemoryMaintenancePolicy` 接受 Draft／Execution 的 UTC 半開截止時間及可空的內容容量上限。
null 截止時間停用該類期限清理；null 上限表示不限，0 可用於驗證無法回收情境。
容量上限只用於回報，不授權刪除期限內資料；天數、筆數配額與設定建議值不寫死在 Domain。

- Saved 引用不論 Pinned 都保護 Revision；Manual Snapshot、Pinned History 也保護版本與歷史。
- Session 的文件／執行 head 不刪除。過期的非受保護 History／Execution 可移除，head 仍可讀。
- Recovery 與其 History 不憑年齡刪除；目前沒有跨程序活動租約，不能將未正常關閉誤判為失效。
- ParentRevisionId 不改寫。被子版本引用的父版本保留；無根葉節點移除後，再巡一輪回收新孤立父版本。
- 每批以 IMMEDIATE 交易包住候選、引用檢查及刪除，與 Saved 新增／更新序列化；全程啟用外鍵。
  Saved 若先提交就受保護；清理先移除目標時，Saved 寫入由外鍵拒絕且不留下 Context。
- Session、Document、Capture 冪等紀錄不刪除。重送舊 Capture 不會因維護重新產生 History。

上述保留可能使大部分版本鏈無法回收。不能靠清空 head、關閉外鍵或改寫不可變版本達到容量上限。

## 工作量、取消與續跑

每次 `MaintainAsync` 最多檢查 1～500 個候選，不是最多刪除 500 筆。
按 Execution、Draft History、Revision、Content、Context 五階段，以主鍵 keyset 分批；
先 LIMIT 候選，再逐筆檢查引用，不以帶保護條件的 LIMIT 隱藏全庫掃描。
一個 Execution 候選最多刪除事件及投影兩列；其餘最多一列，沒有 CASCADE。
引用與 Pinned 探測均有索引，日常容量讀取不 SUM 全庫，不載入 SQL BLOB。

回傳 Cursor 綁定 StoreId 及政策，允許改批次大小，不能跨庫、跨政策或與其他分頁混用。
Cursor 為 null 才完成一輪；`RequiresAnotherPass` 表示該輪有刪除，應另排下一輪處理新孤立資料。
宿主應在批次間讓出執行權，不在一次工作中無界 drain。
遺失回應可重跑原游標；遺失游標可從頭巡查，不需重建資料庫。

SQLite 在候選讀取間、刪除前及 commit 前檢查取消；取消／例外回復整批，之前成功批次保留。
單次同步 SQL、鎖等待及大型 BLOB 刪除不是硬毫秒／位元組上限；鎖等待沿用 repository busy timeout。
隔離 Hosting 仍只在派送前接受取消，派送後以該批交易結果為準；宿主可在批次間停止。

跨批不是資料庫快照。插入主鍵落在已巡過範圍或引用並行改變時，下次從頭巡查才會看見；
不得把一輪完成當作阻止新資料寫入的配額鎖。

## 容量與 schema v3

`ContentBytes` 為去重後 `2 × Contents.Length`，只計 UTF-16 SQL，不含 metadata／索引／Capture。
`DatabaseFileBytes`、`WalFileBytes` 為各自的非原子檔案觀測；WAL 消失記為 0。
刪除列只釋放 SQLite 可重用空間，不代表 DB／WAL 已縮小；本批不執行 VACUUM 或強制 checkpoint。

容量結果：未超限為 `WithinLimit`；超限但巡查未完或該輪有進展為 `MoreWorkRequired`；
超限且完整一輪沒有刪除為 `CannotReclaimWithinPolicy`，不是失敗也不是所有資料皆可刪的暗示。
並行寫入可能改變結果；此值只描述已巡查政策下的進展，不承諾硬磁碟配額。

v2 → v3 在原 migration 交易新增引用索引與單列 `StorageUsage`；一次 SUM 建立基數，
Contents INSERT／DELETE／Length UPDATE trigger 在同一交易維護計量，去重與回復不重複計數。
新庫沿用 v1 → v2 → v3；不改 StoreId 或 Saved 版本 token。升級建索引與初次計量需掃描舊庫，
不受日常候選上限約束，必須留在背景。v2 程式拒絕 v3，不能手改 user_version 降版。

## 後續 B2b

待補 Execution 筆數／每 Session auto revision 配額、宿主排程與跨程序活動 Recovery 判定。
容量壓力下是否提前淘汰期限內版本、版本鏈保留方式及實體檔案整理，須先確認政策，不自行補猜。
Saved SQL 編輯／搜尋、隱私設定與 History UI 仍依[接續](query-memory-handoff.md)另批處理。
